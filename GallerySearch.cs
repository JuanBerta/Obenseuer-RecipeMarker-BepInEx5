using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ObenseuerRecipeMarker
{
    /// <summary>
    /// Search box for the main-menu items gallery (Inventory tab: task items, keys, recipes, blueprints...).
    /// Typing filters every section by name (recipes also by the item they make); sections keep their headers.
    /// While the box has focus the game's hotkeys are muted so typing doesn't drop items, switch tabs, etc.
    /// </summary>
    internal class GallerySearch : MonoBehaviour
    {
        private const string ControlName = "RecipeMarkerGallerySearch";
        private const string RecipeIdPrefix = "Recipe_";

        internal static string Filter = "";
        internal static bool Typing;
        private static string applied = "";
        private static FieldInfo rowsField;

        private string text = "";
        private GUIStyle field, placeholder, clear;
        private bool wasVisible, rectWarned;

        internal static void Init(GameObject host, Harmony harmony)
        {
            try
            {
                rowsField = AccessTools.Field(typeof(ItemsUIPanel), "itemListRowData");
                MethodBase target = AccessTools.Method(typeof(ItemsUIPanel), "UpdateCategoryRows", new[] { typeof(ItemListRowData), typeof(int) });
                if (target == null || rowsField == null) { Plugin.LogInfo("[RecipeMarker] Gallery search: panel members not found, search disabled."); return; }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(GallerySearch), nameof(RowsPostfix)) { priority = Priority.Last });
                InputMute.Patch(harmony);
                host.AddComponent<GallerySearch>();
                Plugin.LogInfo("[RecipeMarker] Gallery search ready.");
            }
            catch (Exception e) { Plugin.LogInfo("[RecipeMarker] Gallery search failed to start: " + e.Message); }
        }

        // ---- Filtering: rebuild the section's rows from the matching entries only ----

        private static void RowsPostfix(ItemsUIPanel __instance, ItemListRowData category)
        {
            if (string.IsNullOrEmpty(Filter) || category == null || category.taskItemsData == null) return;
            try
            {
                var all = rowsField.GetValue(__instance) as List<ItemListRowData>;
                if (all == null) return;
                int header = all.IndexOf(category);
                if (header < 0) return;
                var list = new List<TaskItemUIData>();
                foreach (TaskItemUIData d in category.taskItemsData)
                {
                    if (d == null || d.taskItem == null) continue;
                    if (!__instance.showItemsWithoutIcon && d.itemImage == null) continue;
                    if (Matches(d.taskItem)) list.Add(d);
                }
                foreach (ItemListRowData row in category.itemRows) all.Remove(row);
                category.itemRows.Clear();
                int rows = Mathf.CeilToInt(list.Count / 7f);
                for (int j = 0; j < rows; j++)
                {
                    var row = new ItemListRowData(category.itemType);
                    for (int k = j * 7; k < Mathf.Min(j * 7 + 7, list.Count); k++) row.taskItemsData.Add(list[k]);
                    category.itemRows.Add(row);
                    if (category.open) all.Insert(header + 1 + j, row);
                }
            }
            catch (Exception e) { Debug.LogWarning("[RecipeMarker] Gallery search: " + e.Message); }
        }

        private static bool Has(string s) { return !string.IsNullOrEmpty(s) && s.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0; }

        private static bool Matches(TaskItem t)
        {
            if (Has(t.itemName)) return true;
            if (!string.IsNullOrEmpty(t.id) && t.id.StartsWith(RecipeIdPrefix, StringComparison.Ordinal)
                && int.TryParse(t.id.Substring(RecipeIdPrefix.Length), out int id))
            {
                try
                {
                    Recipe r = RecipeDatabase.instance != null ? RecipeDatabase.instance.FetchRecipesById(id) : null;
                    if (r != null && (Has(r.Name) || Has(r.GetMainOutput()?.Item?.Title))) return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        private static void Apply()
        {
            if (applied == Filter) return;
            applied = Filter;
            try { if (ItemsUIPanel.instance != null) ItemsUIPanel.instance.ReloadList(); } catch (Exception) { }
        }

        // ---- The box ----

        private static FieldInfo viewportField, scrollField;

        // The list the player sees (the panel component itself can sit on an object that never hides).
        private static RectTransform ListRect
        {
            get
            {
                ItemsUIPanel p = ItemsUIPanel.instance;
                if (p == null) return null;
                if (viewportField == null) viewportField = AccessTools.Field(typeof(ItemsUIPanel), "viewport");
                if (scrollField == null) scrollField = AccessTools.Field(typeof(ItemsUIPanel), "scrollRectExtension");
                var vp = viewportField != null ? viewportField.GetValue(p) as RectTransform : null;
                if (vp != null) return vp;
                var sc = scrollField != null ? scrollField.GetValue(p) as Component : null;
                if (sc != null) return sc.transform as RectTransform;
                return p.transform as RectTransform;
            }
        }

        private static bool PanelVisible
        {
            get
            {
                try
                {
                    RectTransform rt = ListRect;
                    return rt != null && rt.gameObject.activeInHierarchy && Cursor.visible;
                }
                catch (Exception) { return false; }
            }
        }

        private void Update()
        {
            bool visible = PanelVisible;
            if (visible != wasVisible) Plugin.LogInfo("[RecipeMarker] Gallery search " + (visible ? "shown" : "hidden") + ".");
            if (!visible && wasVisible)
            {
                // Panel closed: start fresh next time.
                text = ""; Filter = ""; Typing = false; applied = "";
            }
            wasVisible = visible;
            if (!visible) Typing = false;
        }

        private static Rect PanelRect()
        {
            RectTransform rt = ListRect;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            return new Rect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        private static Texture2D Tex(Color fill, Color edge)
        {
            var t = new Texture2D(8, 8) { hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) t.SetPixel(x, y, x == 0 || y == 0 || x == 7 || y == 7 ? edge : fill);
            t.Apply();
            return t;
        }

        private void Styles()
        {
            if (field != null && field.normal.background != null) return;
            field = new GUIStyle(GUI.skin.textField) { fontSize = 15, padding = new RectOffset(10, 30, 6, 6), border = new RectOffset(2, 2, 2, 2) };
            field.normal.background = field.hover.background = Tex(new Color(0.1f, 0.09f, 0.08f, 0.92f), new Color(0.55f, 0.42f, 0.17f));
            field.focused.background = Tex(new Color(0.13f, 0.11f, 0.09f, 0.96f), new Color(0.95f, 0.75f, 0.35f));
            field.normal.textColor = field.hover.textColor = field.focused.textColor = new Color(0.98f, 0.93f, 0.8f);
            placeholder = new GUIStyle(field);
            placeholder.normal.background = null;
            placeholder.normal.textColor = new Color(0.62f, 0.57f, 0.48f);
            placeholder.fontStyle = FontStyle.Italic;
            clear = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            clear.normal.textColor = clear.hover.textColor = new Color(0.79f, 0.6f, 0.23f);
        }

        private void OnGUI()
        {
            if (!PanelVisible) return;
            Styles();
            Rect panel;
            try { panel = PanelRect(); }
            catch (Exception ex) { if (!rectWarned) { rectWarned = true; Plugin.LogInfo("[RecipeMarker] Gallery search position: " + ex.Message); } return; }
            float w = Mathf.Clamp(panel.width - 20f, 220f, 520f);
            var box = new Rect(panel.x + (panel.width - w) / 2f, 12f, w, 32f);
            Event e = Event.current;

            bool focused = GUI.GetNameOfFocusedControl() == ControlName;
            if (focused && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                if (e.keyCode == KeyCode.Escape) text = "";
                GUI.FocusControl(null);
                e.Use();
                focused = false;
            }
            if (e.type == EventType.MouseDown && !box.Contains(e.mousePosition) && focused) GUI.FocusControl(null);

            GUI.SetNextControlName(ControlName);
            text = GUI.TextField(box, text ?? "", 60, field);
            if (string.IsNullOrEmpty(text) && GUI.GetNameOfFocusedControl() != ControlName)
                GUI.Label(box, "Search recipes, keys, items...", placeholder);
            if (!string.IsNullOrEmpty(text) && GUI.Button(new Rect(box.xMax - 28f, box.y, 28f, box.height), "x", clear))
            {
                text = "";
                GUI.FocusControl(null);
            }

            Typing = GUI.GetNameOfFocusedControl() == ControlName;
            string f = (text ?? "").Trim();
            if (f != Filter) { Filter = f; Apply(); }
        }
    }

    /// <summary>While the gallery search box has focus, game key bindings and raw key reads report nothing.</summary>
    internal static class InputMute
    {
        private static readonly string[] ManagerNames = { "IsKeyDown", "IsKeyUp", "IsKey", "GetKeyDown", "GetKeyDownKeyboardOnly", "GetKeyUp", "GetKey", "GetKeyWithHold", "GetAuxiliaryReloadDown" };

        internal static void Patch(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(InputMute), nameof(BoolPrefix));
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(InputManager)).Where(m => ManagerNames.Contains(m.Name) && m.ReturnType == typeof(bool)))
            {
                try { harmony.Patch(m, prefix: prefix); } catch (Exception) { }
            }
            foreach (string n in new[] { "GetKeyDown", "GetKeyUp", "GetKey" })
                foreach (Type arg in new[] { typeof(KeyCode), typeof(string) })
                {
                    MethodInfo m = AccessTools.Method(typeof(Input), n, new[] { arg });
                    if (m == null) continue;
                    try { harmony.Patch(m, prefix: prefix); } catch (Exception) { }
                }
        }

        private static bool BoolPrefix(ref bool __result)
        {
            if (!GallerySearch.Typing) return true;
            __result = false;
            return false;
        }
    }
}
