using System;
using UnityEngine;

namespace ObenseuerRecipeMarker;

/// <summary>
/// Immediate re-pin of every open panel when the player marks/unmarks a
/// recipe or blueprint.
///
/// The pinned ordering itself is applied by the vanilla rebuild events:
///   - main-menu gallery grid: ItemsUIPanel.UpdateCategoryRows (via GalleryMount)
///   - worktable craft-panel buttons: RecipeListUI.UpdateRecipes (via postfix)
///   - furniture blueprint panel: FurnitureShopPanelUI.UpdateSlots / OpenMenu
///     (via WorktableMount)
/// Those events only run when a panel is (re)built — open, close, section
/// toggle, item batch. Marking a recipe while a panel is already open never
/// triggers any of them, which is exactly the "it works, but only after
/// closing and opening the window/UI again" behaviour reported by the player.
///
/// This handler is subscribed to RecipeMarkerManager.MarkersChanged in
/// Plugin.Awake (alongside RecipeMarkerStar.RefreshAll) and re-applies the pin
/// to every currently-open surface immediately, so the marked row/button
/// jumps to the top of the visible list on the very next frame without a
/// close/reopen cycle.
/// </summary>
public static class SortRefresh
{
    private static int _fires;

    public static void OnMarkersChanged()
    {
        try
        {
            _fires++;
            int galleryReloaded = 0;
            int craftPanels = 0;
            int blueprintPanels = 0;

            // Main-menu Recipes/Blueprints gallery: ReloadList is public and
            // no-ops when the panel is inactive (it checks activeInHierarchy),
            // so calling it unconditionally is safe. It re-slices every
            // category, which re-runs the GalleryMount prefix that pins the
            // marked rows to the top of their section.
            if (ItemsUIPanel.instance != null)
            {
                ItemsUIPanel.instance.ReloadList();
                galleryReloaded = 1;
            }

            // Worktable craft-panel button lists currently open in the scene.
            foreach (var ui in UnityEngine.Object.FindObjectsOfType<RecipeListUI>())
            {
                if (ui == null) continue;
                Patch_RecipeListUI_UpdateRecipes.PinButtons(ui);
                craftPanels++;
            }

            // Furniture blueprint panels currently open in the scene.
            foreach (var panel in UnityEngine.Object.FindObjectsOfType<FurnitureShopPanelUI>())
            {
                if (panel == null) continue;
                WorktableMount.PatchPanelSlots(panel);
                blueprintPanels++;
            }

            if (_fires <= 3 || galleryReloaded > 0 || craftPanels > 0 || blueprintPanels > 0)
            {
                Plugin.LogInfo($"[RecipeMarker] SortRefresh #{_fires}: gallery={(galleryReloaded > 0 ? "reloaded" : "inactive")}, craftPanels={craftPanels}, blueprintPanels={blueprintPanels}.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] SortRefresh: {ex.Message}");
        }
    }
}
