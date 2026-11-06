// One music stream at a time on KSA's Music channel group (so KSA's Music slider applies), with fade in and out.
// Streams are opened when a track starts and released when it ends; DMO is ~150 files and only one plays.

using System.Reflection;
using System.Text;
using Brutal.FmodApi;
using Brutal.Logging;
using KSA;

namespace KSAMusic;

internal static class Player
{
    private static Sound _sound;
    private static Channel _channel;
    private static bool _open;
    private static float _fade = 1f, _fadeRate;          // fade level 0..1 and its change per second
    private static ChannelGroup? _music;
    private static bool _closed;                         // FMOD is being shut down

    public static string? Track { get; private set; }
    public static bool Busy => _open;
    public static bool FadingOut => _open && _fadeRate < 0;

    public static bool Playing
    {
        get
        {
            if (!_open) return false;
            if (_channel.TryIsPlaying(out var playing) != Result.Ok) return false;
            return playing;
        }
    }

    private static float _gain = 1f;                     // per-source loudness (KSA's own tracks are mastered quieter)

    public static bool Play(string path, string name, float volume, float fadeInSeconds, float gain = 1f)
    {
        Stop();
        _gain = gain;
        if (_closed) return false;
        var group = MusicGroup();
        if (group == null) return false;
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        var r = GameAudio.System.TryCreateStream(bytes, Mode._2d | Mode.LoopOff, new CreateSoundExInfo(), out _sound);
        if (r != Result.Ok)
        {
            Note.Write($"cannot open {path}: {r}");
            return false;
        }
        if (GameAudio.System.TryPlaySound(_sound, group.Value, true, out _channel) is var pr && pr != Result.Ok)
        {
            Note.Write($"cannot play {path}: {pr}");
            _sound.TryRelease();
            return false;
        }
        _open = true;
        Track = name;
        _fade = fadeInSeconds > 0 ? 0f : 1f;
        _fadeRate = fadeInSeconds > 0 ? 1f / fadeInSeconds : 0f;
        _channel.TrySetVolume(volume * _gain * _fade);
        _channel.TrySetPaused(false);
        Note.Write($"play {name} at {volume * _gain:0.##}");
        return true;
    }

    public static void FadeOut(float seconds)
    {
        if (!_open) return;
        if (seconds <= 0) { Stop(); return; }
        _fadeRate = -1f / seconds;
    }

    public static void Update(double dt, float volume, bool paused)
    {
        if (!_open || _closed) return;
        _fade = Math.Clamp(_fade + _fadeRate * (float)dt, 0f, 1f);
        if (_fadeRate > 0 && _fade >= 1f) _fadeRate = 0;
        if (_fadeRate < 0 && _fade <= 0f) { Stop(); return; }
        _channel.TrySetVolume(volume * _gain * _fade);
        _channel.TrySetPaused(paused);
    }

    public static void Stop()
    {
        if (!_open || _closed) return;
        _channel.TryStop();
        _sound.TryRelease();
        _open = false;
        Track = null;
    }

    /// GameAudio.Release prefix: the last FMOD calls we make. Nothing plays after this.
    public static void Shutdown()
    {
        Stop();
        _closed = true;
    }

    /// Drop the handles without touching FMOD (after KSA has released it).
    public static void Forget()
    {
        _open = false;
        Track = null;
        _music = null;
    }

    private static ChannelGroup? MusicGroup()
    {
        if (_music != null) return _music;
        var p = typeof(GameAudio).GetProperty("MusicGroup", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (p?.GetValue(null) is ChannelGroup g) _music = g;
        else Note.Write("KSA's Music channel group not found");
        return _music;
    }
}
