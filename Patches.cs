using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
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

    internal static readonly FieldInfo ItemInfoDescriptionField =
        AccessTools.Field(typeof(ItemInfoPanel), "itemDescripion");
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
/// RecipeListUI.UpdateRecipes postfix — pin every marked recipe to the top
/// of its own list, in the order the player marked them (earliest mark
/// first; a later mark stacks behind the earlier ones). The vanilla method
/// fully rebuilds the list on every refresh (DestroyButtons + refill), so
/// this postfix runs exactly once per refresh and never competes with later
/// button creation.
///
/// currentRecipes and recipeButtons are reordered in lockstep so the vanilla
/// selected-recipe restore loop (which pairs the two lists by index) keeps
/// working, and the button GameObjects are moved to the front of their
/// parent container — which is what defines the visible order inside a
/// RecipeListUI section.
/// </summary>
[HarmonyPatch(typeof(RecipeListUI), nameof(RecipeListUI.UpdateRecipes))]
public static class Patch_RecipeListUI_UpdateRecipes
{
    private static int _fires;

    private static void Postfix(RecipeListUI __instance)
    {
        try
        {
            _fires++;
            if (__instance == null || __instance.currentRecipes == null)
            {
                if (_fires <= 3) Plugin.LogInfo($"[RecipeMarker] UpdateRecipes postfix #{_fires}: instance/currentRecipes null, skip.");
                return;
            }

            var buttons = Patches.RecipeButtonsField.GetValue(__instance) as List<GameObject>;
            var recipes = __instance.currentRecipes;
            if (buttons == null || recipes.Count == 0 || recipes.Count != buttons.Count)
            {
                if (_fires <= 3) Plugin.LogInfo($"[RecipeMarker] UpdateRecipes postfix #{_fires}: early return (buttons={(buttons == null ? "null" : buttons.Count.ToString())}, recipes={recipes.Count}).");
                return;
            }

            // Collect marked entries in mark order. MarkedRecipeIds is an
            // insertion-ordered list, so index 0 is the earliest mark.
            var marked = new List<(int MarkIndex, Recipe Recipe, GameObject Button)>();
            for (int i = 0; i < recipes.Count; i++)
            {
                Recipe recipe = recipes[i];
                if (recipe == null) continue;
                int markIndex = RecipeMarkerManager.MarkedRecipeIds.IndexOf(recipe.ID);
                if (markIndex < 0) continue;
                marked.Add((markIndex, recipe, buttons[i]));
            }
            if (marked.Count == 0) return;

            marked.Sort((a, b) => a.MarkIndex.CompareTo(b.MarkIndex));

            // Rebuild both backing lists in lockstep: marked recipes first
            // (mark order), then every unmarked recipe in its original order.
            var pinned = new HashSet<Recipe>();
            foreach (var entry in marked) pinned.Add(entry.Recipe);

            var newRecipes = new List<Recipe>(recipes.Count);
            var newButtons = new List<GameObject>(buttons.Count);
            foreach (var entry in marked)
            {
                newRecipes.Add(entry.Recipe);
                newButtons.Add(entry.Button);
            }
            for (int i = 0; i < recipes.Count; i++)
            {
                if (pinned.Contains(recipes[i])) continue;
                newRecipes.Add(recipes[i]);
                newButtons.Add(buttons[i]);
            }

            recipes.Clear();
            recipes.AddRange(newRecipes);
            buttons.Clear();
            buttons.AddRange(newButtons);

            // Move the buttons to the front of their parent container. Walk
            // backwards so the earliest mark ends up as the very first child.
            for (int i = marked.Count - 1; i >= 0; i--)
            {
                Transform t = marked[i].Button.transform;
                if (t != null) t.SetAsFirstSibling();
            }

            Plugin.LogInfo($"[RecipeMarker] UpdateRecipes postfix #{_fires}: pinned {marked.Count} marked recipe(s) to the top of '{__instance.name}' (total {recipes.Count}).");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] UpdateRecipes postfix: {ex.Message}");
        }
    }

    /// <summary>
    /// Transform-only re-pin for a RecipeListUI that is already open when the
    /// player marks/unmarks a recipe (called from SortRefresh.OnMarkersChanged).
    /// Moves the marked recipe buttons to the front of their parent container
    /// in mark order, which is what defines the visible order inside a
    /// RecipeListUI section. The backing lists (currentRecipes/recipeButtons)
    /// are intentionally left untouched so the vanilla selected-recipe restore
    /// loop keeps pairing the two lists by index; the full data+Transform pin
    /// still runs on every vanilla UpdateRecipes rebuild.
    /// </summary>
    public static void PinButtons(RecipeListUI ui)
    {
        if (ui == null || ui.currentRecipes == null) return;
        var buttons = Patches.RecipeButtonsField.GetValue(ui) as List<GameObject>;
        if (buttons == null || buttons.Count == 0) return;

        var marked = new List<(int MarkIndex, GameObject Button)>();
        for (int i = 0; i < ui.currentRecipes.Count && i < buttons.Count; i++)
        {
            Recipe recipe = ui.currentRecipes[i];
            if (recipe == null) continue;
            int markIndex = RecipeMarkerManager.MarkedRecipeIds.IndexOf(recipe.ID);
            if (markIndex < 0) continue;
            marked.Add((markIndex, buttons[i]));
        }
        if (marked.Count == 0) return;

        marked.Sort((a, b) => a.MarkIndex.CompareTo(b.MarkIndex));
        for (int i = marked.Count - 1; i >= 0; i--)
        {
            Transform t = marked[i].Button.transform;
            if (t != null) t.SetAsFirstSibling();
        }
    }
}

/// <summary>
/// ItemsUIPanel.UpdateCategoryRows prefix — pin marked recipe rows to the
/// top of their own section in the main-menu Recipes gallery (the grid the
/// player sees under RECIPES / OVEN RECIPES, backed by TaskItemUI cells).
///
/// Root cause of the v0.2.11 "not pinned" report: the old pin only patched
/// RecipeListUI.UpdateRecipes, which drives the worktable craft panel button
/// list. The gallery grid is rebuilt by ItemsUIPanel.UpdateCategoryRows from
/// ItemListRowData.taskItemsData (insertion order), and nothing touched that
/// ordering, so marked rows kept their vanilla position.
///
/// This prefix reorders category.taskItemsData in place — marked recipes
/// first in mark order (MarkedRecipeIds is insertion-ordered, index 0 =
/// earliest mark), every other entry in its original relative order — right
/// before the vanilla method slices the list into 7-per-row cells. Because
/// UpdateCategoryRows runs on every load batch (EndBatchAdd), panel open
/// (ReloadList), new recipe (AddItem) and section toggle (SetCategoryOpen),
/// the pin is reapplied everywhere the grid is (re)built. The partition is
/// stable and idempotent: already-pinned rows stay pinned, unpinned rows
/// never change relative order, and the persisted TaskItemsManager list is
/// never modified.
/// </summary>
/// <summary>
/// Runtime mount for the main-menu Recipes gallery pin (ItemsUIPanel grid).
///
/// v0.2.12: the previous [HarmonyPatch(typeof(ItemsUIPanel), "UpdateCategoryRows")]
/// static patch produced no observable effect (and, because LogDebug output is
/// filtered from the shipped log, no evidence of whether it mounted). To make
/// mount state observable, the target is now resolved with an exact signature
/// (ItemListRowData, int) via AccessTools.Method and mounted at startup with
/// Harmony.Patch — success/failure is logged at Info level, and the prefix
/// reports fire count / matched marks / pinned rows at Info level for the
/// first few fires and whenever it actually pins.
///
/// The prefix reorders category.taskItemsData in place — marked recipes first
/// in mark order (MarkedRecipeIds is insertion-ordered, index 0 = earliest
/// mark), every other entry in its original relative order — right before the
/// vanilla method slices the list into 7-per-row cells. UpdateCategoryRows
/// runs on every load batch (EndBatchAdd), panel open (ReloadList), new
/// recipe (AddItem) and section toggle (SetCategoryOpen), so the pin is
/// reapplied everywhere the grid is (re)built. The partition is stable and
/// idempotent; the persisted TaskItemsManager list is never modified.
/// </summary>
public static class GalleryMount
{
    private const string RecipeIdPrefix = "Recipe_";

    public static bool Mounted { get; private set; }

    private static int _fires;

    public static void TryMount()
    {
        if (Mounted) return;

        MethodBase target = AccessTools.Method(typeof(ItemsUIPanel), "UpdateCategoryRows",
            new[] { typeof(ItemListRowData), typeof(int) });
        if (target == null)
        {
            Plugin.LogInfo("[RecipeMarker] Gallery mount failed: ItemsUIPanel.UpdateCategoryRows(ItemListRowData,int) not found in game assembly.");
            return;
        }

        try
        {
            Plugin.HarmonyInstance.Patch(target,
                prefix: new HarmonyMethod(typeof(GalleryMount), nameof(GalleryMount.Prefix)));
            Mounted = true;
            Plugin.LogInfo($"[RecipeMarker] Gallery mount OK: {target.DeclaringType?.Name}.{target.Name}({string.Join(", ", Array.ConvertAll(target.GetParameters(), p => p.ParameterType.Name))})");
        }
        catch (Exception ex)
        {
            Plugin.LogInfo($"[RecipeMarker] Gallery mount failed: {ex.Message}");
        }
    }

    private static void Prefix(ItemListRowData category)
    {
        try
        {
            _fires++;
            if (category == null || category.taskItemsData == null || category.taskItemsData.Count < 2)
            {
                if (_fires <= 3) Plugin.LogInfo($"[RecipeMarker] Gallery prefix #{_fires}: null/empty category, skip.");
                return;
            }

            var data = category.taskItemsData;
            var marked = new List<(int MarkIndex, TaskItemUIData Item)>();
            var unmarked = new List<TaskItemUIData>(data.Count);
            foreach (TaskItemUIData uid in data)
            {
                if (uid == null) { unmarked.Add(uid); continue; }
                int markIndex = TryGetMarkIndex(uid);
                if (markIndex < 0) { unmarked.Add(uid); continue; }
                marked.Add((markIndex, uid));
            }
            if (marked.Count == 0)
            {
                if (_fires <= 3) Plugin.LogInfo($"[RecipeMarker] Gallery prefix #{_fires}: section '{category.title}' ({category.itemType}) rows={data.Count}, markedRecipeIds=[{string.Join(",", RecipeMarkerManager.MarkedRecipeIds)}], markedBlueprintIds=[{string.Join(",", RecipeMarkerManager.MarkedBlueprintIds)}], 0 marked recipe/blueprint rows matched.");
                return;
            }

            marked.Sort((a, b) => a.MarkIndex.CompareTo(b.MarkIndex));
            data.Clear();
            foreach (var entry in marked) data.Add(entry.Item);
            foreach (TaskItemUIData uid in unmarked) data.Add(uid);

            Plugin.LogInfo($"[RecipeMarker] Gallery prefix #{_fires}: pinned {marked.Count} marked recipe/blueprint row(s) to the top of section '{category.title}' ({category.itemType}) out of {data.Count} rows; first={marked[0].Item.taskItem?.id}.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] Gallery prefix: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve a gallery row to its mark index (position in the insertion-
    /// ordered marked lists, -1 when not marked). Recipe rows carry a TaskItem
    /// whose id is "Recipe_{Recipe.ID}" (RecipeController.CreateTaskItem) and
    /// match MarkedRecipeIds; blueprint rows are TaskItemBlueprint instances
    /// and match MarkedBlueprintIds by their stable Id. Blueprints gallery
    /// support was added in v0.2.13 (previously only Recipe_ rows were pinned).
    /// </summary>
    private static int TryGetMarkIndex(TaskItemUIData uid)
    {
        if (uid?.taskItem == null) return -1;
        TaskItem taskItem = uid.taskItem;

        if (taskItem is TaskItemBlueprint blueprint)
        {
            string blueprintId = blueprint.Id;
            if (string.IsNullOrEmpty(blueprintId)) return -1;
            return RecipeMarkerManager.MarkedBlueprintIds.IndexOf(blueprintId);
        }

        if (string.IsNullOrEmpty(taskItem.id) ||
            !taskItem.id.StartsWith(RecipeIdPrefix, StringComparison.Ordinal) ||
            !int.TryParse(taskItem.id.Substring(RecipeIdPrefix.Length),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int recipeId))
        {
            return -1;
        }
        return RecipeMarkerManager.MarkedRecipeIds.IndexOf(recipeId);
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

    internal static void PatchPanelSlots(FurnitureShopPanelUI panel)
    {
        var slots = panel.slots;
        if (slots == null || slots.Count == 0) return;

        int attached = 0;
        var marked = new List<(int MarkIndex, BuildingSystemShopSlot Slot)>();
        foreach (var slot in slots)
        {
            if (slot == null) continue;
            if (slot.shopSlotType == BuildingSystemShopSlot.ShopSlotType.PlayerSlot) continue;
            if (slot.buildObject?.taskItem is TaskItemBlueprint blueprint)
            {
                RecipeMarkerStar.AttachBlueprint(slot.gameObject, blueprint, 18f);
                attached++;
                int markIndex = RecipeMarkerManager.MarkedBlueprintIds.IndexOf(blueprint.Id);
                if (markIndex < 0) continue;
                marked.Add((markIndex, slot));
            }
        }

        // Pin marked blueprint slots to the front of the panel (earliest mark
        // first), keep every unmarked slot in its vanilla order. Reordering
        // the Transform hierarchy only affects display, never the underlying
        // FurnitureInfo data, so category switches / panel rebuilds stay safe
        // and idempotent.
        if (marked.Count > 0)
        {
            marked.Sort((a, b) => a.MarkIndex.CompareTo(b.MarkIndex));
            for (int i = marked.Count - 1; i >= 0; i--)
            {
                Transform t = marked[i].Slot.gameObject?.transform;
                if (t != null) t.SetAsFirstSibling();
            }
        }

        if (attached > 0 || marked.Count > 0)
        {
            Plugin.LogInfo($"[RecipeMarker] UpdateSlots postfix: {attached}/{slots.Count} worktable slots got stars, {marked.Count} marked blueprint(s) pinned to the top (panel={panel.name}).");
        }
        else
        {
            Plugin.LogDebug($"[RecipeMarker] UpdateSlots postfix: {attached}/{slots.Count} worktable slots got stars (panel={panel.name}).");
        }
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
/// ItemInfoPanel.UpdateInfo postfix — when the hovered item is consumed by at
/// least one marked recipe/blueprint, append a compact usage block to the
/// right-hand info panel:
///
///     Required for: {recipe name}
///     Required amount: {amount}
///
/// The block repeats once per consuming marked recipe, so a material used by
/// several marked recipes lists each of them. The vanilla method resets
/// itemDescripion to item.Description on every hover, so the appended block
/// can never accumulate across hovers. "Required for" (instead of the vanilla
/// tenement "Required in") keeps mod-driven usage visually distinguishable
/// from apartment upgrades.
/// </summary>
[HarmonyPatch(typeof(ItemInfoPanel), nameof(ItemInfoPanel.UpdateInfo))]
public static class Patch_ItemInfoPanel_UpdateInfo
{
    private static void Postfix(ItemInfoPanel __instance, ItemData itemData)
    {
        try
        {
            if (__instance == null || itemData?.item == null) return;
            RecipeMarkerManager.EnsureInputCache();
            if (!RecipeMarkerManager.TryGetRequiredBy(itemData.item.ID, out var entries) ||
                entries == null || entries.Count == 0) return;

            var description = Patches.ItemInfoDescriptionField.GetValue(__instance) as TextMeshProUGUI;
            if (description == null) return;

            var sb = new StringBuilder();
            sb.Append("\n\n");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append("\n");
                sb.Append("Required for: ").Append(entries[i].Name).Append('\n');
                sb.Append("Required amount: ").Append(entries[i].Amount.ToString("0.##", CultureInfo.InvariantCulture));
            }
            description.text += sb.ToString();
            Plugin.LogDebug($"[RecipeMarker] ItemInfoPanel postfix: item={itemData.item.Title} (ID {itemData.item.ID}), appended {entries.Count} entry(ies) to right panel.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] ItemInfoPanel postfix: {ex.Message}");
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
            RecipeMarkerManager.EnsureInputCache();
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

/// <summary>
/// ToolTip.Activate(ItemData, int, int, object[]) postfix — inject the same
/// "Required for / Required amount" block into the floating hover tooltip (the
/// popup that follows the mouse). The ItemInfoPanel postfix covers the fixed
/// right-hand side panel, but players read the floating popup while hovering
/// an item and that is the surface where the vanilla tenement-upgrade block
/// ("Required in: Apartment N …") is shown. "Required for" is deliberately
/// different from the vanilla "Required in" so mod-driven usage (marked
/// recipes/blueprints) is visually distinguishable from vanilla apartment
/// upgrades.
///
/// v0.2.10: the previous dedupe guard (details.text.Contains("Required in:"))
/// matched the vanilla tenement block too, so while the player had any marked
/// apartment upgrade the mod block was silently skipped. The guard now matches
/// the exact injected block; per-hover the vanilla method rebuilds
/// details.text from scratch, so blocks cannot accumulate across hovers.
///
/// The previous TooltipUI.UpdateTooltip prefix relied on
/// ToolTip.instance.currentItem for the hovered item, but the vanilla
/// Activate(ItemData) assigns currentItem only AFTER ConstructDataString has
/// already called UpdateTooltip — so the prefix always read the previous
/// hover's item and its title guard never matched (and the first hover sees
/// null). Postfixing Activate instead runs once currentItem is already
/// correct, and the block is appended straight to the TooltipUI.details text
/// component (no re-entrant UpdateTooltip, no recipe ingredient list rebuild).
/// </summary>
[HarmonyPatch(typeof(ToolTip), nameof(ToolTip.Activate), new[] { typeof(ItemData), typeof(int), typeof(int), typeof(object[]) })]
public static class Patch_ToolTip_Activate
{
    private static readonly FieldInfo TooltipUIsField = AccessTools.Field(typeof(ToolTip), "tooltipUIs");
    private static readonly FieldInfo TooltipUIDetailsField = AccessTools.Field(typeof(TooltipUI), "details");

    private static void Postfix(ToolTip __instance)
    {
        try
        {
            if (__instance == null || __instance.currentItem == null) return;
            RecipeMarkerManager.EnsureInputCache();

            Item item = __instance.currentItem;
            if (!RecipeMarkerManager.TryGetRequiredBy(item.ID, out var entries) ||
                entries == null || entries.Count == 0) return;

            var uis = TooltipUIsField.GetValue(__instance) as List<TooltipUI>;
            if (uis == null) return;

            var sb = new StringBuilder();
            sb.Append("\n\n");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append("\n");
                sb.Append("Required for: ").Append(entries[i].Name).Append('\n');
                sb.Append("Required amount: ").Append(entries[i].Amount.ToString("0.##", CultureInfo.InvariantCulture));
            }
            string block = sb.ToString();

            int updated = 0;
            foreach (var ui in uis)
            {
                if (ui == null) continue;
                var details = TooltipUIDetailsField.GetValue(ui) as TextMeshProUGUI;
                if (details == null) continue;
                // v0.2.10: exact-block dedupe. The old guard
                // (Contains("Required in:")) also matched the vanilla tenement
                // block, so the mod text was never appended while a vanilla
                // block was present. Match the full injected block instead;
                // per-hover details.text is rebuilt from scratch, so nothing
                // accumulates.
                if (details.text.Contains(block)) continue;
                details.text += block;
                details.gameObject.SetActive(true);
                updated++;
            }
            Plugin.LogDebug($"[RecipeMarker] ToolTip.Activate postfix: item={item.Title} (ID {item.ID}), appended block to {updated}/{uis.Count} tooltip UI(s).");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] ToolTip.Activate postfix: {ex.Message}");
        }
    }
}
