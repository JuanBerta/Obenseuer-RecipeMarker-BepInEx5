using System;
using System.Collections.Generic;
using UnityEngine;

namespace ObenseuerRecipeMarker;

/// <summary>
/// Runtime state: the single source of truth for all recipe markers.
/// Holds the set of marked recipes (by Recipe.ID) and marked furniture
/// blueprints (by TaskItemBlueprint.id), plus a derived cache of the input
/// item IDs marked recipes consume. The cache is what the ItemData.UpdateItem
/// patch reads, so inventory highlighting stays cheap.
///
/// Every marker change fires <see cref="MarkersChanged"/> so all attached
/// stars (station, worktable, recipes gallery) refresh to the same state.
/// </summary>
public static class RecipeMarkerManager
{
    /// <summary>Persisted list of marked recipe IDs (insertion order, stable for the JSON file).</summary>
    public static readonly List<int> MarkedRecipeIds = new List<int>();

    /// <summary>Persisted list of marked furniture blueprint IDs (TaskItemBlueprint.id).</summary>
    public static readonly List<string> MarkedBlueprintIds = new List<string>();

    /// <summary>Derived cache: every item ID that is an input of at least one marked recipe.</summary>
    private static readonly HashSet<int> MarkedInputItemIds = new HashSet<int>();

    /// <summary>
    /// Derived cache: item ID -&gt; marked recipes/blueprints that consume it
    /// (display name, required amount). Built together with
    /// MarkedInputItemIds so hover lookups are O(1).
    /// </summary>
    private static readonly Dictionary<int, List<(string Name, float Amount)>> MarkedRequiredByItem =
        new Dictionary<int, List<(string Name, float Amount)>>();

    /// <summary>Fired after any marker change so every attached star refreshes.</summary>
    public static event Action MarkersChanged;

    public static bool IsRecipeMarked(int recipeId)
    {
        return MarkedRecipeIds.Contains(recipeId);
    }

    public static bool IsBlueprintMarked(string blueprintId)
    {
        return blueprintId != null && MarkedBlueprintIds.Contains(blueprintId);
    }

    public static bool IsMarkedInputItem(int itemId)
    {
        return MarkedInputItemIds.Contains(itemId);
    }

    /// <summary>
    /// Look up every marked recipe/blueprint that consumes the given item.
    /// Returns false when the item is not used by any marked recipe.
    /// </summary>
    public static bool TryGetRequiredBy(int itemId, out List<(string Name, float Amount)> entries)
    {
        return MarkedRequiredByItem.TryGetValue(itemId, out entries);
    }

    /// <summary>Toggle a recipe's marker; persists immediately and refreshes the inventory highlight.</summary>
    public static void ToggleRecipe(Recipe recipe)
    {
        if (recipe == null || recipe.ID < 0) return;

        if (MarkedRecipeIds.Contains(recipe.ID))
        {
            MarkedRecipeIds.Remove(recipe.ID);
        }
        else
        {
            MarkedRecipeIds.Add(recipe.ID);
        }

        RebuildInputCache();
        Save();
        RefreshInventory();
        MarkersChanged?.Invoke();
    }

    /// <summary>Toggle a furniture blueprint's marker (worktable blueprint grid).</summary>
    public static void ToggleBlueprint(TaskItemBlueprint blueprint)
    {
        if (blueprint == null) return;

        // v0.2.5: use the stable Id property (id -> addressableAssetPath ->
        // itemName) instead of the raw serialized `id` field. Blueprint assets
        // often leave `id` empty, which made the old code silently no-op.
        string blueprintId = blueprint.Id;
        if (string.IsNullOrEmpty(blueprintId)) return;

        if (MarkedBlueprintIds.Contains(blueprintId))
        {
            MarkedBlueprintIds.Remove(blueprintId);
        }
        else
        {
            MarkedBlueprintIds.Add(blueprintId);
        }

        // v0.2.6: blueprints consume inventory items too; rebuild the derived
        // cache so marking/unmarking a blueprint toggles the gold frame on its
        // materials (e.g. Sawdust for a cage blueprint) just like recipes do.
        RebuildInputCache();
        Save();
        RefreshInventory();
        MarkersChanged?.Invoke();
    }

    /// <summary>
    /// Re-derive MarkedInputItemIds from the marked recipes' runtime Input
    /// dictionaries plus every marked blueprint's resourceItems. Blueprint
    /// resources that are plain items (Furniture.ReseourceItem.item, e.g.
    /// Sawdust) feed the same gold-frame highlight as recipe inputs; furniture
    /// / furnitureGroup dependencies are not inventory items and are skipped.
    /// </summary>
    public static void RebuildInputCache()
    {
        MarkedInputItemIds.Clear();
        MarkedRequiredByItem.Clear();
        int resolvedRecipes = 0;
        foreach (int recipeId in MarkedRecipeIds)
        {
            try
            {
                Recipe recipe = RecipeDatabase.instance?.FetchRecipesById(recipeId);
                if (recipe?.Input == null) continue;
                resolvedRecipes++;
                foreach (KeyValuePair<Item, float> pair in recipe.Input)
                {
                    Item item = pair.Key;
                    if (item == null) continue;
                    MarkedInputItemIds.Add(item.ID);
                    AddRequiredBy(item.ID, recipe.Name, pair.Value);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RecipeMarker] RebuildInputCache: recipe {recipeId}: {ex.Message}");
            }
        }

        int resolvedBlueprints = 0;
        foreach (string blueprintId in MarkedBlueprintIds)
        {
            try
            {
                TaskItemBlueprint blueprint = FindBlueprint(blueprintId);
                if (blueprint == null || blueprint.resourceItems == null) continue;
                resolvedBlueprints++;
                foreach (Furniture.ReseourceItem resource in blueprint.resourceItems)
                {
                    if (resource == null || resource.item == null || resource.item.Item == null) continue;
                    MarkedInputItemIds.Add(resource.item.Item.ID);
                    AddRequiredBy(resource.item.Item.ID, blueprint.itemName, resource.amount);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RecipeMarker] RebuildInputCache: blueprint {blueprintId}: {ex.Message}");
            }
        }

        Plugin.LogInfo($"[RecipeMarker] RebuildInputCache: {resolvedRecipes}/{MarkedRecipeIds.Count} recipe(s) resolved, {resolvedBlueprints}/{MarkedBlueprintIds.Count} blueprint(s) resolved, {MarkedRequiredByItem.Count} distinct input item(s) in cache (RecipeDatabase.instance={(RecipeDatabase.instance == null ? "null" : "ready")}).");
    }

    private static bool _cacheAttempted;

    /// <summary>
    /// Rebuild the derived input cache once the game scene has made
    /// RecipeDatabase.instance available. The first build runs inside Load(),
    /// which executes at plugin startup — before the scene registers the
    /// recipe database — so a session that loads a save without re-toggling
    /// any marker could otherwise keep an empty cache forever. Every hover /
    /// highlight patch calls this guard; it rebuilds at most once.
    /// </summary>
    public static void EnsureInputCache()
    {
        if (_cacheAttempted) return;
        if (MarkedRequiredByItem.Count > 0 ||
            (MarkedRecipeIds.Count == 0 && MarkedBlueprintIds.Count == 0))
        {
            _cacheAttempted = true;
            return;
        }
        if (RecipeDatabase.instance == null) return;
        RebuildInputCache();
        _cacheAttempted = true;
    }

    private static void AddRequiredBy(int itemId, string displayName, float amount)
    {
        if (string.IsNullOrEmpty(displayName)) displayName = "Recipe";
        if (!MarkedRequiredByItem.TryGetValue(itemId, out var list))
        {
            list = new List<(string Name, float Amount)>();
            MarkedRequiredByItem[itemId] = list;
        }
        list.Add((displayName, amount));
    }

    /// <summary>
    /// Resolve a TaskItemBlueprint instance from its stable Id. The worktable
    /// grid is backed by BuildingSystem.blueprints (FurnitureInfo.taskItem),
    /// the main-menu gallery by TaskItemsManager.taskItems; a full
    /// Resources.FindObjectsOfTypeAll scan is the last resort so a blueprint
    /// works even before the player has it in either list.
    /// </summary>
    private static TaskItemBlueprint FindBlueprint(string blueprintId)
    {
        if (string.IsNullOrEmpty(blueprintId)) return null;

        try
        {
            if (BuildingSystem.instance != null && BuildingSystem.instance.blueprints != null)
            {
                foreach (var info in BuildingSystem.instance.blueprints)
                {
                    if (info?.taskItem is TaskItemBlueprint b && b.Id == blueprintId) return b;
                }
            }
        }
        catch
        {
            // best-effort lookup
        }

        try
        {
            if (TaskItemsManager.instance != null && TaskItemsManager.instance.taskItems != null)
            {
                foreach (var info in TaskItemsManager.instance.taskItems)
                {
                    if (info?.taskItem is TaskItemBlueprint b && b.Id == blueprintId) return b;
                }
            }
        }
        catch
        {
            // best-effort lookup
        }

        try
        {
            TaskItemBlueprint[] all = Resources.FindObjectsOfTypeAll<TaskItemBlueprint>();
            foreach (var b in all)
            {
                if (b != null && b.Id == blueprintId) return b;
            }
        }
        catch
        {
            // best-effort lookup
        }

        return null;
    }

    /// <summary>Force every visible inventory slot to re-run ItemData.UpdateItem (applies/clears the gold frame).</summary>
    public static void RefreshInventory()
    {
        try
        {
            Inventory.instance?.UpdateAllSlotsNow();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] RefreshInventory: {ex.Message}");
        }
    }

    public static void Load()
    {
        RecipeMarkerSave.Load(out List<int> recipeIds, out List<string> blueprintIds);
        MarkedRecipeIds.Clear();
        MarkedRecipeIds.AddRange(recipeIds);
        MarkedBlueprintIds.Clear();
        // v0.2.5: drop any empty ids written by older builds (raw TaskItemBlueprint.id
        // was empty on real assets, so a persisted entry could not be matched anyway).
        foreach (string id in blueprintIds)
        {
            if (!string.IsNullOrEmpty(id)) MarkedBlueprintIds.Add(id);
        }
        RebuildInputCache();
    }

    public static void Save()
    {
        RecipeMarkerSave.Save(MarkedRecipeIds, MarkedBlueprintIds);
    }
}
