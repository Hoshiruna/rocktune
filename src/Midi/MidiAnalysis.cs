// SPDX-License-Identifier: MIT
namespace RockTune.Midi;

public static class MidiAnalysis
{
    public static Report Analyze(MidiSong song)
    {
        var tracks = new List<Report>();
        foreach (var track in song.Tracks)
        {
            var intervals = NoteIntervals.Enumerate(track.Notes, 0, track.EndTick).ToArray();
            var overlaps = intervals.Where(interval => interval.Active.Length > 1).Select(interval => new Report
            {
                ["start_tick"] = interval.Start.TickValue(), ["end_tick"] = interval.End.TickValue(),
                ["pitches"] = interval.Active.Select(index => track.Notes[index].Pitch).Order().ToArray(),
                ["start_seconds"] = song.TempoMap.At(interval.Start).ToDouble(),
                ["end_seconds"] = song.TempoMap.At(interval.End).ToDouble()
            }).ToList();
            tracks.Add(new Report
            {
                ["track"] = track.Number, ["name"] = track.Name, ["note_count"] = track.Notes.Count,
                ["midi_channels"] = track.Notes.Select(note => note.Channel + 1).Distinct().Order().ToArray(),
                ["pitch_range"] = track.Notes.Count == 0 ? null : new[] { track.Notes.Min(note => note.Pitch), track.Notes.Max(note => note.Pitch) },
                ["programs"] = track.Programs, ["maximum_polyphony"] = intervals.Select(interval => interval.Active.Length).DefaultIfEmpty().Max(),
                ["overlap_intervals"] = overlaps, ["warnings"] = track.Warnings, ["controllers"] = track.Controls, ["pitch_bends"] = track.Bends
            });
        }
        return new Report
        {
            ["source_midi"] = song.Path, ["format"] = song.Format, ["ticks_per_beat"] = song.TempoMap.TicksPerBeat,
            ["duration_seconds"] = song.TempoMap.At(song.EndTick).ToDouble(),
            ["track_numbering"] = "1-based file tracks, including any conductor track",
            ["tempo_changes"] = song.TempoMap.Report(), ["tracks"] = tracks
        };
    }
}
