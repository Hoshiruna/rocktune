// SPDX-License-Identifier: MIT
using RockTune.Domain;

namespace RockTune.Midi;

public sealed record MidiNote(Rational StartTick, Rational EndTick, int Pitch, int Velocity, int Channel);

public sealed class MidiTrack(int number, string name, long endTick)
{
    public int Number { get; } = number;
    public string Name { get; set; } = name;
    public long EndTick { get; set; } = endTick;
    public List<MidiNote> Notes { get; } = [];
    public List<Report> Programs { get; } = [];
    public List<Report> Controls { get; } = [];
    public List<Report> Bends { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasPitchBends => Bends.Any(bend => (int)bend["value"]! != 0);
}

public sealed record MidiSong(string Path, int Format, List<MidiTrack> Tracks, TempoMap TempoMap)
{
    public long EndTick => Tracks.Max(track => track.EndTick);
}

public sealed class TempoMap
{
    private readonly long[] ticks;
    private readonly int[] tempos;
    private readonly Rational[] seconds;
    public int TicksPerBeat { get; }

    public TempoMap(int ticksPerBeat, IEnumerable<(long Tick, int Tempo)> changes)
    {
        if (ticksPerBeat <= 0)
        {
            throw new MusicException("SMPTE MIDI timing is not supported; use PPQN timing");
        }
        TicksPerBeat = ticksPerBeat;
        var explicitTempos = new Dictionary<long, int>();
        foreach (var (tick, tempo) in changes)
        {
            if (tempo <= 0)
            {
                throw new MusicException("MIDI tempo must be positive");
            }
            if (explicitTempos.TryGetValue(tick, out var previous) && previous != tempo)
            {
                throw new MusicException($"Conflicting MIDI tempo events at tick {tick}");
            }
            explicitTempos[tick] = tempo;
        }
        explicitTempos.TryAdd(0, 500000);
        ticks = explicitTempos.Keys.Order().ToArray();
        tempos = ticks.Select(tick => explicitTempos[tick]).ToArray();
        seconds = new Rational[ticks.Length];
        for (var index = 1; index < ticks.Length; index++)
        {
            seconds[index] = seconds[index - 1] + Delta(ticks[index] - ticks[index - 1], tempos[index - 1]);
        }
    }

    private Rational Delta(Rational deltaTicks, int tempo) => deltaTicks * tempo / ((Rational)TicksPerBeat * 1000000);

    public Rational At(Rational tick)
    {
        var index = Segment(ticks.Length, offset => (Rational)ticks[offset] <= tick);
        return seconds[index] + Delta(tick - ticks[index], tempos[index]);
    }

    public Rational TickAt(Rational time)
    {
        var index = Segment(seconds.Length, offset => seconds[offset] <= time);
        return ticks[index] + (time - seconds[index]) * TicksPerBeat * 1000000 / tempos[index];
    }

    private static int Segment(int count, Func<int, bool> atOrBefore)
    {
        var lower = 0;
        var upper = count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (atOrBefore(middle))
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }
        return Math.Max(0, lower - 1);
    }

    public List<Report> Report() => ticks.Select((tick, index) => new Report
    {
        ["tick"] = tick, ["seconds"] = seconds[index].ToDouble(),
        ["microseconds_per_beat"] = tempos[index], ["bpm"] = 60000000.0 / tempos[index]
    }).ToList();
}
