// Soundtrack Editor Forked's playlist rules (read from its SoundtrackEditor.cs), so DMO's playlists.cfg plays as
// its author tuned it:
//   - the playlists whose playWhen matches stay in file order, and the first one wins;
//   - the current playlist moves to the front and stays while it still matches;
//   - playAfter / playNext pull the named playlist in ahead of the one naming it, playBefore puts it behind;
//   - when the last track ends, play moves to the next matching playlist after the current one, else loops.

namespace KSAMusic;

internal static class Director
{
    private static List<Playlist> _lists = new();
    private static List<Playlist> _valid = new();
    private static Playlist? _current;
    private static List<string> _order = new();
    private static int _index = -1;
    private static readonly Random Rng = new();
    private const double SwitchDelay = 2.5;              // s
    private static Playlist? _wanted;
    private static double _wantedSince;
    private static readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);

    public static string SetName { get; private set; } = "";
    public static Playlist? Current => _current;
    public static IReadOnlyList<Playlist> Valid => _valid;

    /// True while KSAMusic has something to play or is still fading out; KSA's own music is held off meanwhile.
    public static bool Owns => _current != null || Player.Busy;

    public static void Use(string setName, List<Playlist> lists)
    {
        if (SetName == setName && ReferenceEquals(_lists, lists)) return;
        if (SetName == setName)
        {
            // The same set, reloaded (playlist editor save): keep the playing track, carry the current playlist over
            // by name, and let the next Tick pick again if it no longer exists or matches.
            _lists = lists;
            _valid = new();
            _wanted = null;
            Missing.Clear();
            var same = _current == null ? null : lists.FirstOrDefault(p => Same(p.Name, _current.Name));
            if (same == null) { Switch(null); return; }
            int index = _index;
            Begin(same);
            _index = Math.Min(index, _order.Count - 1);
            return;
        }
        SetName = setName;
        _lists = lists;
        _valid = new();
        Switch(null);
    }

    /// Re-evaluate which playlist should play (a few times a second).
    public static void Tick(GameState s)
    {
        var valid = _lists.Where(p => p.Enabled && p.When.Match(s)).ToList();
        if (_current != null && valid.IndexOf(_current) > 0)
        {
            valid.Remove(_current);
            valid.Insert(0, _current);
        }
        for (int i = 0; i < valid.Count; i++)
        {
            var p = valid[i];
            if ((p.PlayAfter ?? p.PlayNext) is string ahead && valid.FindIndex(x => Same(x.Name, ahead)) is int j && j > i)
            {
                var x = valid[j];
                valid.RemoveAt(j);
                valid.Insert(i, x);
            }
            else if (p.PlayBefore is string behind && valid.FindIndex(x => Same(x.Name, behind)) is int k && k >= 0 && k < i)
            {
                valid.RemoveAt(i);
                valid.Insert(k, p);
            }
        }
        _valid = valid;
        // A change must hold for SwitchDelay before the music follows it, so bounces and brief blips don't flicker
        // between playlists (user, 2026-10-29). The first pick after a silence starts at once.
        var want = _current != null && valid.Contains(_current) ? _current : valid.Count > 0 ? valid[0] : null;
        if (want == _current) { _wanted = null; return; }
        double now = Environment.TickCount64 / 1000.0;
        if (_current == null && want != null || want != _wanted) { _wanted = want; _wantedSince = now; }
        if (_current == null || now - _wantedSince >= SwitchDelay)
        {
            _wanted = null;
            Switch(want);
        }
    }

    /// Every frame: start the next track when nothing is playing.
    public static void Update(Settings cfg)
    {
        if (Player.Busy)
        {
            if (Player.FadingOut || Player.Playing) return;
            Player.Stop();
        }
        if (_current == null) return;

        _index++;
        if (_index >= _order.Count)
        {
            int at = _valid.IndexOf(_current);
            if (at >= 0 && at + 1 < _valid.Count) { Begin(_valid[at + 1]); _index = 0; }
            else if (_current.Loop) { Begin(_current); _index = 0; }    // reshuffles a shuffled playlist
            else
            {
                if (_current.DisableAfterPlay) _current.Enabled = false;
                var next = _current.PlayNext is string n ? _lists.FirstOrDefault(x => Same(x.Name, n)) : null;
                if (next != null) { next.Enabled = true; Begin(next); _index = 0; }
                else { _current = null; return; }
            }
        }
        if (_order.Count == 0) return;

        string name = _order[_index];
        string? path = name.StartsWith('@') ? Library.FromPool(name[1..]) : Library.Find(name);
        if (path == null)
        {
            if (Missing.Add(name)) Note.Write($"track '{name}' not found ({_current.Name})");
            return;                                     // try the next track next frame
        }
        Player.Play(path, Path.GetFileNameWithoutExtension(path), Prefs.Level, _index == 0 && cfg.FadeIn > 0 ? cfg.FadeIn : 0,
            Library.Gain(path));
    }

    private static void Switch(Playlist? p)
    {
        if (p == _current) return;
        Begin(p);
        _index = -1;
        Player.FadeOut(Settings.Current.FadeOut);
    }

    private static void Begin(Playlist? p)
    {
        _current = p;
        _order = p == null ? new() : p.Tracks.ToList();
        if (p is { Shuffle: true })
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

/// Track name → file, from every ogg under Music/ (DMO resolves tracks by file name, wherever they sit).
internal static class Library
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> Gains = new(StringComparer.Ordinal);    // by path; default 1
    private static readonly Dictionary<string, List<string>> Pools = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Queue<string>> Bags = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Random Rng = new();

    public static int Count => Files.Count;

    public static void Scan(string musicDir)
    {
        Files.Clear();
        Gains.Clear();
        Pools.Clear();                                   // a rescan (playlist reload) rebuilds pools from the cfgs
        Bags.Clear();
        ScanAlso(musicDir);
    }

    /// Adds a second folder's tracks (the player's own music) without clearing the first; on a name clash the first
    /// folder's file stays.
    public static void ScanAlso(string musicDir, float gain = 1f)
    {
        if (!Directory.Exists(musicDir)) return;
        foreach (var f in Directory.EnumerateFiles(musicDir, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".ogg" or ".mp3" or ".wav" && Files.TryAdd(Path.GetFileNameWithoutExtension(f), f) && gain != 1f)
                Gains[f] = gain;
        }
    }

    /// Loudness for a track's source folder (1 unless its folder was scanned with a gain).
    public static float Gain(string path) => Gains.TryGetValue(path, out var g) ? g : 1f;

    public static string? Find(string track) => Files.TryGetValue(track, out var p) ? p : null;

    /// A named pool of tracks played as a shuffle bag ("@pool" in a playlist), e.g. the OST space layer.
    public static void SetPool(string name, IEnumerable<string> tracks) =>
        Pools[name] = tracks.Where(t => Files.ContainsKey(t)).ToList();

    public static string? FromPool(string name)
    {
        if (!Pools.TryGetValue(name, out var pool) || pool.Count == 0) return null;
        if (!Bags.TryGetValue(name, out var bag) || bag.Count == 0)
            Bags[name] = bag = new Queue<string>(pool.OrderBy(_ => Rng.Next()));
        return Find(bag.Dequeue());
    }
}
