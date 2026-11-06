// KSAMusic's own log (KSAMusic.log beside the dll), mirrored to the game log. The game log misses lines written
// before KSA sets up its logger (StarMap's BeforeMain), and a log of every playlist and track change is what
// tuning the music needs.

using Brutal.Logging;

namespace KSAMusic;

internal static class Note
{
    private static StreamWriter? _file;
    private static int _lines;

    public static void Open(string path)
    {
        try { _file ??= new StreamWriter(path, append: false) { AutoFlush = true }; }
        catch { _file = null; }
    }

    public static void Write(string line)
    {
        if (++_lines > 5000) return;                     // a fault repeating every frame must not fill the disk
        try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}"); } catch { }
        try { DefaultCategory.Log.Info($"[KSAMusic] {line}"); } catch { }
    }
}
