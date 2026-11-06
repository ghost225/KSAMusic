// Samples KSA's game state into the terms Soundtrack Editor's playWhen uses (KSP scenes, situations, vessel types).
// KSA has no KSC scene, prelaunch state or vessel type, so those are inferred; see PLAN.md "Mapping DMO conditions".

using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using KSA;

namespace KSAMusic;

internal sealed class GameState
{
    public readonly HashSet<string> Scenes = new(StringComparer.OrdinalIgnoreCase);
    public string? Body;                                   // KSA id of the parent body
    public readonly HashSet<string> BodyNames = new(StringComparer.OrdinalIgnoreCase);   // id plus KSP aliases
    public double Altitude = double.NaN, SurfaceSpeed, OrbitalSpeed;
    public bool InAtmosphere, Paused, InitialAscent;
    public double PeakAltitude = double.NaN;               // highest altitude since the last ground contact
    public int Situation, VesselType;
    public string? TimeOfDay;
    public bool IsKerbol;

    public string Describe() =>
        $"{string.Join("|", Scenes)} {Body ?? "-"} alt {Altitude / 1000:0.#} km  vs {SurfaceSpeed:0} vo {OrbitalSpeed:0} m/s  "
        + $"{SituationName(Situation)}{(VesselType == Cfg.Plane ? " plane" : "")}{(InAtmosphere ? " atmo" : "")}{(InitialAscent ? " ascent" : "")} {TimeOfDay}";

    private static string SituationName(int s) => s switch
    {
        Cfg.Landed => "landed", Cfg.Splashed => "splashed", Cfg.Prelaunch => "prelaunch", Cfg.Flying => "flying",
        Cfg.SubOrbital => "sub-orbital", Cfg.Orbiting => "orbiting", Cfg.Escaping => "escaping", _ => "-",
    };
}

internal static class Sampler
{
    // Debug aliases for testing DMO in Sol (settings: testAliases).
    private static readonly Dictionary<string, string> SolAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Earth"] = "Kerbin", ["Moon"] = "Mun", ["Luna"] = "Mun", ["Mars"] = "Duna", ["Venus"] = "Eve",
        ["Mercury"] = "Moho", ["Jupiter"] = "Jool", ["Sol"] = "Sun", ["Sun"] = "Sun",
    };

    /// Per-vehicle memory: KSA has no PRELAUNCH or vessel type, so we watch what each vehicle has done.
    private sealed class Track
    {
        public bool LeftGround, RolledFast, IsPlane;
        public double Travelled;                           // m moved on the surface before first leaving it
        public double AirTime;                             // s airborne without a break
    }
    private static readonly ConditionalWeakTable<Vehicle, Track> Tracks = new();

    // The first ascent from the pad or runway (user, 2026-10-29: launch music only on the initial launch, not when
    // bouncing on the ground or re-entering). Kept per flight, not per vehicle, so staging doesn't end it.
    private enum Ascent { None, Armed, Climbing, Done }
    private static Ascent _ascent = Ascent.None;
    private static double _armedTravel, _falling, _peak;    // _peak: highest altitude since the last ground contact

    private static string? _lastTodBody;
    private static double _lastElevation = double.NaN;
    private static bool _rising = true;

    public static string? SceneOverride;                  // Api.SetScene

    public static GameState Sample(double dt, bool testAliases)
    {
        var s = new GameState { IsKerbol = Peers.IsKerbol, Paused = SafeIsPaused() };
        var cam = Program.GetCamera();

        if (SceneOverride != null) s.Scenes.Add(SceneOverride);
        else if (Peers.MenuCredits) s.Scenes.Add("Credits");
        else if (Peers.MenuOpen) s.Scenes.Add("MainMenu");
        else if (Program.IsEditorOpen) { s.Scenes.Add("VAB"); s.Scenes.Add("SPH"); }
        else if (Peers.TechTreeOpen) s.Scenes.Add("RnDComplex");
        else if (Peers.KscViewActive) s.Scenes.Add("SpaceCentre");

        Vehicle? v = GameAudio.GetAudioCamera()?.Following as Vehicle ?? Program.ControlledVehicle;
        if (s.Scenes.Count == 0)
        {
            if (v != null) { s.Scenes.Add("Flight"); s.Scenes.Add("PSystem"); }
            else if (IsMapView()) s.Scenes.Add("TrackingStation");
        }

        if (v != null && s.Scenes.Contains("Flight"))
            SampleVehicle(s, v, dt, testAliases);
        else if (s.Scenes.Contains("SpaceCentre") && cam?.GetEffectiveNearbyCelestial() is Celestial home)
        {
            SetBody(s, home, testAliases);
            s.TimeOfDay = TimeOfDay(home, cam.PositionEcl);
        }
        return s;
    }

    private static void SampleVehicle(GameState s, Vehicle v, double dt, bool testAliases)
    {
        var parent = v.Parent;
        SetBody(s, parent as Astronomical, testAliases);
        double3 r = v.GetPositionCci();
        double radius = parent.MeanRadius;
        s.Altitude = Len(r) - radius;
        s.SurfaceSpeed = v.GetSurfaceSpeed();
        s.OrbitalSpeed = v.OrbitalSpeed;

        double atmoTop = 0;
        if (parent is Celestial c && c.GetAtmosphereReference() is { } atmo)
            atmoTop = (double)atmo.Physical.Height;
        s.InAtmosphere = atmoTop > 0 && s.Altitude < atmoTop;

        var t = Tracks.GetOrCreateValue(v);
        var sit = v.Situation;
        bool ground = sit.HasTerrainContact(), ocean = sit.HasOceanContact();
        if (ground || ocean)
        {
            t.AirTime = 0;
            if (!t.LeftGround) t.Travelled += s.SurfaceSpeed * dt;
            if (s.SurfaceSpeed > 10) t.RolledFast = true;
            s.Situation = !t.LeftGround && t.Travelled < 100 ? Cfg.Prelaunch : ocean ? Cfg.Splashed : Cfg.Landed;
        }
        else
        {
            // Leaving the ground after a fast take-off roll is what a plane does; rockets lift off from a standstill.
            // A vehicle settling onto the pad at spawn, or a short hop, hasn't left the ground: only 1.5 s aloft counts.
            t.AirTime += dt;
            if (!t.LeftGround && t.AirTime > 1.5)
            {
                t.IsPlane = t.RolledFast;
                t.LeftGround = true;
            }
            var o = v.Orbit;
            if (s.InAtmosphere) s.Situation = Cfg.Flying;
            else if (o.Eccentricity >= 1 || o.Apoapsis > parent.SphereOfInfluence) s.Situation = Cfg.Escaping;
            else if (o.Periapsis < radius + atmoTop) s.Situation = Cfg.SubOrbital;
            else s.Situation = Cfg.Orbiting;
        }
        // A take-off roll already makes it a plane, so the runway roll gets plane music rather than rocket music.
        s.VesselType = t.IsPlane || (!t.LeftGround && t.RolledFast) ? Cfg.Plane : Cfg.Ship;

        double3 vel = v.GetVelocityCci();
        double radialSpeed = Dot(r, vel) / Math.Max(Len(r), 1);
        UpdateAscent(s, ground || ocean, radialSpeed, atmoTop, dt);
        s.InitialAscent = _ascent is Ascent.Armed or Ascent.Climbing;
        _peak = ground || ocean ? s.Altitude : Math.Max(_peak, s.Altitude);
        s.PeakAltitude = _peak;

        if (parent is Celestial body)
            s.TimeOfDay = TimeOfDay(body, v.GetPositionEcl());
        else
            s.TimeOfDay = "DayAM";                         // around the star itself
    }

    private static void UpdateAscent(GameState s, bool contact, double radialSpeed, double atmoTop, double dt)
    {
        // Back on the ground without having really left it (spawn settle, short hop) is still prelaunch: re-armed.
        if (s.Situation == Cfg.Prelaunch)
        {
            if (_ascent != Ascent.Armed) _armedTravel = 0;
            _ascent = Ascent.Armed;                    // a new launch, whatever happened before
            return;
        }
        switch (_ascent)
        {
            case Ascent.Armed when contact:
                // A take-off roll; a rover that drives off for kilometres never launched.
                _armedTravel += s.SurfaceSpeed * dt;
                if (_armedTravel > 3000) _ascent = Ascent.Done;
                break;
            case Ascent.Armed:
                _ascent = Ascent.Climbing;
                _falling = 0;
                break;
            case Ascent.Climbing:
                // Planes climb and descend all flight long; only a rocket falling back ends its launch.
                _falling = radialSpeed < -5 && s.VesselType != Cfg.Plane ? _falling + dt : 0;
                if (contact || _falling > 3 || (atmoTop > 0 && s.Altitude > atmoTop))
                    _ascent = Ascent.Done;                 // landed again, falling back, or out of the air
                break;
        }
    }

    private static void SetBody(GameState s, Astronomical? body, bool testAliases)
    {
        if (body == null) return;
        s.Body = body.Id;
        s.BodyNames.Add(body.Id);
        if (body is StellarBody) s.BodyNames.Add("Sun");    // KSP's internal name for Kerbol
        if (testAliases && SolAliases.TryGetValue(body.Id, out var alias)) s.BodyNames.Add(alias);
    }

    /// KSP's TimeOfDay: night below -6° sun elevation, twilight to +6°, day above; AM while the sun is rising.
    private static string? TimeOfDay(Celestial body, double3 posEcl)
    {
        IParentBody p = body;
        while (p is IOrbiter o && o.Parent != null) p = o.Parent;
        if (p is not Astronomical star || star == (Astronomical)body) return "DayAM";
        double3 up = posEcl - body.GetPositionEcl();
        double3 sun = star.GetPositionEcl() - posEcl;
        double elev = Math.Asin(Math.Clamp(Dot(up, sun) / (Len(up) * Len(sun)), -1, 1)) * 180 / Math.PI;
        if (_lastTodBody == body.Id && !double.IsNaN(_lastElevation) && Math.Abs(elev - _lastElevation) > 1e-4)
            _rising = elev > _lastElevation;
        _lastTodBody = body.Id;
        _lastElevation = elev;
        string half = _rising ? "AM" : "PM";
        return elev < -6 ? "Night" + half : elev < 6 ? "Twilight" + half : "Day" + half;
    }

    private static bool IsMapView()
    {
        try { return Program.MainViewport.GetActiveController() is MapController; }
        catch { return false; }
    }

    private static bool SafeIsPaused()
    {
        try { return Universe.IsPaused(); }
        catch { return false; }
    }

    private static double Dot(double3 a, double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Len(double3 a) => Math.Sqrt(Dot(a, a));
}

/// Other mods' state, read by reflection so KSAMusic needs none of them installed.
internal static class Peers
{
    private sealed class Flag(string assembly, string type, string member)
    {
        private Func<object?>? _get;
        private double _nextLookup;

        public object? Read()
        {
            if (_get == null)
            {
                double now = Environment.TickCount64 / 1000.0;
                if (now < _nextLookup) return null;
                _nextLookup = now + 5;                     // the mod may load after us; look again later
                var t = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                if (t?.GetProperty(member, F) is PropertyInfo p) _get = () => p.GetValue(null);
                else if (t?.GetField(member, F) is FieldInfo f) _get = () => f.GetValue(null);
                else return null;
            }
            try { return _get(); }
            catch { return null; }
        }

        public bool Bool => Read() is true;
    }

    private static readonly Flag World = new("KSAWorld", "KSAWorld.World", "IsKerbol");
    private static readonly Flag Menu = new("KSAMainMenu", "KSAMainMenu.Menu", "IsOpen");
    private static readonly Flag Credits = new("KSAMainMenu", "KSAMainMenu.Menu", "IsCreditsOpen");
    private static readonly Flag TechTree = new("KSAScience", "KSAScience.ScienceApi", "IsTechTreeOpen");
    private static readonly Flag KscView = new("KSAKonstructs", "KSAKonstructs.KscView", "IsActive");

    public static bool IsKerbol => World.Bool;
    public static bool MenuOpen => Menu.Bool;
    public static bool MenuCredits => Credits.Bool;
    public static bool TechTreeOpen => TechTree.Bool;
    public static bool KscViewActive => KscView.Bool;
}
