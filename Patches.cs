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
/// Worktable (FurnitureCrafting) blueprint grid star patch.
///
/// Mount point resolution, in order of preference:
///   1. BuildingSystemSlot.UpdateBuildSlot(BuildingSystem.FurnitureInfo) — the
///      slot-level fill used by FurnitureShopPanelUI.AddBuildSlot (verified
///      against the live game Assembly-CSharp.dll).
///   2. FurnitureShopPanelUI.UpdateSlots(List&lt;FurnitureInfo&gt;, bool) — the
///      panel-level rebuild that instantiates every BuildingSystemShopSlot and
///      fills its public `slots` list.
/// At runtime the first candidate that resolves wins; the single Postfix
/// handles both mount points by checking the __instance type. If every
/// candidate is missing, Prepare() logs a loud CRITICAL and skips this patch
/// without affecting the other patches (no silent failure).
///
/// Slot matching never depends on the runtime subclass: the only real signal
/// that a slot represents a furniture recipe is buildObject.taskItem being a
/// TaskItemBlueprint (the worktable blueprint list is populated by
/// TaskItemsManager with exactly that task item type). Player-side
/// (inventory) slots are skipped explicitly — the vanilla "marked" gold frame
/// already covers marking there. RecipeMarkerStar.AttachInternal deduplicates
/// on the target GameObject, so re-runs (category switches, panel rebuilds)
/// never stack duplicate stars.
/// </summary>
[HarmonyPatch]
public static class Patch_BuildingSystemSlot_UpdateBuildSlot
{
    private static readonly MethodBase[] CandidateTargets =
    {
        AccessTools.DeclaredMethod(typeof(BuildingSystemSlot),
            nameof(BuildingSystemSlot.UpdateBuildSlot),
            new[] { typeof(BuildingSystem.FurnitureInfo) }),
        AccessTools.DeclaredMethod(typeof(FurnitureShopPanelUI),
            nameof(FurnitureShopPanelUI.UpdateSlots),
            new[] { typeof(List<BuildingSystem.FurnitureInfo>), typeof(bool) }),
    };

    private static MethodBase TargetMethod()
    {
        foreach (var candidate in CandidateTargets)
        {
            if (candidate != null) return candidate;
        }
        return null;
    }

    private static bool Prepare(MethodBase original)
    {
        if (original == null)
        {
            Debug.LogError("[RecipeMarker] CRITICAL: neither BuildingSystemSlot.UpdateBuildSlot(FurnitureInfo) nor FurnitureShopPanelUI.UpdateSlots(List<FurnitureInfo>, bool) was found — worktable blueprint stars will not work.");
            return false;
        }

        Plugin.LogDebug($"[RecipeMarker] UpdateBuildSlot patch mounted on: {original.DeclaringType?.Name}.{original.Name}({string.Join(", ", Array.ConvertAll(original.GetParameters(), p => p.ParameterType.Name))})");
        return true;
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
            Plugin.LogDebug($"[RecipeMarker] Star attached: go={slot.gameObject.name}, slotType={slotTypeName}, shopSlotType={shopSlotTypeName}, blueprint={blueprint.id}, totalStars={RecipeMarkerStar.All.Count}");
        }
        else
        {
            Plugin.LogDebug($"[RecipeMarker] UpdateBuildSlot(FurnitureInfo) no blueprint: go={slot.gameObject.name}, slotType={slotTypeName}, shopSlotType={shopSlotTypeName}, taskItem={taskItemTypeName}");
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
