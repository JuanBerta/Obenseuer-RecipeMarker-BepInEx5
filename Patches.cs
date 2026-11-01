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
/// BuildingSystemSlot.UpdateBuildSlot postfix — attach a star to the worktable
/// (FurnitureCrafting) blueprint grid. The worktable right-hand grid is built
/// by ShopBaseUI + FurnitureShopPanelUI, which instantiate BuildingSystemShopSlot
/// and call UpdateBuildSlot(BuildingSystem.FurnitureInfo); only slots whose
/// FurnitureInfo.taskItem is a TaskItemBlueprint (a furniture recipe/blueprint)
/// receive a star. Shop slots (player inventory side, no taskItem) are skipped.
/// </summary>
[HarmonyPatch(typeof(BuildingSystemSlot), nameof(BuildingSystemSlot.UpdateBuildSlot))]
public static class Patch_BuildingSystemSlot_UpdateBuildSlot
{
    private static void Postfix(BuildingSystemSlot __instance)
    {
        try
        {
            if (__instance == null || !(__instance is BuildingSystemShopSlot)) return;
            if (__instance.buildObject?.taskItem is TaskItemBlueprint blueprint)
            {
                RecipeMarkerStar.AttachBlueprint(__instance.gameObject, blueprint, 18f);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] UpdateBuildSlot postfix: {ex.Message}");
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
