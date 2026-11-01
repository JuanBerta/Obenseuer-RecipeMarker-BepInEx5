using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ObenseuerRecipeMarker;

/// <summary>
/// BepInEx 6 entry point for Obenseuer-RecipeMarker.
/// Adds a yellow star marker to crafting recipe buttons (station RecipeListUI),
/// the worktable blueprint grid (BuildingSystemShopSlot) and the main-menu
/// inventory Recipes gallery (TaskItemUI). RecipeMarkerManager is the single
/// source of truth: marking/unmarking anywhere refreshes every attached star
/// through the MarkersChanged event, so all open panels stay in sync.
/// Marked recipes persist and their input items are highlighted in the
/// inventory (reusing the vanilla "marked" gold frame on ItemData).
/// </summary>
[BepInPlugin("com.beinded.obenseuer.recipemarker", "Obenseuer Recipe Marker", "0.2.4")]
public class Plugin : BaseUnityPlugin
{
    public const string HarmonyId = "com.beinded.obenseuer.recipemarker";

    internal static Plugin Instance { get; private set; }

    /// <summary>Master switch: whether star buttons are shown on recipe buttons.</summary>
    internal static ConfigEntry<bool> ShowStars { get; private set; }

    /// <summary>Key that toggles star visibility at runtime.</summary>
    internal static ConfigEntry<KeyCode> ToggleStarsKey { get; private set; }

    private Harmony _harmony;

    /// <summary>
    /// Debug helper used by the Harmony patches. Writes through the BepInEx
    /// logger AND the Unity console so the message is visible no matter how
    /// the user captures logs. The messages are intentionally prefixed so
    /// worktable-slot diagnostics can be filtered with "UpdateBuildSlot".
    /// </summary>
    public static void LogDebug(string message)
    {
        string line = $"[RecipeMarker] {message}";
        Instance?.Logger.LogDebug(line);
        Debug.Log(line);
    }

    /// <summary>
    /// Info helper used by the Harmony patches. BepInEx Info level is written
    /// to LogOutput.log by default, unlike Debug, so mount-point diagnostics
    /// can be verified in the shipped log without enabling Debug logging.
    /// </summary>
    public static void LogInfo(string message)
    {
        string line = $"[RecipeMarker] {message}";
        Instance?.Logger.LogInfo(line);
        Debug.Log(line);
    }

    /// <summary>
    /// Error helper used by the Harmony patches. Writes through the BepInEx
    /// logger AND the Unity console so a mount-point failure is visible no
    /// matter how the user captures logs (Unity log writer failures included).
    /// </summary>
    public static void LogError(string message)
    {
        string line = $"[RecipeMarker] {message}";
        Instance?.Logger.LogError(line);
        Debug.LogError(line);
    }

    private void Awake()
    {
        Instance = this;

        ShowStars = Config.Bind("General", "ShowRecipeMarkerStars", true,
            "Show the yellow star marker on recipe buttons. Toggled at runtime with the key below.");
        ToggleStarsKey = Config.Bind("General", "ToggleStarsKey", KeyCode.F3,
            "Key that toggles recipe star visibility at runtime.");

        try
        {
            RecipeMarkerManager.Load();

            // Global sync: any marker change refreshes every attached star
            // (station buttons, worktable blueprint slots, recipes gallery).
            RecipeMarkerManager.MarkersChanged += RecipeMarkerStar.RefreshAll;

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll();
            Logger.LogInfo($"[RecipeMarker] Harmony patches applied ({HarmonyId}).");
            Logger.LogInfo($"[RecipeMarker] Loaded {RecipeMarkerManager.MarkedRecipeIds.Count} marked recipe(s).");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[RecipeMarker] Failed to initialize: {ex}");
        }
    }

    private void Update()
    {
        if (!Instance) return;
        try
        {
            if (Input.GetKeyDown(ToggleStarsKey.Value))
            {
                bool visible = !ShowStars.Value;
                ShowStars.Value = visible;
                RecipeMarkerStar.SetAllVisible(visible);
                Logger.LogInfo($"[RecipeMarker] Stars {(visible ? "shown" : "hidden")}.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[RecipeMarker] Update error: {ex.Message}");
        }
    }

    private void OnDestroy()
    {
        RecipeMarkerManager.Save();
        _harmony?.UnpatchSelf();
    }
}
