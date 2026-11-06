// Soundtrack Editor Forked playlists.cfg reader (the format Dynamic Music Overhaul ships in), plus KSAMusic's
// settings.cfg. Only the keys DMO and our Sol overlay use are understood; anything else is ignored.

using System.Globalization;

namespace KSAMusic;

internal sealed class Playlist
{
    public string Name = "";
    public bool Loop = true, Shuffle, PauseOnGamePause = true, DisableAfterPlay, Enabled = true;
    public string? PlayNext, PlayAfter, PlayBefore;
    public List<string> Tracks = new();
    public Conditions When = new();
    public override string ToString() => Name;
}

/// <summary>A playlist's playWhen block. Null fields are not tested.</summary>
internal sealed class Conditions
{
    public HashSet<string>? Scenes, Bodies, TimesOfDay;
    public double? MinAlt, MaxAlt, MinVs, MaxVs, MinVo, MaxVo;
    public double? MinPeak;                     // KSAMusic's own key: climbed this high since last touching ground
    public bool? InAtmosphere;
    public bool? InitialAscent;                 // KSAMusic's own key: the launch from the pad, until the craft falls back
    public int? Situation;                      // KSP Vessel.Situations bitmask
    public int? VesselTypes;                    // bitmask of 1 << KSP VesselType

    public bool Match(GameState s)
    {
        if (Scenes != null && !Scenes.Overlaps(s.Scenes)) return false;
        if (Bodies != null && (s.Body == null || !Bodies.Overlaps(s.BodyNames))) return false;
        if (TimesOfDay != null && (s.TimeOfDay == null || !TimesOfDay.Contains(s.TimeOfDay))) return false;
        if (Situation is int sit && (s.Situation & sit) == 0) return false;
        if (VesselTypes is int vt && (s.VesselType & vt) == 0) return false;
        if (InAtmosphere is bool atm && s.InAtmosphere != atm) return false;
        if (InitialAscent is bool asc && s.InitialAscent != asc) return false;
        if (MinPeak is double pk && !(s.PeakAltitude >= pk)) return false;
        if (MinAlt is double a0 && s.Altitude < a0) return false;
        if (MaxAlt is double a1 && s.Altitude > a1) return false;
        if (MinVs is double v0 && s.SurfaceSpeed < v0) return false;
        if (MaxVs is double v1 && s.SurfaceSpeed > v1) return false;
        if (MinVo is double o0 && s.OrbitalSpeed < o0) return false;
        if (MaxVo is double o1 && s.OrbitalSpeed > o1) return false;
        return true;
    }
}

internal static class Cfg
{
    // KSP enums, as Soundtrack Editor writes them by name or number.
    public const int Landed = 1, Splashed = 2, Prelaunch = 4, Flying = 8, SubOrbital = 16, Orbiting = 32, Escaping = 64;
    public const int Ship = 1 << 7, Plane = 1 << 8;
    private static readonly string[] Situations = { "LANDED", "SPLASHED", "PRELAUNCH", "FLYING", "SUB_ORBITAL", "ORBITING", "ESCAPING", "DOCKED" };
    private static readonly string[] VesselTypes = { "Debris", "SpaceObject", "Unknown", "Probe", "Relay", "Rover", "Lander", "Ship", "Plane", "Station", "Base", "EVA", "Flag", "DeployedScienceController", "DeployedSciencePart", "DroppedPart", "DeployedGroundPart" };

    private sealed class Node
    {
        public readonly List<(string Key, string Value)> Values = new();
        public readonly List<(string Name, Node Node)> Nodes = new();
        public string? Get(string k) => Values.FirstOrDefault(v => v.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;
        public Node? Child(string n) => Nodes.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Node;
    }

    public static List<Playlist> Load(string path)
    {
        var root = Parse(File.ReadAllText(path));
        var lists = new List<Playlist>();
        foreach (var (name, n) in root.Nodes)
        {
            if (!name.Equals("playlist", StringComparison.OrdinalIgnoreCase)) continue;
            var p = new Playlist
            {
                Name = n.Get("name") ?? $"#{lists.Count}",
                Loop = Bool(n.Get("loop")) ?? true,
                Shuffle = Bool(n.Get("shuffle")) ?? false,
                PauseOnGamePause = Bool(n.Get("pauseOnGamePause")) ?? true,
                DisableAfterPlay = Bool(n.Get("disableAfterPlay")) ?? false,
                PlayNext = n.Get("playNext"), PlayAfter = n.Get("playAfter"), PlayBefore = n.Get("playBefore"),
            };
            if (n.Child("tracks") is Node t)
                p.Tracks.AddRange(t.Values.Where(v => v.Key.Equals("track", StringComparison.OrdinalIgnoreCase)).Select(v => v.Value));
            if (n.Child("playWhen") is Node w)
                p.When = new Conditions
                {
                    Scenes = Set(w.Get("scene")),
                    Bodies = Set(w.Get("bodyName")),
                    TimesOfDay = Set(w.Get("timeOfDay")),
                    MinAlt = Num(w.Get("minAltitude")), MaxAlt = Num(w.Get("maxAltitude")),
                    MinVs = Num(w.Get("minVelocitySurface")), MaxVs = Num(w.Get("maxVelocitySurface")),
                    MinVo = Num(w.Get("minVelocityOrbital")), MaxVo = Num(w.Get("maxVelocityOrbital")),
                    InAtmosphere = Bool(w.Get("inAtmosphere")),
                    InitialAscent = Bool(w.Get("initialAscent")),
                    MinPeak = Num(w.Get("minPeakAltitude")),
                    Situation = Mask(w.Get("situation"), Situations),
                    VesselTypes = Mask(w.Get("vesselType"), VesselTypes),
                };
            lists.Add(p);
        }
        return lists;
    }

    /// KSAMusic's own extension: pool { name = menu  track = ... }, played from a playlist as track = @menu.
    public static Dictionary<string, List<string>> LoadPools(string path)
    {
        var pools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, n) in Parse(File.ReadAllText(path)).Nodes)
            if (name.Equals("pool", StringComparison.OrdinalIgnoreCase) && n.Get("name") is string id)
                pools[id] = n.Values.Where(v => v.Key.Equals("track", StringComparison.OrdinalIgnoreCase)).Select(v => v.Value).ToList();
        return pools;
    }

    public static Dictionary<string, string> LoadSettings(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Split('#')[0];
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private static Node Parse(string text)
    {
        var stack = new Stack<Node>();
        var cur = new Node();
        string? pending = null;                          // a bare word naming the next { block
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) line = line[..c];
            // A line can hold several tokens ("tracks {", "}"); split around braces.
            foreach (var tok in Tokens(line))
            {
                if (tok == "{")
                {
                    var child = new Node();
                    cur.Nodes.Add((pending ?? "", child));
                    stack.Push(cur);
                    cur = child;
                    pending = null;
                }
                else if (tok == "}")
                {
                    if (stack.Count > 0) cur = stack.Pop();
                    pending = null;
                }
                else if (tok.IndexOf('=') is int eq and > 0)
                    cur.Values.Add((tok[..eq].Trim(), tok[(eq + 1)..].Trim()));
                else
                    pending = tok.Trim();
            }
        }
        return stack.Count > 0 ? stack.Last() : cur;
    }

    private static IEnumerable<string> Tokens(string line)
    {
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '{' && line[i] != '}') continue;
            var before = line[start..i].Trim();
            if (before.Length > 0) yield return before;
            yield return line[i].ToString();
            start = i + 1;
        }
        var rest = line[start..].Trim();
        if (rest.Length > 0) yield return rest;
    }

    private static HashSet<string>? Set(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null
        : new HashSet<string>(v.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

    private static double? Num(string? v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool? Bool(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" => true,
        "false" or "no" => false,
        _ => null,
    };

    /// A number (already a bitmask) or names joined by '|', each the bit of its index in the enum.
    private static int? Mask(string? v, string[] names)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (int.TryParse(v, out int n)) return n;
        int m = 0;
        foreach (var s in v.Split('|'))
        {
            int i = Array.FindIndex(names, x => x.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i >= 0) m |= 1 << i;
        }
        return m;
    }
}
