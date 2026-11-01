using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ObenseuerRecipeMarker;

/// <summary>
/// Cached FieldInfo handles shared by the Harmony patches below.
/// </summary>
public static partial class Patches
{
    internal static readonly FieldInfo RecipeButtonsField =
        AccessTools.Field(typeof(RecipeListUI), "recipeButtons");

    internal static readonly FieldInfo ItemDataMarkedField =
        AccessTools.Field(typeof(ItemData), "marked");
}

/// <summary>
/// RecipeListUI.CreateButton postfix — attach a star to every recipe button
/// the moment it is created. Covers executable, missing-ingredient and
/// disabled recipes, and survives button list rebuilds.
/// </summary>
[HarmonyPatch(typeof(RecipeListUI), nameof(RecipeListUI.CreateButton))]
public static class Patch_RecipeListUI_CreateButton
{
    private static void Postfix(RecipeListUI __instance)
    {
        try
        {
            var buttons = Patches.RecipeButtonsField.GetValue(__instance) as List<GameObject>;
            if (buttons == null || buttons.Count == 0) return;

            GameObject go = buttons[buttons.Count - 1];
            if (go == null) return;

            var recipeButton = go.GetComponent<RecipeButton>();
            if (recipeButton == null || recipeButton.recipe == null) return;

            RecipeMarkerStar.Attach(go, recipeButton.recipe);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] CreateButton postfix: {ex.Message}");
        }
    }
}

/// <summary>
/// Runtime mount for the worktable blueprint grid star patch.
///
/// v0.2.5 change: the worktable target is no longer resolved by a
/// [HarmonyPatch] TargetMethod during Awake PatchAll. On the user's install the
/// exact methods exist in the live Assembly-CSharp.dll (a decompiler confirms
/// both), yet Harmony's bulk method resolution still reported "not found" and
/// Prepare() logged a one-shot CRITICAL before the patch class was skipped
/// forever. The worktable mount is therefore a plain static method that runs
/// at startup (right after PatchAll) and again on every craft-menu open:
///   1. BuildingSystemSlot.UpdateBuildSlot(BuildingSystem.FurnitureInfo) — exact.
///   2. FurnitureShopPanelUI.UpdateSlots(List&lt;FurnitureInfo&gt;, bool) — exact.
///   3. Loose scan by method name + parameter type name as a last resort.
/// The first candidate that resolves wins and is mounted with
/// Harmony.AddPostfix at runtime; a failed attempt only logs Debug and is
/// retried on the next open, a successful mount logs Info once with the
/// resolved signature. This design never emits a single CRITICAL and then
/// gives up.
///
/// The single Postfix handles both mount points by checking the __instance
/// type. Slot matching never depends on the runtime subclass: the only real
/// signal that a slot represents a furniture recipe is buildObject.taskItem
/// being a TaskItemBlueprint. Player-side (inventory) slots are skipped
/// explicitly. AttachInternal deduplicates on the target GameObject, so
/// re-runs (category switches, panel rebuilds) never stack duplicate stars.
/// </summary>
public static class WorktableMount
{
    public static bool Mounted { get; private set; }

    private static int _attempts;

    /// <summary>
    /// Fast path: called once after PatchAll at startup. When the target is
    /// already resolvable (the normal case) the worktable patch is live before
    /// the player ever opens the craft menu.
    /// </summary>
    public static void TryMount()
    {
        if (Mounted) return;
        _attempts++;

        MethodBase target = ResolveMountPoint();
        if (target == null)
        {
            Plugin.LogDebug($"[RecipeMarker] Worktable mount attempt #{_attempts}: no target method found yet (BuildingSystemSlot={typeof(BuildingSystemSlot).FullName}, FurnitureShopPanelUI={typeof(FurnitureShopPanelUI).FullName}); retrying on next craft-menu open.");
            return;
        }

        try
        {
            Plugin.HarmonyInstance.Patch(
                target,
                postfix: new HarmonyMethod(typeof(WorktableMount), nameof(WorktableMount.Postfix)));
            Mounted = true;
            Plugin.LogInfo($"[RecipeMarker] Worktable patch mounted (attempt #{_attempts}): {target.DeclaringType?.Name}.{target.Name}({string.Join(", ", Array.ConvertAll(target.GetParameters(), p => p.ParameterType.Name))})");
        }
        catch (Exception ex)
        {
            Plugin.LogDebug($"[RecipeMarker] Worktable mount attempt #{_attempts} failed: {ex.Message}; retrying on next craft-menu open.");
        }
    }

    /// <summary>
    /// Called from the OpenMenu postfix on every craft-menu open. Retries the
    /// deferred mount and, once mounted, re-applies stars to the freshly built
    /// panel (the vanilla fill already ran before this postfix runs).
    /// </summary>
    public static void OnCraftingMenuOpened(ShopBaseUI shop)
    {
        TryMount();
        if (!Mounted || shop == null || shop.shopPanel == null) return;
        PatchPanelSlots(shop.shopPanel);
    }

    /// <summary>
    /// Ordered mount-point resolution against the live game assembly:
    ///   1. BuildingSystemSlot.UpdateBuildSlot(BuildingSystem.FurnitureInfo) — exact.
    ///   2. FurnitureShopPanelUI.UpdateSlots(List&lt;FurnitureInfo&gt;, bool) — exact.
    ///   3/4. Loose scan by method name + parameter type name, so a game update
    ///        that moves FurnitureInfo (or changes a signature) still mounts
    ///        instead of failing the whole patch.
    /// The first candidate that resolves wins; every candidate failing yields
    /// null, which the caller treats as a deferred retry (Debug log) rather
    /// than a one-shot CRITICAL.
    /// </summary>
    private static MethodBase ResolveMountPoint()
    {
        MethodBase candidate = AccessTools.DeclaredMethod(typeof(BuildingSystemSlot),
            nameof(BuildingSystemSlot.UpdateBuildSlot),
            new[] { typeof(BuildingSystem.FurnitureInfo) });
        if (candidate != null) return candidate;

        candidate = AccessTools.DeclaredMethod(typeof(FurnitureShopPanelUI),
            nameof(FurnitureShopPanelUI.UpdateSlots),
            new[] { typeof(List<BuildingSystem.FurnitureInfo>), typeof(bool) });
        if (candidate != null) return candidate;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var m in typeof(BuildingSystemSlot).GetMethods(flags))
        {
            if (m.Name != nameof(BuildingSystemSlot.UpdateBuildSlot)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType.Name.Contains("FurnitureInfo"))
            {
                Plugin.LogInfo($"[RecipeMarker] UpdateBuildSlot loose-scanned mount: {m.DeclaringType?.Name}.{m.Name}({ps[0].ParameterType.FullName})");
                return m;
            }
        }

        foreach (var m in typeof(FurnitureShopPanelUI).GetMethods(flags))
        {
            if (m.Name != nameof(FurnitureShopPanelUI.UpdateSlots)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType.Name.Contains("FurnitureInfo"))
            {
                Plugin.LogInfo($"[RecipeMarker] UpdateSlots loose-scanned mount: {m.DeclaringType?.Name}.{m.Name}({ps[0].ParameterType.FullName}, {ps[1].ParameterType.FullName})");
                return m;
            }
        }

        return null;
    }

    private static void Postfix(object __instance)
    {
        try
        {
            if (__instance == null) return;

            if (__instance is FurnitureShopPanelUI panel)
            {
                PatchPanelSlots(panel);
                return;
            }

            if (__instance is BuildingSystemSlot slot)
            {
                PatchSlot(slot);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] UpdateBuildSlot postfix: {ex.Message}");
        }
    }

    private static void PatchPanelSlots(FurnitureShopPanelUI panel)
    {
        var slots = panel.slots;
        if (slots == null || slots.Count == 0) return;

        int attached = 0;
        foreach (var slot in slots)
        {
            if (slot == null) continue;
            if (slot.shopSlotType == BuildingSystemShopSlot.ShopSlotType.PlayerSlot) continue;
            if (slot.buildObject?.taskItem is TaskItemBlueprint blueprint)
            {
                RecipeMarkerStar.AttachBlueprint(slot.gameObject, blueprint, 18f);
                attached++;
            }
        }
        Plugin.LogDebug($"[RecipeMarker] UpdateSlots postfix: {attached}/{slots.Count} worktable slots got stars (panel={panel.name}).");
    }

    private static void PatchSlot(BuildingSystemSlot slot)
    {
        var buildObject = slot.buildObject;
        if (buildObject == null) return;

        string slotTypeName = slot.GetType().Name;
        string shopSlotTypeName = slot is BuildingSystemShopSlot shopSlot ? shopSlot.shopSlotType.ToString() : "N/A";
        string taskItemTypeName = buildObject.taskItem == null ? "null" : buildObject.taskItem.GetType().Name;

        // Player inventory side (playerPanel) — vanilla gold frame handles it.
        if (slot is BuildingSystemShopSlot playerSide &&
            playerSide.shopSlotType == BuildingSystemShopSlot.ShopSlotType.PlayerSlot)
        {
            Plugin.LogDebug($"[RecipeMarker] UpdateBuildSlot(FurnitureInfo) skipped PlayerSlot: go={slot.gameObject.name}, taskItem={taskItemTypeName}");
            return;
        }

        if (buildObject.taskItem is TaskItemBlueprint blueprint)
        {
            RecipeMarkerStar.AttachBlueprint(slot.gameObject, blueprint, 18f);
            Plugin.LogDebug($"[RecipeMarker] Star attached: go={slot.gameObject.name}, slotType={slotTypeName}, shopSlotType={shopSlotTypeName}, blueprint={blueprint.Id}, totalStars={RecipeMarkerStar.All.Count}");
        }
        else
        {
            Plugin.LogDebug($"[RecipeMarker] UpdateBuildSlot(FurnitureInfo) no blueprint: go={slot.gameObject.name}, slotType={slotTypeName}, shopSlotType={shopSlotTypeName}, taskItem={taskItemTypeName}");
        }
    }
}

/// <summary>
/// ShopBaseUI.OpenMenu(BaseCrafting) postfix — every time a crafting panel
/// (worktable) opens, give the deferred worktable mount another chance and
/// re-apply stars to the freshly built panel. This is the high-level entry
/// point that stays stable across game updates (verified against the live
/// Assembly-CSharp.dll, 0.5.17 build 26349752).
/// </summary>
[HarmonyPatch(typeof(ShopBaseUI), nameof(ShopBaseUI.OpenMenu), new[] { typeof(BaseCrafting) })]
public static class Patch_ShopBaseUI_OpenMenu
{
    private static void Postfix(ShopBaseUI __instance)
    {
        try
        {
            WorktableMount.OnCraftingMenuOpened(__instance);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] OpenMenu postfix: {ex.Message}");
        }
    }
}

/// <summary>
/// TaskItemUI.SetItem postfix — attach a star to every recipe/blueprint icon
/// row in the main-menu inventory's right-hand Recipes gallery (and Blueprints
/// category). ItemsUIPanel.AddItem feeds ItemListScrollCellView.PopulateItemRow,
/// which instantiates taskItemPrefab and calls TaskItemUI.SetItem. Recipe rows
/// carry a TaskItem whose id is "Recipe_{Recipe.ID}" (created by
/// RecipeController.CreateTaskItem), so the recipe is resolved back through
/// RecipeDatabase; TaskItemBlueprint rows map to the worktable blueprint grid.
/// </summary>
[HarmonyPatch(typeof(TaskItemUI), nameof(TaskItemUI.SetItem))]
public static class Patch_TaskItemUI_SetItem
{
    private const string RecipeIdPrefix = "Recipe_";

    private static void Postfix(TaskItemUI __instance, TaskItem taskItem)
    {
        try
        {
            if (__instance == null || taskItem == null) return;

            if (taskItem is TaskItemBlueprint blueprint)
            {
                RecipeMarkerStar.AttachBlueprint(__instance.gameObject, blueprint, 18f);
                // Info-level diagnostic so gallery rows can be verified in the
                // shipped log: the blueprint's stable Id (with the raw id and
                // item name for comparison) and its current marked state.
                Plugin.LogInfo($"[RecipeMarker] Blueprint gallery row star: name='{blueprint.itemName}', id='{blueprint.Id}' (raw='{blueprint.id}'), marked={RecipeMarkerManager.IsBlueprintMarked(blueprint.Id)}");
                return;
            }

            if (string.IsNullOrEmpty(taskItem.id) ||
                !taskItem.id.StartsWith(RecipeIdPrefix, StringComparison.Ordinal) ||
                !int.TryParse(taskItem.id.Substring(RecipeIdPrefix.Length),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int recipeId))
            {
                return;
            }

            Recipe recipe = RecipeDatabase.instance?.FetchRecipesById(recipeId);
            if (recipe == null) return;
            RecipeMarkerStar.AttachRecipe(__instance.gameObject, recipe, 18f);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] SetItem postfix: {ex.Message}");
        }
    }
}

/// <summary>
/// ItemData.UpdateItem postfix — force the vanilla "marked" gold frame on
/// inventory slots whose item is an input of a marked recipe. Unmarking clears
/// the frame automatically: the vanilla method resets it to false and our
/// patch only ever turns it on.
/// </summary>
[HarmonyPatch(typeof(ItemData), nameof(ItemData.UpdateItem))]
public static class Patch_ItemData_UpdateItem
{
    private static void Postfix(ItemData __instance)
    {
        try
        {
            if (__instance == null || __instance.item == null) return;
            if (!RecipeMarkerManager.IsMarkedInputItem(__instance.item.ID)) return;

            var markedGo = Patches.ItemDataMarkedField.GetValue(__instance) as GameObject;
            if (markedGo != null && !markedGo.activeSelf)
            {
                markedGo.SetActive(true);
            }
        }
        catch
        {
            // Never break vanilla inventory updates because of the mod.
        }
    }
}
