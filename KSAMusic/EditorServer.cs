// The playlist editor, served by the mod (user, 2026-11-06: ship the drag-and-drop editor with the release, with
// their preset, and let players edit it). Music > Edit playlists... starts a tiny web server on 127.0.0.1 and opens
// Editor/playlist_editor.html in the player's browser: tracks on the left, situations on the right.
//
// Routes (the same as tools/playlist_editor.py, so the page is shared):
//   GET  /               the page (Editor/playlist_editor.html in the mod folder)
//   GET  /library.json   every track KSAMusic can play: the mod's Music/ (credited in Music/credits.json) and the
//                        player's own <profile>/KSAMusic/Music
//   GET  /state.json     the player's saved picks, else the shipped preset
//   GET  /preset.json    the shipped preset (Playlists/preset.json)
//   GET  /audio/<id>     a track, for the preview (with byte ranges)
//   POST /save           writes <profile>/KSAMusic/Playlists/{assignments.json,menu.cfg,kerbol.cfg,sol.cfg}; the
//                        next audio update reloads them (main thread)
// A plain TcpListener on the loopback address, not HttpListener (which wants admin rights on Windows). Only those
// four fixed file names are ever written, and every value is cleaned before it goes into a cfg.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KSAMusic;

internal static class EditorServer
{
    private static TcpListener? _listener;
    private static int _port;

    public static string? Url => _listener == null ? null : $"http://127.0.0.1:{_port}/";

    /// Start (once) and open the editor in the player's browser.
    public static void Open()
    {
        try
        {
            if (_listener == null)
            {
                for (int port = 8767; port < 8790 && _listener == null; port++)
                {
                    try
                    {
                        var l = new TcpListener(IPAddress.Loopback, port);
                        l.Start();
                        _listener = l;
                        _port = port;
                    }
                    catch (SocketException)
                    {
                        // taken: try the next one
                    }
                }
                if (_listener == null)
                {
                    Note.Write("playlist editor: no free port between 8767 and 8789");
                    return;
                }
                var t = new Thread(Serve) { IsBackground = true, Name = "KSAMusic editor" };
                t.Start();
                Note.Write($"playlist editor on {Url}");
            }
            Process.Start(new ProcessStartInfo(Url!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Note.Write($"playlist editor: {ex.Message}");
        }
    }

    private static void Serve()
    {
        while (_listener != null)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { using (client) Handle(client.GetStream()); }
                catch (Exception ex) { Note.Write($"playlist editor request: {ex.Message}"); }
            });
        }
    }

    // --- HTTP (just enough of it) -----------------------------------------------------------------------------------

    private static void Handle(NetworkStream net)
    {
        net.ReadTimeout = 10000;
        var head = new StringBuilder();
        var buf = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16384)
        {
            if (net.Read(buf, 0, 1) <= 0) return;
            head.Append((char)buf[0]);
        }
        string[] lines = head.ToString().Split("\r\n");
        string[] request = lines[0].Split(' ');
        if (request.Length < 2) return;
        string method = request[0], path = Uri.UnescapeDataString(request[1].Split('?')[0]);
        var headers = lines.Skip(1).Where(l => l.Contains(':'))
            .ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());

        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int n) && n > 0 && n < 8 << 20)
        {
            body = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = net.Read(body, got, n - got);
                if (r <= 0) return;
                got += r;
            }
        }

        // Only our own page: a web page elsewhere in the player's browser could otherwise reach this server (a
        // cross-site POST, or DNS rebinding), so the Host must be ours, and a save must come from our own origin.
        string host = headers.GetValueOrDefault("host", "");
        if (host != $"127.0.0.1:{_port}" && host != $"localhost:{_port}")
        {
            Send(net, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden"));
            return;
        }
        if (method == "POST" && headers.TryGetValue("origin", out var origin) &&
            origin != $"http://127.0.0.1:{_port}" && origin != $"http://localhost:{_port}")
        {
            Send(net, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden"));
            return;
        }

        if (method == "GET" && path is "/" or "/index.html")
            Send(net, 200, "text/html; charset=utf-8", File.ReadAllBytes(Path.Combine(Music.ModDir, "Editor", "playlist_editor.html")));
        else if (method == "GET" && path == "/library.json")
            Send(net, 200, "application/json", Encoding.UTF8.GetBytes(LibraryJson()));
        else if (method == "GET" && path == "/state.json")
            Send(net, 200, "application/json", ReadOr(Path.Combine(Music.UserDir, "Playlists", "assignments.json"), PresetPath));
        else if (method == "GET" && path == "/preset.json")
            Send(net, 200, "application/json", ReadOr(PresetPath, null));
        else if (method == "GET" && path == "/bodies.json")
            Send(net, 200, "application/json", Encoding.UTF8.GetBytes(_bodiesJson ?? "null"));
        else if (method == "GET" && path.StartsWith("/audio/", StringComparison.Ordinal))
            SendAudio(net, path["/audio/".Length..], headers.GetValueOrDefault("range"));
        else if (method == "POST" && path == "/save")
            Send(net, 200, "application/json", Encoding.UTF8.GetBytes(Save(body)));
        else
            Send(net, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
    }

    private static string PresetPath => Path.Combine(Music.ModDir, "Playlists", "preset.json");

    private static byte[] ReadOr(string path, string? fallback) =>
        File.Exists(path) ? File.ReadAllBytes(path)
        : fallback != null && File.Exists(fallback) ? File.ReadAllBytes(fallback)
        : Encoding.UTF8.GetBytes("null");

    private static void Send(NetworkStream net, int code, string type, byte[] data, string? extra = null)
    {
        string status = code switch { 200 => "OK", 206 => "Partial Content", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {code} {status}\r\nContent-Type: {type}\r\nContent-Length: {data.Length}\r\n" +
                   $"Cache-Control: no-store\r\nConnection: close\r\n{extra}\r\n";
        net.Write(Encoding.ASCII.GetBytes(head));
        net.Write(data);
    }

    private static void SendAudio(NetworkStream net, string id, string? range)
    {
        if (!Tracks().TryGetValue(id, out var t))
        {
            Send(net, 404, "text/plain", Encoding.UTF8.GetBytes("no such track"));
            return;
        }
        var info = new FileInfo(t.File);
        long size = info.Length, start = 0, end = size - 1;
        if (range is { } r && r.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var parts = r[6..].Split('-');
            if (long.TryParse(parts[0], out var a)) start = a;
            if (parts.Length > 1 && long.TryParse(parts[1], out var b)) end = Math.Min(b, size - 1);
        }
        start = Math.Clamp(start, 0, Math.Max(0, size - 1));
        using var fs = File.OpenRead(t.File);
        fs.Seek(start, SeekOrigin.Begin);
        var data = new byte[Math.Max(0, end - start + 1)];
        int read = 0;
        while (read < data.Length)
        {
            int got = fs.Read(data, read, data.Length - read);
            if (got <= 0) break;
            read += got;
        }
        string type = Path.GetExtension(t.File).ToLowerInvariant() switch { ".mp3" => "audio/mpeg", ".wav" => "audio/wav", _ => "audio/ogg" };
        Send(net, range != null ? 206 : 200, type, data,
            "Accept-Ranges: bytes\r\n" + (range != null ? $"Content-Range: bytes {start}-{end}/{size}\r\n" : ""));
    }

    // --- the library ------------------------------------------------------------------------------------------------

    private sealed record Track(string Id, string File, JsonObject Meta);

    /// Every playable file: the mod's Music/ first (its credits from Music/credits.json), then the player's own.
    private static Dictionary<string, Track> Tracks()
    {
        var credits = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        string creditsFile = Path.Combine(Music.ModDir, "Music", "credits.json");
        try
        {
            if (File.Exists(creditsFile) && JsonNode.Parse(File.ReadAllText(creditsFile))?["tracks"] is JsonArray arr)
                foreach (var n in arr.OfType<JsonObject>())
                    if (n["id"]?.GetValue<string>() is string id)
                        credits[id] = n;
        }
        catch (Exception ex)
        {
            Note.Write($"playlist editor: credits.json unreadable: {ex.Message}");
        }

        var tracks = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        void Add(string root, bool own, bool game = false)
        {
            if (!Directory.Exists(root)) return;
            foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".ogg" or ".mp3" or ".wav")) continue;
                string id = Path.GetFileNameWithoutExtension(f);
                if (tracks.ContainsKey(id)) continue;
                JsonObject meta;
                if (game)
                    meta = new JsonObject
                    {
                        ["id"] = id, ["title"] = GameTitle(id), ["album"] = "Kitten Space Agency (your game)",
                        ["artist"] = "RocketWerkz", ["license"] = "KSA's own music, played from your game",
                    };
                else if (!own && credits.TryGetValue(id, out var c))
                    meta = (JsonObject)c.DeepClone();
                else
                {
                    string folder = Path.GetRelativePath(root, Path.GetDirectoryName(f)!);
                    meta = new JsonObject
                    {
                        ["id"] = id, ["title"] = id,
                        ["album"] = own ? (folder == "." ? "Your music" : "Your music: " + folder) : (folder == "." ? "Other tracks" : folder),
                        ["artist"] = "", ["license"] = own ? "your file" : "",
                    };
                }
                meta["id"] = id;
                meta["pack"] = !own && !game;
                meta["own"] = own;
                meta["game"] = game;
                if (meta["duration"] == null && ext == ".ogg")
                    meta["duration"] = OggSeconds(f);
                tracks[id] = new Track(id, f, meta);
            }
        }
        Add(Path.Combine(Music.ModDir, "Music"), own: false);
        Add(Path.Combine(Music.UserDir, "Music"), own: true);
        Add(Music.GameMusicDir, own: false, game: true);
        return tracks;
    }

    /// "05_LEOSunrise" -> "LEO Sunrise": KSA's file names, readable.
    private static string GameTitle(string id)
    {
        string t = id.Length > 3 && char.IsDigit(id[0]) && char.IsDigit(id[1]) && id[2] == '_' ? id[3..] : id;
        return System.Text.RegularExpressions.Regex.Replace(t, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
    }

    private static string LibraryJson()
    {
        var arr = new JsonArray();
        foreach (var t in Tracks().Values)
            arr.Add(t.Meta.DeepClone());
        return new JsonObject
        {
            ["tracks"] = arr,
            ["ownMusicDir"] = Path.Combine(Music.UserDir, "Music"),
            ["replaceWarning"] = ReplaceWarning(),
        }.ToJsonString();
    }

    /// Length of an Ogg Vorbis file: the last page's granule position over the sample rate in the identification
    /// header. Null when it isn't plain Vorbis.
    private static double? OggSeconds(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var headBuf = new byte[64];
            if (fs.Read(headBuf, 0, headBuf.Length) < 64) return null;
            int vorbis = IndexOf(headBuf, "\x01vorbis"u8.ToArray());
            if (vorbis < 0) return null;
            int rate = BitConverter.ToInt32(headBuf, vorbis + 12);
            if (rate <= 0) return null;
            int tail = (int)Math.Min(fs.Length, 65536);
            fs.Seek(-tail, SeekOrigin.End);
            var end = new byte[tail];
            fs.ReadExactly(end, 0, tail);
            for (int i = tail - 14; i >= 0; i--)
                if (end[i] == 'O' && end[i + 1] == 'g' && end[i + 2] == 'g' && end[i + 3] == 'S')
                    return Math.Round(BitConverter.ToInt64(end, i + 6) / (double)rate, 1);
        }
        catch
        {
            // no duration then
        }
        return null;
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    // --- bodies -----------------------------------------------------------------------------------------------------
    //
    // "Add a place" picks from the bodies actually in the game, mods included (user, 2026-11-06: "don't want
    // misspelling to break things"). Taken on the main thread once a universe exists and whenever it changes: every
    // registered system's bodies from its template (kind and parent from the templates), the loaded one first. Also
    // written to <profile>/KSAMusic/bodies.json for the development editor (tools/playlist_editor.py).

    private static volatile string? _bodiesJson;          // built on the main thread, read as-is by the server
    private static string? _bodiesFor;

    public static void SnapshotBodies()
    {
        string? current = KSA.Universe.CurrentSystem?.Id;
        if (current == null || current == _bodiesFor) return;
        _bodiesFor = current;
        try
        {
            var systems = new JsonArray();
            var ids = KSA.SelectSystem.Systems.Select(x => x.Id).Distinct().OrderBy(x => x == current ? 0 : 1).ToList();
            foreach (string id in ids)
            {
                var info = KSA.SelectSystem.Systems.FirstOrDefault(x => x.Id == id);
                var template = KSA.SystemLibrary.Find(id);
                if (template == null) continue;
                var bodies = new List<(string Id, string Parent, string Kind)>();
                foreach (var t in template.Bodies)
                {
                    KSA.AstronomicalTemplate r = t;
                    try { r = (t as KSA.AstronomicalReference)?.Resolve() ?? t; } catch { }
                    string kind = r switch
                    {
                        KSA.VehicleTemplate or KSA.BarycenterTemplate => "",
                        KSA.StellarBodyTemplate => "star",
                        _ => "body",
                    };
                    if (kind == "" || string.IsNullOrEmpty(t.Id)) continue;
                    string parent = !string.IsNullOrEmpty(t.ParentId) ? t.ParentId : r.ParentId ?? "";
                    bodies.Add((t.Id, parent, kind));
                }
                // planet or moon: a body whose parent is a star (or nothing known) is a planet, else a moon
                var stars = bodies.Where(b => b.Kind == "star").Select(b => b.Id).ToHashSet();
                var arr = new JsonArray();
                foreach (var b in bodies)
                    arr.Add(new JsonObject
                    {
                        ["id"] = b.Id, ["parent"] = b.Parent,
                        ["kind"] = b.Kind == "star" ? "star" : stars.Contains(b.Parent) || b.Parent == "" ? "planet" : "moon",
                    });
                systems.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = info?.GetName() is { Length: > 0 } n ? n : id, ["loaded"] = id == current, ["bodies"] = arr,
                });
            }
            var snap = new JsonObject { ["current"] = current, ["systems"] = systems };
            _bodiesJson = snap.ToJsonString();
            string pretty = snap.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), file = Path.Combine(Music.UserDir, "bodies.json");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Music.UserDir);
                    File.WriteAllText(file, pretty);
                }
                catch (Exception ex)
                {
                    Note.Write($"playlist editor: couldn't write {file}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Note.Write($"playlist editor: body list failed: {ex.Message}");
        }
    }

    // --- saving -----------------------------------------------------------------------------------------------------

    /// When the playlists in use weren't made with the editor (no shipped preset, nothing saved yet: e.g. a hand-made
    /// DMO setup), a first save would replace them all. Say so, so the editor can ask first. Null when it's safe.
    private static string? ReplaceWarning()
    {
        string user = Path.Combine(Music.UserDir, "Playlists");
        if (File.Exists(Path.Combine(user, "assignments.json")) || File.Exists(PresetPath))
            return null;
        string pl = File.Exists(Path.Combine(user, "kerbol.cfg")) || File.Exists(Path.Combine(user, "menu.cfg"))
            ? user : Path.Combine(Music.ModDir, "Playlists");
        int count = 0;
        try
        {
            foreach (var f in Directory.Exists(pl) ? Directory.GetFiles(pl, "*.cfg") : Array.Empty<string>())
                count += File.ReadLines(f).Count(l => l.Trim().Equals("playlist", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // count stays a best guess
        }
        if (count == 0) return null;
        return $"Saving replaces the {count} hand-written playlists now in use (from {pl}). " +
               $"To go back to them later, delete the folder {user}.";
    }

    private static string Save(byte[] body)
    {
        try
        {
            var state = JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("not a JSON object");
            if (ReplaceWarning() is string warning && state["replaceHandWritten"]?.GetValue<bool>() != true)
                return new JsonObject { ["saved"] = false, ["needsConfirm"] = true, ["error"] = warning }.ToJsonString();
            state.Remove("replaceHandWritten");
            var known = Tracks();
            var dir = Path.Combine(Music.UserDir, "Playlists");
            Directory.CreateDirectory(dir);
            var header = "// KSAMusic playlists, written by the playlist editor (Music > Edit playlists...). Edit them there.\n" +
                         "// First matching playlist wins, so specific situations come first.\n";
            var menu = new StringBuilder(header);
            var world = new StringBuilder(header);
            var sol = new StringBuilder(header);
            bool solFlightToKsa = state["solFlightToKsa"]?.GetValue<bool>() == true;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in (state["cards"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var tracks = (card["tracks"] as JsonArray ?? new JsonArray())
                    .Select(t => t?.GetValue<string>()).OfType<string>().Where(known.ContainsKey).ToList();
                if (tracks.Count == 0) continue;
                used.UnionWith(tracks);
                var when = card["when"] as JsonObject ?? new JsonObject();
                bool isMenu = card["file"]?.GetValue<string>() == "menu";
                bool flight = Clean(when["scene"]?.ToString()) == "Flight";
                string text = Playlist(card, when, tracks);
                (isMenu ? menu : world).Append(text);
                if (!isMenu && !(solFlightToKsa && flight)) sol.Append(text);
            }
            File.WriteAllText(Path.Combine(dir, "menu.cfg"), menu.ToString());
            File.WriteAllText(Path.Combine(dir, "kerbol.cfg"), world.ToString());
            File.WriteAllText(Path.Combine(dir, "sol.cfg"), sol.ToString());
            File.WriteAllText(Path.Combine(dir, "assignments.json"), state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Music.ReloadRequested = true;
            Note.Write($"playlist editor: saved {used.Count} tracks to {dir}");
            return new JsonObject { ["saved"] = true, ["tracks"] = used.Count, ["dir"] = dir }.ToJsonString();
        }
        catch (Exception ex)
        {
            Note.Write($"playlist editor: save failed: {ex.Message}");
            return new JsonObject { ["saved"] = false, ["error"] = ex.Message }.ToJsonString();
        }
    }

    private static string Playlist(JsonObject card, JsonObject when, List<string> tracks)
    {
        string B(string key, bool def) => (card[key]?.GetValue<bool>() ?? def) ? "True" : "False";
        var sb = new StringBuilder();
        sb.Append("playlist\n{\n");
        sb.Append($"\tname = {Clean(card["title"]?.ToString()) ?? "Playlist"}\n");
        sb.Append($"\tloop = {B("loop", true)}\n\tshuffle = {B("shuffle", false)}\n\tpauseOnGamePause = {B("pauseOnGamePause", true)}\n");
        sb.Append("\ttracks\n\t{\n");
        foreach (var t in tracks) sb.Append($"\t\ttrack = {t}\n");
        sb.Append("\t}\n\tplayWhen\n\t{\n");
        foreach (var (key, value) in when)
        {
            string? k = Clean(key), v = value is JsonValue jv && jv.TryGetValue<bool>(out bool b) ? (b ? "True" : "False") : Clean(value?.ToString());
            if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v) && k.All(char.IsLetterOrDigit))
                sb.Append($"\t\t{k} = {v}\n");
        }
        sb.Append("\t}\n}\n");
        return sb.ToString();
    }

    /// Keeps a value on one line and out of the cfg syntax (no braces, no comments).
    private static string? Clean(string? s)
    {
        if (s == null) return null;
        var c = new string(s.Where(ch => ch is not ('{' or '}' or '\r' or '\n')).ToArray()).Replace("//", "/").Trim();
        return c.Length > 120 ? c[..120] : c;
    }
}
