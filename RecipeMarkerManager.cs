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

        Save();
        RefreshInventory();
        MarkersChanged?.Invoke();
    }

    /// <summary>Re-derive MarkedInputItemIds from the marked recipes' runtime Input dictionaries.</summary>
    public static void RebuildInputCache()
    {
        MarkedInputItemIds.Clear();
        foreach (int recipeId in MarkedRecipeIds)
        {
            try
            {
                Recipe recipe = RecipeDatabase.instance?.FetchRecipesById(recipeId);
                if (recipe?.Input == null) continue;
                foreach (Item item in recipe.Input.Keys)
                {
                    if (item != null) MarkedInputItemIds.Add(item.ID);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RecipeMarker] RebuildInputCache: recipe {recipeId}: {ex.Message}");
            }
        }
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
