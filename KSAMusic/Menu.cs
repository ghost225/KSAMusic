// KSAMusic: a "Music" menu in KSA's top menu bar (user, 2026-10-27), in place of an always-on floating window.
// Injected right after the game's own menus by a transpiler on Program.DrawMenuBar, as KSAScience and KSAFar do
// (DrawProgramMenusHook is empty and may be inlined, so it can't simply be postfixed); the transpilers chain.
// The window, volume and mute are player preferences, kept in <KSA Documents>/mods/KSAMusic/prefs.cfg;
// settings.cfg beside the dll holds the pack's defaults.

using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;

namespace KSAMusic;

internal static class Prefs
{
    public static bool ShowWindow, Muted;
    public static float Volume = 0.8f;

    private static string Path => System.IO.Path.Combine(KSA.Constants.DocumentsFolderPath, "mods", "KSAMusic", "prefs.cfg");

    public static void Load(Settings defaults)
    {
        ShowWindow = defaults.Overlay;
        Volume = defaults.Volume;
        try
        {
            var d = Cfg.LoadSettings(Path);
            if (d.TryGetValue("showWindow", out var w)) ShowWindow = w.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (d.TryGetValue("muted", out var m)) Muted = m.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (d.TryGetValue("volume", out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                Volume = Math.Clamp(f, 0f, 2f);
        }
        catch (Exception ex)
        {
            Note.Write($"cannot read {Path}: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, string.Format(CultureInfo.InvariantCulture,
                "# KSAMusic player preferences (the Music menu writes this file)\nshowWindow = {0}\nmuted = {1}\nvolume = {2:0.###}\n",
                ShowWindow ? "true" : "false", Muted ? "true" : "false", Volume));
        }
        catch (Exception ex)
        {
            Note.Write($"cannot write {Path}: {ex.Message}");
        }
    }

    /// The level the player hears from KSAMusic, before KSA's own Music slider.
    public static float Level => Muted ? 0f : Volume;
}

internal static class Menu
{
    private static bool _volumeDirty;

    internal static void Apply(Harmony harmony)
    {
        MethodInfo drawMenuBar = AccessTools.Method(typeof(Program), nameof(Program.DrawMenuBar))
                                 ?? throw new MissingMethodException("Program.DrawMenuBar");
        harmony.Patch(drawMenuBar, transpiler: new HarmonyMethod(typeof(Menu), nameof(MenuBarTranspiler)));
    }

    private static IEnumerable<CodeInstruction> MenuBarTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo hook = AccessTools.Method(typeof(Program), nameof(Program.DrawProgramMenusHook));
        MethodInfo ours = AccessTools.Method(typeof(Menu), nameof(DrawMenu));
        int hooked = 0;
        foreach (CodeInstruction ci in instructions)
        {
            yield return ci;
            if (ci.Calls(hook))
            {
                yield return new CodeInstruction(OpCodes.Call, ours);
                hooked++;
            }
        }
        if (hooked == 0)
            throw new InvalidOperationException("Program.DrawMenuBar no longer calls DrawProgramMenusHook.");
    }

    private static void DrawMenu()
    {
        try
        {
            if (!ImGui.BeginMenu("Music"))
            {
                if (_volumeDirty) { Prefs.Save(); _volumeDirty = false; }   // save once the slider is let go
                return;
            }
            try
            {
                ImGui.TextDisabled($"Playing: {Player.Track ?? "nothing"}");
                ImGui.TextDisabled($"Playlist: {Director.Current?.Name ?? "-"}{(Director.Owns ? "" : "  (KSA's own music)")}");
                ImGui.Separator();
                if (ImGui.MenuItem("Next track", "", false))
                    Player.FadeOut(1f);
                if (ImGui.MenuItem("Mute", "", Prefs.Muted))
                {
                    Prefs.Muted = !Prefs.Muted;
                    Prefs.Save();
                }
                float v = Prefs.Volume;
                if (ImGui.SliderFloat("Volume", ref v, 0f, 1.5f))
                {
                    Prefs.Volume = v;
                    _volumeDirty = true;
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Edit playlists...", "", false))
                    EditorServer.Open();
                if (EditorServer.Url is string url)
                    ImGui.TextDisabled($"  editor at {url}");
                if (ImGui.MenuItem("Music window", "", Prefs.ShowWindow))
                {
                    Prefs.ShowWindow = !Prefs.ShowWindow;
                    Prefs.Save();
                }
            }
            finally
            {
                ImGui.EndMenu();
            }
        }
        catch (Exception ex)
        {
            Note.Write($"menu failed: {ex.Message}");
        }
    }
}
