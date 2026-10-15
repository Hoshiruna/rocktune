// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using RockTune.Domain;

namespace RockTune.Midi;

public static class MidiArranger
{
    private sealed record Span(Rational Start, Rational End, int NoteIndex);
    private sealed record Reduction(List<MidiNote> Notes, List<Report> Provenance, List<Report> Reductions);

    public static MidiImportResult Arrange(MidiSong song, JsonElement plan)
    {
        try
        {
            return ArrangePlan(song, plan);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new MusicException("Invalid arrangement: " + error.Message);
        }
    }

    private static MidiImportResult ArrangePlan(MidiSong song, JsonElement plan)
    {
        var channels = plan.GetProperty("channels");
        if (channels.ValueKind != JsonValueKind.Object || !channels.EnumerateObject().Any() ||
            channels.EnumerateObject().Any(item => !MusicDefaults.Channels.Contains(item.Name)))
        {
            throw new MusicException("Arrangement needs SQ1, SQ2, TRI or NOI channel sections");
        }
        var duration = song.TempoMap.At(song.EndTick);
        var priorities = new Dictionary<int, int>();
        if (plan.TryGetProperty("drum_priority", out var drumPriority))
        {
            foreach (var pitch in drumPriority.EnumerateArray().Select(item => item.GetInt32()))
            {
                if (!priorities.TryAdd(pitch, priorities.Count))
                {
                    throw new MusicException("Drum priorities must not repeat a pitch");
                }
            }
        }
        var tracks = new List<MidiTrack>();
        var options = new ImportOptions
        {
            Fps = Number(plan, "fps", MusicDefaults.Fps),
            Loop = plan.TryGetProperty("loop", out var loop) && loop.GetBoolean()
        };
        var details = new Dictionary<string, (List<Report> Sections, List<Report> Sources)>();
        var used = new HashSet<int>();
        foreach (var channel in MusicDefaults.Channels)
        {
            if (!channels.TryGetProperty(channel, out var channelSections))
            {
                continue;
            }
            var track = new MidiTrack(tracks.Count + 1, channel, song.EndTick);
            var sources = new List<Report>();
            var sections = new List<Report>();
            Rational previousEnd = 0;
            foreach (var section in channelSections.EnumerateArray())
            {
                var number = section.GetProperty("track").GetInt32();
                var start = Number(section, "start", 0);
                var end = Number(section, "end", duration);
                var policy = section.TryGetProperty("voice", out var voice) ? voice.GetString()! : "strict";
                if (number < 1 || number > song.Tracks.Count || start < previousEnd || end <= start || end > duration)
                {
                    throw new MusicException($"{channel}: sections need valid tracks and ordered, nonoverlapping intervals within the song");
                }
                if (policy is not ("strict" or "highest" or "lowest" or "drum-priority"))
                {
                    throw new MusicException("Unknown voice policy: " + policy);
                }
                if (policy == "drum-priority" && channel != "NOI")
                {
                    throw new MusicException("drum-priority applies only to NOI");
                }
                var source = song.Tracks[number - 1];
                if (source.Warnings.Count > 0 || source.HasPitchBends)
                {
                    throw new MusicException($"Track {number} has unfinished notes or unsupported pitch bends");
                }
                var reduction = Reduce(source, song.TempoMap.TickAt(start), song.TempoMap.TickAt(end), policy, priorities, channel, song.TempoMap);
                track.Notes.AddRange(reduction.Notes);
                sources.AddRange(reduction.Provenance);
                sections.Add(new Report
                {
                    ["source_track"] = number, ["start_seconds"] = start.ToDouble(), ["end_seconds"] = end.ToDouble(),
                    ["voice_policy"] = policy, ["output_note_count"] = reduction.Notes.Count, ["reductions"] = reduction.Reductions
                });
                used.Add(number);
                previousEnd = end;
            }
            tracks.Add(track);
            options.Mapping[channel] = track.Number;
            details[channel] = (sections, sources);
        }
        if (plan.TryGetProperty("noise_map", out var noiseMap))
        {
            foreach (var item in noiseMap.EnumerateObject())
            {
                options.NoiseMap.Add(int.Parse(item.Name, CultureInfo.InvariantCulture), item.Value.GetInt32());
            }
        }
        if (plan.TryGetProperty("transpose", out var transpose))
        {
            foreach (var item in transpose.EnumerateObject())
            {
                options.Transpose.Add(item.Name, item.Value.GetInt32());
            }
        }
        var result = MidiImporter.Import(new MidiSong(song.Path, song.Format, tracks, song.TempoMap), options);
        var channelReports = (Dictionary<string, Report>)result.Report["channels"]!;
        foreach (var (channel, (sections, sources)) in details)
        {
            var report = channelReports[channel];
            report["track"] = null;
            report["source_tracks"] = sections.Select(section => (int)section["source_track"]!).Distinct().Order().ToArray();
            report["arrangement_sections"] = sections;
            var events = (List<Report>)report["events"]!;
            for (var index = 0; index < events.Count; index++)
            {
                foreach (var (key, value) in sources[index])
                {
                    events[index][key] = value;
                }
            }
        }
        result.Report["excluded_tracks"] = song.Tracks.Where(track => !used.Contains(track.Number)).Select(track => track.Number).ToArray();
        result.Report["voice_policy"] = "explicit section arrangement; every chord reduction is recorded";
        result.Report["arrangement_plan"] = plan.Clone();
        var text = string.Join('\n', result.Text.Split('\n')
            .Where(line => !(line.StartsWith("; MIDI track ", StringComparison.Ordinal) && line.Contains(" -> ", StringComparison.Ordinal))))
            .Replace("; MIDI track numbers are 1-based. %N means N NES update frames.",
                "; Arranged from multiple source tracks. %N means N NES update frames.", StringComparison.Ordinal);
        return result with { Text = text };
    }

    private static Rational Number(JsonElement parent, string name, Rational fallback)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return fallback;
        }
        return Rational.Parse(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
    }

    private static Reduction Reduce(MidiTrack track, Rational start, Rational end, string policy,
        Dictionary<int, int> priorities, string channel, TempoMap timeline)
    {
        if (track.Notes.Any(note => note.EndTick <= note.StartTick && start <= note.StartTick && note.StartTick < end))
        {
            throw new MusicException($"Track {track.Number} has a zero-length note");
        }
        var spans = new List<Span>();
        var reductions = new List<Report>();
        foreach (var interval in NoteIntervals.Enumerate(track.Notes, start, end))
        {
            var active = interval.Active;
            if (active.Length == 0)
            {
                continue;
            }
            if (active.Length > 1 && policy == "strict")
            {
                throw new MusicException(string.Create(CultureInfo.InvariantCulture,
                    $"{channel} source track {track.Number} overlaps at {timeline.At(interval.Start).ToDouble():F6}s; choose a voice policy"));
            }
            if (policy == "drum-priority")
            {
                var missing = active.Select(index => track.Notes[index].Pitch).Where(pitch => !priorities.ContainsKey(pitch)).Distinct().Order().ToArray();
                if (missing.Length > 0)
                {
                    throw new MusicException("Drum priority list is missing pitches: [" + string.Join(", ", missing) + "]");
                }
            }
            var chosen = policy switch
            {
                "highest" => active.OrderByDescending(index => track.Notes[index].Pitch).ThenByDescending(index => track.Notes[index].StartTick).ThenBy(index => index).First(),
                "lowest" => active.OrderBy(index => track.Notes[index].Pitch).ThenByDescending(index => track.Notes[index].StartTick).ThenBy(index => index).First(),
                "drum-priority" => active.OrderBy(index => priorities[track.Notes[index].Pitch]).ThenByDescending(index => track.Notes[index].StartTick).ThenBy(index => index).First(),
                _ => active.Min()
            };
            if (spans.Count > 0 && spans[^1].NoteIndex == chosen && spans[^1].End == interval.Start)
            {
                spans[^1] = spans[^1] with { End = interval.End };
            }
            else
            {
                spans.Add(new Span(interval.Start, interval.End, chosen));
            }
            if (active.Length > 1)
            {
                reductions.Add(new Report
                {
                    ["start_tick"] = interval.Start.TickValue(), ["end_tick"] = interval.End.TickValue(),
                    ["start_seconds"] = timeline.At(interval.Start).ToDouble(), ["end_seconds"] = timeline.At(interval.End).ToDouble(),
                    ["retained_pitch"] = track.Notes[chosen].Pitch,
                    ["omitted_pitches"] = active.Where(index => index != chosen).Select(index => track.Notes[index].Pitch).Order().ToArray()
                });
            }
        }
        var notes = new List<MidiNote>();
        var provenance = new List<Report>();
        foreach (var span in spans)
        {
            var original = track.Notes[span.NoteIndex];
            notes.Add(original with { StartTick = span.Start, EndTick = span.End });
            provenance.Add(new Report
            {
                ["source_track"] = track.Number, ["original_start_tick"] = original.StartTick.TickValue(),
                ["original_end_tick"] = original.EndTick.TickValue(),
                ["shortened_or_split"] = span.Start != original.StartTick || span.End != original.EndTick
            });
        }
        return new Reduction(notes, provenance, reductions);
    }
}
