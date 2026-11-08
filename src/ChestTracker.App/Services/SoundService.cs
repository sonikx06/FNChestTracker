using System.IO;
using System.Media;

namespace ChestTracker.App.Services;

public enum SoundCue
{
    ChestAdded,
    ChestRemoved,
    MatchStarted,
    MatchEnded,
    Uncertain,
    Teammate,
}

/// <summary>Short synthesized notification tones (no audio files, no extra dependencies).</summary>
public sealed class SoundService : IDisposable
{
    private const int SampleRate = 22050;
    private readonly Dictionary<(SoundCue, int), SoundPlayer> _cache = new();

    public bool Enabled { get; set; } = true;

    public double Volume { get; set; } = 0.5;

    public void Play(SoundCue cue)
    {
        if (!Enabled || Volume <= 0.001)
        {
            return;
        }

        var volumeKey = (int)Math.Round(Volume * 20);
        try
        {
            if (!_cache.TryGetValue((cue, volumeKey), out var player))
            {
                player = new SoundPlayer(new MemoryStream(Build(cue, volumeKey / 20.0)));
                player.Load();
                _cache[(cue, volumeKey)] = player;
            }

            player.Play();
        }
        catch
        {
            // Audio device problems must never affect tracking.
        }
    }

    private static byte[] Build(SoundCue cue, double volume)
    {
        // (frequency Hz, duration s) notes
        (double F, double D)[] notes = cue switch
        {
            SoundCue.ChestAdded => [(987.77, 0.06), (1318.51, 0.11)],
            SoundCue.ChestRemoved => [(659.25, 0.07), (493.88, 0.12)],
            SoundCue.MatchStarted => [(523.25, 0.08), (659.25, 0.08), (783.99, 0.14)],
            SoundCue.MatchEnded => [(783.99, 0.08), (659.25, 0.08), (523.25, 0.16)],
            SoundCue.Teammate => [(698.46, 0.05), (880.0, 0.09)],
            _ => [(880, 0.06), (880, 0.06)],
        };

        var samples = new List<short>();
        foreach (var (f, d) in notes)
        {
            var count = (int)(SampleRate * d);
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / SampleRate;
                var attack = Math.Min(1, i / (SampleRate * 0.004));
                var release = Math.Exp(-4.5 * t / d);
                var wave = Math.Sin(2 * Math.PI * f * t) + (0.25 * Math.Sin(4 * Math.PI * f * t));
                samples.Add((short)(wave / 1.25 * attack * release * volume * 0.6 * short.MaxValue));
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            w.Write(s);
        }

        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        foreach (var p in _cache.Values)
        {
            p.Dispose();
        }

        _cache.Clear();
    }
}
