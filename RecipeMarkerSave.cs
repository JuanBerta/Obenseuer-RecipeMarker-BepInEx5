using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ObenseuerRecipeMarker;

/// <summary>
/// Persistence for the marked recipe list (and marked furniture blueprints).
/// Follows the game's QualitySave pattern: a small dedicated JSON file in
/// Application.persistentDataPath, written on every change, loaded inside
/// try/catch (a corrupt file is discarded and the mod starts fresh). Never
/// touches the game's save files.
///
/// Format v2 adds "markedBlueprintIds" for the worktable blueprint grid while
/// keeping the v1 "markedRecipeIds" field; older v1 files load fine (blueprint
/// list simply starts empty).
/// </summary>
public static class RecipeMarkerSave
{
    private const string FileName = "RecipeMarker.json";
    private const int Version = 2;

    public static string SavePath => Path.Combine(Application.persistentDataPath, FileName);

    public static void Save(List<int> recipeIds, List<string> blueprintIds)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"version\": ").Append(Version).Append(",\n  \"markedRecipeIds\": [");
            for (int i = 0; i < recipeIds.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(recipeIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append("],\n  \"markedBlueprintIds\": [");
            for (int i = 0; i < blueprintIds.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(blueprintIds[i]).Append('"');
            }
            sb.Append("]\n}\n");
            File.WriteAllText(SavePath, sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] Failed to save {SavePath}: {ex.Message}");
        }
    }

    /// <summary>Load both marked recipe IDs and marked blueprint IDs from the JSON file.</summary>
    public static void Load(out List<int> recipeIds, out List<string> blueprintIds)
    {
        recipeIds = new List<int>();
        blueprintIds = new List<string>();
        try
        {
            if (!File.Exists(SavePath)) return;

            string text = File.ReadAllText(SavePath);

            // markedRecipeIds: int array
            recipeIds.AddRange(ParseIntArray(text, "markedRecipeIds"));
            // markedBlueprintIds: string array (may be absent in v1 files)
            blueprintIds.AddRange(ParseStringArray(text, "markedBlueprintIds"));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecipeMarker] Save file corrupt, starting fresh: {ex.Message}");
            recipeIds = new List<int>();
            blueprintIds = new List<string>();
        }
    }

    private static List<int> ParseIntArray(string text, string fieldName)
    {
        var result = new List<int>();
        int start = text.IndexOf(fieldName + "\": [", StringComparison.Ordinal);
        if (start < 0) return result;
        start = text.IndexOf('[', start);
        int end = text.IndexOf(']', start);
        if (end <= start) return result;

        string inner = text.Substring(start + 1, end - start - 1);
        foreach (string part in inner.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    private static List<string> ParseStringArray(string text, string fieldName)
    {
        var result = new List<string>();
        int start = text.IndexOf(fieldName + "\": [", StringComparison.Ordinal);
        if (start < 0) return result;
        start = text.IndexOf('[', start);
        int end = text.IndexOf(']', start);
        if (end <= start) return result;

        string inner = text.Substring(start + 1, end - start - 1);
        foreach (string part in inner.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length < 2) continue;
            trimmed = trimmed.Trim('"');
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }
        return result;
    }
}
