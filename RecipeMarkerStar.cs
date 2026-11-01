using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ObenseuerRecipeMarker;

/// <summary>
/// A small MonoBehaviour that renders the yellow star on a recipe button.
///
/// Star placement: parented to the recipe button GameObject, positioned at the
/// top-right corner (anchors 1,1 with a negative offset so it floats over the
/// button edge, like the upgrade-panel star next to "Not Enough Resources").
///
/// Clicking is implemented through IPointerClickHandler instead of Button.onClick:
/// the game ships both UnityEngine.dll (legacy facade) and UnityEngine.UI.dll, and
/// the C# compiler's assembly resolution for Button.onClick proved unreliable with
/// that pair; a plain Image + IPointerClickHandler needs no Button member at all.
/// </summary>
public class RecipeMarkerStar : MonoBehaviour, IPointerClickHandler
{
    public static readonly List<RecipeMarkerStar> All = new List<RecipeMarkerStar>();

    private static Sprite _starSprite;
    private static Sprite _starSpriteEmpty;

    private Image _image;
    private Recipe _recipe;
    private TaskItemBlueprint _blueprint;

    public Recipe Recipe => _recipe;
    public bool IsMarked
    {
        get
        {
            if (_recipe != null) return RecipeMarkerManager.IsRecipeMarked(_recipe.ID);
            if (_blueprint != null) return RecipeMarkerManager.IsBlueprintMarked(_blueprint.id);
            return false;
        }
    }

    /// <summary>Attach a star to a recipe button GameObject (backwards-compatible alias).</summary>
    public static RecipeMarkerStar Attach(GameObject recipeButtonGo, Recipe recipe)
    {
        return AttachRecipe(recipeButtonGo, recipe, 22f);
    }

    /// <summary>Attach a star to a recipe button GameObject.</summary>
    public static RecipeMarkerStar AttachRecipe(GameObject recipeButtonGo, Recipe recipe, float size = 22f)
    {
        if (recipeButtonGo == null || recipe == null) return null;
        return AttachInternal(recipeButtonGo, recipe, null, size);
    }

    /// <summary>Attach a star to a furniture blueprint slot GameObject (worktable grid / gallery).</summary>
    public static RecipeMarkerStar AttachBlueprint(GameObject blueprintSlotGo, TaskItemBlueprint blueprint, float size = 22f)
    {
        if (blueprintSlotGo == null || blueprint == null) return null;
        return AttachInternal(blueprintSlotGo, null, blueprint, size);
    }

    private static RecipeMarkerStar AttachInternal(GameObject targetGo, Recipe recipe, TaskItemBlueprint blueprint, float size)
    {
        // Reuse the vanilla star sprite from the upgrade panel if we can find it.
        if (_starSprite == null)
        {
            var sprite = FindStarSprite();
            if (sprite != null) _starSprite = sprite;
        }

        // Skip when the button already carries one (e.g. double attach after rebuilds).
        var existing = targetGo.GetComponent<RecipeMarkerStar>();
        if (existing != null)
        {
            existing._recipe = recipe;
            existing._blueprint = blueprint;
            existing.Refresh();
            return existing;
        }

        var starGo = new GameObject("RecipeMarkerStar", typeof(RectTransform));
        var star = starGo.AddComponent<RecipeMarkerStar>();
        starGo.transform.SetParent(targetGo.transform, false);

        var rt = starGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-6f, -6f);
        rt.sizeDelta = new Vector2(size, size);
        rt.SetAsLastSibling();

        star._image = starGo.AddComponent<Image>();
        star._image.raycastTarget = true;
        star._image.type = Image.Type.Simple;
        star._recipe = recipe;
        star._blueprint = blueprint;
        star.Refresh();

        // Defensive visibility: newly created objects default to active, but
        // ensure the star is never accidentally disabled by a parent rebuild
        // and stays on top of the slot artwork.
        starGo.SetActive(true);
        rt.SetAsLastSibling();

        All.Add(star);
        return star;
    }

    private void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    private void OnDestroy()
    {
        All.Remove(this);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left) return;
        Toggle();
    }

    public void Toggle()
    {
        if (_recipe != null)
        {
            RecipeMarkerManager.ToggleRecipe(_recipe);
        }
        else if (_blueprint != null)
        {
            RecipeMarkerManager.ToggleBlueprint(_blueprint);
        }
        Refresh();
    }

    /// <summary>Apply marked/unmarked visual (yellow vs. grey star).</summary>
    public void Refresh()
    {
        if (_image == null) return;
        bool marked = IsMarked;
        _image.sprite = marked ? GetStarSprite() : GetStarSpriteEmpty();
        // Unmarked stars are a soft grey so the slot itself stays readable,
        // but keep the alpha high enough that the star is still discoverable.
        _image.color = marked ? new Color(1f, 0.84f, 0.1f, 1f) : new Color(0.65f, 0.65f, 0.65f, 0.95f);
    }

    public void SetVisible(bool visible)
    {
        if (gameObject != null) gameObject.SetActive(visible);
    }

    /// <summary>Toggle visibility of every attached star (config hotkey).</summary>
    public static void SetAllVisible(bool visible)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i] != null) All[i].SetVisible(visible);
        }
    }

    /// <summary>Refresh every attached star (called after save load).</summary>
    public static void RefreshAll()
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i] != null) All[i].Refresh();
        }
    }

    private static Sprite FindStarSprite()
    {
        try
        {
            // The upgrade panel uses button_round_001_upgrade_star (from the runtime
            // Resources, cached by the first upgrade-panel open). Ask UnityEngine to
            // find it by name instead of loading an AssetBundle ourselves.
            Sprite[] all = Resources.FindObjectsOfTypeAll<Sprite>();
            foreach (var s in all)
            {
                if (s == null) continue;
                if (s.name.Contains("button_round_001_upgrade_star") ||
                    s.name.Contains("item_quality_stars"))
                {
                    return s;
                }
            }
        }
        catch
        {
            // resource lookup is best-effort
        }
        return null;
    }

    private static Sprite GetStarSprite()
    {
        return _starSprite != null ? _starSprite : Sprite.Create(
            Texture2D.whiteTexture,
            new Rect(0f, 0f, 4f, 4f),
            new Vector2(0.5f, 0.5f));
    }

    private static Sprite GetStarSpriteEmpty()
    {
        if (_starSpriteEmpty != null) return _starSpriteEmpty;
        _starSpriteEmpty = Sprite.Create(Texture2D.whiteTexture,
            new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
        return _starSpriteEmpty;
    }
}
