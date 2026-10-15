// SPDX-License-Identifier: MIT
using RockTune.Domain;

namespace RockTune.Midi;

internal sealed record NoteInterval(Rational Start, Rational End, int[] Active);

internal static class NoteIntervals
{
    public static IEnumerable<NoteInterval> Enumerate(IReadOnlyList<MidiNote> notes, Rational start, Rational end)
    {
        var boundaries = new SortedDictionary<Rational, List<(bool Starting, int Index)>>();
        void Add(Rational tick, bool starting, int index)
        {
            if (!boundaries.TryGetValue(tick, out var changes))
            {
                changes = [];
                boundaries[tick] = changes;
            }
            changes.Add((starting, index));
        }
        for (var index = 0; index < notes.Count; index++)
        {
            var begin = Rational.Max(start, notes[index].StartTick);
            var finish = Rational.Min(end, notes[index].EndTick);
            if (begin < finish)
            {
                Add(begin, true, index);
                Add(finish, false, index);
            }
        }
        var active = new SortedSet<int>();
        var ticks = boundaries.Keys.ToArray();
        for (var offset = 0; offset + 1 < ticks.Length; offset++)
        {
            var tick = ticks[offset];
            foreach (var change in boundaries[tick].Where(change => !change.Starting))
            {
                active.Remove(change.Index);
            }
            foreach (var change in boundaries[tick].Where(change => change.Starting))
            {
                active.Add(change.Index);
            }
            yield return new NoteInterval(tick, ticks[offset + 1], active.ToArray());
        }
    }
}
