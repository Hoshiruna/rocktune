// SPDX-License-Identifier: MIT
using System.Globalization;
using RockTune.Domain;
using RockTune.Mml;

namespace RockTune.Midi;

public sealed class ImportOptions
{
    public Dictionary<string, int> Mapping { get; } = [];
    public Dictionary<int, int> NoiseMap { get; } = [];
    public Dictionary<string, int> Transpose { get; } = [];
    public Rational Start { get; set; }
    public Rational? End { get; set; }
    public Rational Fps { get; set; } = MusicDefaults.Fps;
    public bool Loop { get; set; }
}

public sealed record MidiImportResult(string Text, Report Report);

public static class MidiImporter
{
    private static readonly string[] PitchNames = ["c", "c+", "d", "d+", "e", "f", "f+", "g", "g+", "a", "a+", "b"];
    private static readonly Dictionary<string, string> ChannelSettings = new()
    {
        ["SQ1"] = "@d2 v12 @env0,0", ["SQ2"] = "@d1 v10 @env0,0", ["TRI"] = "", ["NOI"] = "@d0 v10 @env1,2"
    };
    private sealed record SelectedNote(Rational Onset, Rational Release, MidiNote Note, bool Clipped);

    public static MidiImportResult Import(MidiSong song, ImportOptions options)
    {
        var start = options.Start;
        var end = options.End ?? song.TempoMap.At(song.EndTick);
        var fps = options.Fps;
        if (fps < 1 || fps > 240 || start < 0 || end <= start || end > song.TempoMap.At(song.EndTick))
        {
            throw new MusicException("Use FPS 1..240 and a positive interval within the MIDI duration");
        }
        if (options.Mapping.Count == 0 || options.Mapping.Keys.Any(channel => !MusicDefaults.Channels.Contains(channel)))
        {
            throw new MusicException("Map at least one of SQ1, SQ2, TRI, NOI to a MIDI track");
        }
        if (options.Transpose.Keys.Any(channel => !options.Mapping.ContainsKey(channel)))
        {
            throw new MusicException("Transpose settings must refer to mapped channels");
        }
        if (options.NoiseMap.Any(item => item.Key is < 0 or > 127 || item.Value is < 1 or > 15))
        {
            throw new MusicException("Noise mappings require MIDI pitches 0..127 and NES noise numbers 1..15");
        }
        var startFrame = Rational.FrameAt(start, fps);
        var totalFrames = Rational.FrameAt(end, fps) - startFrame;
        if (totalFrames < 1)
        {
            throw new MusicException("Selected interval lasts less than one frame");
        }
        var title = Path.GetFileNameWithoutExtension(song.Path).Replace(';', ' ').Replace('\n', ' ');
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"#title {title} ({start.ToDouble():F3}-{end.ToDouble():F3}s)"),
            "#fps " + fps,
            "; MIDI track numbers are 1-based. %N means N NES update frames.",
            "; NES timbres are fixed; MIDI programs, velocity and other controllers are not reproduced."
        };
        var reports = new Dictionary<string, Report>();
        foreach (var channel in MusicDefaults.Channels)
        {
            if (!options.Mapping.TryGetValue(channel, out var number))
            {
                continue;
            }
            if (number < 1 || number > song.Tracks.Count)
            {
                throw new MusicException($"MIDI track {number} does not exist");
            }
            var track = song.Tracks[number - 1];
            if (track.Warnings.Count > 0)
            {
                throw new MusicException($"Track {number} has malformed or unfinished notes: {track.Warnings[0]}");
            }
            if (track.HasPitchBends)
            {
                throw new MusicException($"Track {number} uses pitch bend; this importer does not encode it");
            }
            var selected = new List<SelectedNote>();
            foreach (var note in track.Notes)
            {
                var onset = song.TempoMap.At(note.StartTick);
                var release = song.TempoMap.At(note.EndTick);
                if (note.EndTick <= note.StartTick && start <= onset && onset < end)
                {
                    throw new MusicException($"Track {number} has a zero-length note");
                }
                if (onset >= end || release <= start)
                {
                    continue;
                }
                selected.Add(new SelectedNote(Rational.Max(start, onset), Rational.Min(end, release), note, onset < start || release > end));
            }
            var tokens = new List<string>();
            var events = new List<Report>();
            var cursor = 0;
            var previousEnd = start;
            var transpose = options.Transpose.GetValueOrDefault(channel);
            foreach (var item in selected.OrderBy(item => item.Onset).ThenBy(item => item.Note.Pitch))
            {
                var note = item.Note;
                if (item.Onset < previousEnd)
                {
                    throw new MusicException(string.Create(CultureInfo.InvariantCulture,
                        $"{channel} track {number} has overlapping notes at {item.Onset.ToDouble():F6}s; select a monophonic interval or arrange the chord first"));
                }
                previousEnd = item.Release;
                var begin = Rational.FrameAt(item.Onset, fps) - startFrame;
                var finish = Rational.FrameAt(item.Release, fps) - startFrame;
                if (finish <= begin)
                {
                    throw new MusicException(string.Create(CultureInfo.InvariantCulture,
                        $"{channel} track {number} pitch {note.Pitch} at {item.Onset.ToDouble():F6}s collapses below one frame"));
                }
                if (begin > cursor)
                {
                    tokens.Add($"r%{begin - cursor}");
                }
                int? noise = null;
                int? pitch = null;
                if (channel == "NOI")
                {
                    if (note.Channel != 9)
                    {
                        throw new MusicException("NOI requires percussion on MIDI channel 10");
                    }
                    if (!options.NoiseMap.TryGetValue(note.Pitch, out var mappedNoise))
                    {
                        throw new MusicException($"Missing explicit noise mapping for drum pitch {note.Pitch}");
                    }
                    noise = mappedNoise;
                    tokens.Add($"n{noise}%{finish - begin}");
                }
                else
                {
                    if (note.Channel == 9)
                    {
                        throw new MusicException("Use NOI for MIDI channel 10 percussion");
                    }
                    var transposed = checked(note.Pitch + transpose);
                    if (transposed is < 12 or > 127)
                    {
                        throw new MusicException($"Transposed pitch {transposed} cannot be represented by this MML dialect");
                    }
                    pitch = transposed;
                    tokens.Add($"o{transposed / 12 - 1} {PitchNames[transposed % 12]}%{finish - begin}");
                }
                events.Add(new Report
                {
                    ["source_start_tick"] = note.StartTick.TickValue(), ["source_end_tick"] = note.EndTick.TickValue(),
                    ["source_pitch"] = note.Pitch, ["source_velocity"] = note.Velocity,
                    ["start_frame"] = begin, ["end_frame"] = finish, ["clipped"] = item.Clipped,
                    ["output_pitch"] = pitch, ["noise"] = noise
                });
                cursor = finish;
            }
            if (cursor < totalFrames)
            {
                tokens.Add($"r%{totalFrames - cursor}");
            }
            lines.AddRange(["", $"; MIDI track {number} -> {channel}", channel + ":"]);
            if (ChannelSettings[channel].Length > 0)
            {
                lines.Add(ChannelSettings[channel]);
            }
            if (options.Loop)
            {
                lines.Add("|");
            }
            lines.Add(string.Join(' ', tokens));
            reports[channel] = new Report
            {
                ["track"] = number, ["note_count"] = selected.Count, ["transpose_semitones"] = transpose,
                ["events"] = events, ["clipped_note_count"] = events.Count(item => (bool)item["clipped"]!)
            };
        }
        var report = new Report
        {
            ["source_midi"] = song.Path, ["start_seconds"] = start.ToDouble(), ["end_seconds"] = end.ToDouble(),
            ["duration_frames"] = totalFrames, ["fps_fraction"] = fps.ToString(), ["loop"] = options.Loop,
            ["channels"] = reports, ["excluded_tracks"] = song.Tracks.Where(track => !options.Mapping.ContainsValue(track.Number)).Select(track => track.Number).ToArray(),
            ["voice_policy"] = "strict: reject overlaps; no dropped notes within selected tracks and interval",
            ["timing_policy"] = "round absolute times to nearest frame, halves upward; rebase selected interval",
            ["timbre_policy"] = "fixed NES timbres; sustain affects note length; MIDI program, velocity, other controllers omitted",
            ["noise_map"] = options.NoiseMap.OrderBy(item => item.Key).ToDictionary(item => item.Key.ToString(CultureInfo.InvariantCulture), item => item.Value)
        };
        return new MidiImportResult(MmlFormatter.Format(string.Join('\n', lines) + "\n"), report);
    }
}
