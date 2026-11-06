// KSAMusic: situational music for KSA (user, 2026-10-26), released on its own 2026-11-06.
//
// Plays playlists by scene, body, altitude, speed and situation, in Soundtrack Editor Forked's playlists.cfg format
// (so Dynamic Music Overhaul's playlists work as they are), from Playlists/menu.cfg, kerbol.cfg and sol.cfg. The
// release ships open music (Transfer Windows, Kevin MacLeod) with a KSP-like preset and a drag-and-drop playlist
// editor (EditorServer, Music > Edit playlists...). Players' edits and own music live in <profile>/KSAMusic, and
// KSA's own tracks are played from the game install. Settings are in settings.cfg beside the dll.

using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace KSAMusic;

[StarMapMod]
public sealed class Mod
{
    private Harmony? _harmony;

    [StarMapBeforeMain]
    public void BeforeMain()
    {
        try
        {
            Music.Load(Path.GetDirectoryName(typeof(Mod).Assembly.Location) ?? ".");
            _harmony = new Harmony("ksamusic");
            _harmony.Patch(AccessTools.Method(typeof(GameAudio), nameof(GameAudio.UpdateAudio)),
                postfix: new HarmonyMethod(typeof(Music), nameof(Music.AfterUpdateAudio)));
            _harmony.Patch(AccessTools.Method(typeof(LocationMusicPlayer), nameof(LocationMusicPlayer.Update)),
                prefix: new HarmonyMethod(typeof(Music), nameof(Music.BeforeStockMusic)));
            // Stop while FMOD is still up: StarMap's Unload runs after KSA has closed FMOD (GameAudio.Release), and
            // releasing a stream then blocks forever, so the game never exited (thread dump, 2026-10-27).
            _harmony.Patch(AccessTools.Method(typeof(GameAudio), nameof(GameAudio.Release)),
                prefix: new HarmonyMethod(typeof(Player), nameof(Player.Shutdown)));
            Menu.Apply(_harmony);
        }
        catch (Exception ex)
        {
            Note.Write($"disabled: {ex}");
            _harmony?.UnpatchAll("ksamusic");
        }
    }

    [StarMapAfterGui]
    public void AfterGui(double dt) => Music.DrawOverlay();

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll("ksamusic");
        Player.Forget();                                 // no FMOD calls here: it is already shut down
    }
}

/// For other mods (by reflection): force a KSP scene name ("MainMenu", "AstronautComplex", ...), or null for automatic.
public static class Api
{
    public static void SetScene(string? scene) => Sampler.SceneOverride = string.IsNullOrWhiteSpace(scene) ? null : scene;
    public static string? CurrentPlaylist => Director.Current?.Name;
    public static string? CurrentTrack => Player.Track;
}

internal sealed class Settings
{
    public static Settings Current = new();
    public float Volume = 0.8f, FadeOut = 2f, FadeIn = 0f;
    public string Kerbol = "dmo", Sol = "ost";
    public bool OstSpaceLayer = true, TestAliases, Overlay;

    public static Settings Read(string path)
    {
        var d = Cfg.LoadSettings(path);
        var s = new Settings();
        float F(string k, float def) => d.TryGetValue(k, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
        bool B(string k, bool def) => d.TryGetValue(k, out var v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
        s.Volume = Math.Clamp(F("volume", s.Volume), 0f, 2f);
        s.FadeOut = F("fadeOut", s.FadeOut);
        s.FadeIn = F("fadeIn", s.FadeIn);
        s.Kerbol = d.GetValueOrDefault("kerbol", s.Kerbol).ToLowerInvariant();
        s.Sol = d.GetValueOrDefault("sol", s.Sol).ToLowerInvariant();
        s.OstSpaceLayer = B("ostSpaceLayer", s.OstSpaceLayer);
        s.TestAliases = B("testAliases", s.TestAliases);
        s.Overlay = B("overlay", s.Overlay);
        return s;
    }
}

internal static class Music
{
    // KSP OST tracks for the Kerbol space layer: the four space ambiences and the Kevin MacLeod flight tracks.
    private static readonly string[] OstSpace =
    {
        "KSP_SpaceAmbience01", "KSP_SpaceAmbience02", "KSP_SpaceAmbience03", "KSP_SpaceAmbience04",
        "Brittle_Rille", "Frozen_Star", "Impact_Lento", "Wizardtorium", "Groove_Grove", "Bathed_in_the_Light",
        "Frost_Waltz", "Frost_Waltz__Alternate", "Dreamy_Flashback", "Arcadia",
    };

    private static readonly Dictionary<string, List<Playlist>> Sets = new();
    private static GameState _state = new();
    private static double _sinceTick = 1;
    private static bool _silence;                        // sol = off / kerbol = off: hold KSA's music too
    private static bool _loaded;
    private static int _errors;
    private static string _dir = ".";
    private static string? _lastScene, _lastPlaylist;

    /// The mod's folder (what Load was given).
    public static string ModDir => _dir;

    /// The player's own KSAMusic folder in the game profile: edited playlists and their own music. It survives mod
    /// updates, which replace the mod folder.
    public static string UserDir
    {
        get
        {
            try { return Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAMusic"); }
            catch { return Path.Combine(_dir, "user"); }
        }
    }

    /// KSA's own music in the game install (Content/Core/Music). Played from there, never shipped.
    public static string GameMusicDir =>
        Path.Combine(Path.GetDirectoryName(typeof(KSA.Program).Assembly.Location) ?? ".", "Content", "Core", "Music");

    /// Set by the playlist editor's server thread after a save; the next audio update (main thread) reloads.
    public static volatile bool ReloadRequested;

    public static void Load(string dir)
    {
        _dir = dir;
        Note.Open(Path.Combine(dir, "KSAMusic.log"));
        Settings.Current = Settings.Read(Path.Combine(dir, "settings.cfg"));
        Prefs.Load(Settings.Current);
        Library.Scan(Path.Combine(dir, "Music"));
        Library.ScanAlso(Path.Combine(UserDir, "Music"));            // the player's own music
        // KSA's own tracks, from the game install. KSA plays them at Volume 0.2 (Sounds.xml); the packs here are mastered
        // for KSAMusic's 0.8, so these get a quarter of that to sound as they do in stock KSA.
        Library.ScanAlso(GameMusicDir, gain: 0.25f);
        Library.SetPool("ostspace", OstSpace);

        // The playlist editor saves into the profile (Borea replaces the mod folder on update); until then, the
        // playlists that ship with the mod.
        string user = Path.Combine(UserDir, "Playlists");
        string pl = File.Exists(Path.Combine(user, "kerbol.cfg")) || File.Exists(Path.Combine(user, "menu.cfg"))
            ? user : Path.Combine(dir, "Playlists");
        List<Playlist> Read(string f) => File.Exists(Path.Combine(pl, f)) ? Cfg.Load(Path.Combine(pl, f)) : new();
        foreach (var f in Directory.Exists(pl) ? Directory.GetFiles(pl, "*.cfg") : Array.Empty<string>())
            foreach (var (name, tracks) in Cfg.LoadPools(f))
                Library.SetPool(name, tracks);
        var menu = Read("menu.cfg");                     // first, so its menu rotation wins over DMO's single theme
        var kerbol = menu.Concat(Read("kerbol.cfg")).ToList();
        if (Settings.Current.OstSpaceLayer)
            foreach (var p in kerbol.Where(p => p.Name.Contains("space", StringComparison.OrdinalIgnoreCase)
                                                && !p.Name.StartsWith("Space Center", StringComparison.OrdinalIgnoreCase)))
                p.Tracks.AddRange(new[] { "interval", "@ostspace" });
        Sets["kerbol"] = kerbol;
        Sets["sol"] = menu.Concat(Read("sol.cfg")).ToList();
        Sets["none"] = new();
        Note.Write($"{Library.Count} tracks; {kerbol.Count} Kerbol and {Sets["sol"].Count} Sol playlists from {pl}; volume {Settings.Current.Volume}");
        _loaded = true;
    }

    /// GameAudio.UpdateAudio postfix: runs every frame, menu and editor included.
    public static void AfterUpdateAudio(double deltaTime)
    {
        try
        {
            if (!_loaded) Load(_dir);                    // BeforeMain failed or never ran
            EditorServer.SnapshotBodies();               // cheap unless the universe changed
            if (ReloadRequested)                         // the playlist editor saved (main thread only)
            {
                ReloadRequested = false;
                Load(_dir);
            }
            var cfg = Settings.Current;
            _sinceTick += deltaTime;
            if (_sinceTick >= 0.25)
            {
                _state = Sampler.Sample(_sinceTick, cfg.TestAliases);
                _sinceTick = 0;
                string mode = _state.IsKerbol ? cfg.Kerbol : cfg.Sol;
                string set = _state.IsKerbol || cfg.TestAliases
                    ? (mode == "off" && !cfg.TestAliases ? "none" : "kerbol")
                    : mode == "ost" ? "sol" : "none";
                _silence = mode == "off";
                Director.Use(set, Sets[set]);
                Director.Tick(_state);
                string scene = string.Join("|", _state.Scenes) + " " + _state.Body;
                string playlist = $"{set}/{Director.Current?.Name ?? "-"}";
                if (scene != _lastScene || playlist != _lastPlaylist)
                    Note.Write($"{_state.Describe()} -> {playlist}");
                _lastScene = scene;
                _lastPlaylist = playlist;
            }
            bool pause = _state.Paused && Director.Current is { PauseOnGamePause: true } && _state.Scenes.Contains("Flight");
            Player.Update(deltaTime, Prefs.Level, pause);
            Director.Update(cfg);
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Note.Write(ex.ToString());
        }
    }

    /// LocationMusicPlayer.Update prefix: KSA's own music steps aside while KSAMusic plays.
    public static bool BeforeStockMusic()
    {
        if (!Director.Owns && !_silence) return true;
        LocationMusicPlayer.Stop();
        return false;
    }

    public static void DrawOverlay()
    {
        if (!Prefs.ShowWindow) return;
        // Bottom-left, clear of the main menu's title (user, 2026-10-26); pinned there while the menu is open.
        float2 pos = new(12f, ImGui.GetIO().DisplaySize.Y - 12f);
        float2? pivot = new float2(0f, 1f);
        ImGui.SetNextWindowPos(ref pos, Peers.MenuOpen ? ImGuiCond.Always : ImGuiCond.FirstUseEver, ref pivot);
        bool keep = true;
        if (ImGui.Begin("Music###KSAMusicOverlay", ref keep, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.Text($"{(_state.IsKerbol ? "Kerbol" : "Sol")}  set {Director.SetName}");
            ImGui.Text(_state.Describe());
            ImGui.Text($"Playlist: {Director.Current?.Name ?? "-"}");
            ImGui.Text($"Track: {Player.Track ?? "-"}{(Player.FadingOut ? " (fading)" : "")}");
            ImGui.Text($"Matching: {string.Join(", ", Director.Valid.Select(p => p.Name))}");
        }
        ImGui.End();
        if (!keep)                                       // closed with its X: same as unticking Music > Music window
        {
            Prefs.ShowWindow = false;
            Prefs.Save();
        }
    }
}
