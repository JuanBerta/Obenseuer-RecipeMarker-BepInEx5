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
    private static Sprite _fallbackStarSprite;

    private Image _image;
    private Recipe _recipe;
    private TaskItemBlueprint _blueprint;

    public Recipe Recipe => _recipe;
    public bool IsMarked
    {
        get
        {
            if (_recipe != null) return RecipeMarkerManager.IsRecipeMarked(_recipe.ID);
            if (_blueprint != null) return RecipeMarkerManager.IsBlueprintMarked(_blueprint.Id);
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
        // Marked = yellow hollow star; unmarked = dark grey hollow star.
        // White was nearly invisible on bright slot backgrounds; dark grey keeps
        // the marker discoverable while clearly reading as "not active".
        _image.color = marked ? new Color(1f, 0.84f, 0.1f, 1f) : new Color(0.4f, 0.4f, 0.4f, 1f);
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
        if (_starSprite != null) return _starSprite;
        // Fallback is a runtime-generated hollow star; a solid white square would block text.
        return GetFallbackStarSprite();
    }

    private static Sprite GetStarSpriteEmpty()
    {
        if (_starSpriteEmpty != null) return _starSpriteEmpty;
        // Unmarked stars always use the generated hollow star: its pixels are pure
        // white, so the grey tint stays neutral and never picks up the yellow tone
        // of the vanilla upgrade-panel sprite (which would look "active").
        _starSpriteEmpty = GetFallbackStarSprite();
        return _starSpriteEmpty;
    }

    /// <summary>
    /// Lazily generate a solid five-pointed star texture (white pixels on a transparent
    /// background) used whenever the vanilla upgrade-panel sprite is not available.
    /// The star is fully filled (no hollow center): a hollow outline proved nearly
    /// invisible on some recipe button backgrounds, so the marker stays solid and gets
    /// its "not active" look purely from the dark grey Image tint.
    /// </summary>
    private static Sprite GetFallbackStarSprite()
    {
        if (_fallbackStarSprite != null) return _fallbackStarSprite;

        const int texSize = 64;
        var tex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false);
        tex.name = "RecipeMarkerSolidStar";

        var pixels = new Color[texSize * texSize];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(0f, 0f, 0f, 0f);

        float cx = texSize * 0.5f;
        float cy = texSize * 0.5f;
        float outerR = texSize * 0.46f;
        float innerRatio = 0.382f;   // standard pentagram inner radius ratio

        var star = new Vector2[10];
        for (int i = 0; i < 5; i++)
        {
            float aOuter = Mathf.PI / 2f + i * 2f * Mathf.PI / 5f;
            float aInner = aOuter + Mathf.PI / 5f;
            star[i * 2] = new Vector2(cx + Mathf.Cos(aOuter) * outerR, cy + Mathf.Sin(aOuter) * outerR);
            star[i * 2 + 1] = new Vector2(cx + Mathf.Cos(aInner) * outerR * innerRatio, cy + Mathf.Sin(aInner) * outerR * innerRatio);
        }

        for (int y = 0; y < texSize; y++)
        {
            for (int x = 0; x < texSize; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                if (PointInPolygon(p, star))
                {
                    pixels[y * texSize + x] = Color.white;
                }
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        _fallbackStarSprite = Sprite.Create(tex, new Rect(0f, 0f, texSize, texSize), new Vector2(0.5f, 0.5f), 100f);
        return _fallbackStarSprite;
    }

    private static bool PointInPolygon(Vector2 point, Vector2[] poly)
    {
        int n = poly.Length;
        bool inside = false;
        int j = n - 1;
        for (int i = 0; i < n; i++)
        {
            Vector2 pi = poly[i];
            Vector2 pj = poly[j];
            if ((pi.y > point.y) != (pj.y > point.y) &&
                point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y) + pi.x)
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }
}
