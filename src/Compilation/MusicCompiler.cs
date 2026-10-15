// SPDX-License-Identifier: MIT
using RockTune.Domain;

namespace RockTune.Compilation;

public static class MusicCompiler
{
    private static readonly (int Unit, int Code)[] DurationCodes = [(32, 0xE0), (16, 0xC0), (8, 0xA0), (4, 0x80), (2, 0x60), (1, 0x40)];
    private sealed record DurationPiece(int Tempo, int Code, int Frames);
    private readonly record struct Modulation(int Period, int Step, int Reversals)
    {
        public static Modulation From(Setting setting) => new(setting.Values[0], setting.Values[1], setting.Values[2]);
        public int[] Values => [Period, Step, Reversals];
    }
    private sealed record EncodedChannel(List<byte> Data, List<(int Location, int Target)> Fixups, Report Report);

    public static CompiledSong Compile(Score score, IReadOnlyList<int> periods, Rational? speed = null,
        int address = 0x8AD6, int addressLimit = 0xC000)
    {
        var multiplier = speed ?? 1;
        if (multiplier <= 0)
        {
            throw new MusicException("Speed multiplier must be positive");
        }
        if (address is < 0x8000 or >= 0xC000 || addressLimit is < 0xC000 or > 0x10000)
        {
            throw new MusicException("Use a song address in $8000..$BFFF and a music limit in $C000..$10000");
        }
        var song = new List<byte>(new byte[11]);
        song[0] = 0x0F;
        var modulations = new Dictionary<Modulation, int> { [new(0, 0, 0)] = 0 };
        foreach (var setting in score.Channels.Values.SelectMany(events => events).OfType<Setting>())
        {
            if (setting.Name == "vibrato")
            {
                var definition = Modulation.From(setting);
                modulations.TryAdd(definition, modulations.Count);
            }
        }
        if (modulations.Count > 32)
        {
            throw new MusicException("The driver supports at most 32 distinct modulation definitions");
        }
        var reports = new Dictionary<string, Report>();
        var loops = new HashSet<(int Start, int Duration)>();
        for (var index = 0; index < MusicDefaults.Channels.Count; index++)
        {
            var name = MusicDefaults.Channels[index];
            if (!score.Channels.TryGetValue(name, out var sourceEvents))
            {
                continue;
            }
            var events = sourceEvents.Select(item => item switch
            {
                Note note => (MusicEvent)(note with { Start = note.Start / multiplier, End = note.End / multiplier }),
                LoopPoint loop => loop with { Time = loop.Time / multiplier },
                _ => item
            }).ToList();
            var encoded = EncodeChannel(name, events, score.Fps, periods, modulations);
            var channelAddress = address + song.Count;
            WriteWord(song, 1 + 2 * index, channelAddress);
            foreach (var (location, target) in encoded.Fixups)
            {
                WriteWord(encoded.Data, location, channelAddress + target);
            }
            encoded.Report["cpu_address"] = channelAddress;
            encoded.Report["loop_addresses"] = encoded.Fixups.Select(fixup => channelAddress + fixup.Target).ToArray();
            reports[name] = encoded.Report;
            if (encoded.Report["loop_start_frame"] is int start)
            {
                loops.Add((start, (int)encoded.Report["loop_duration_frames"]!));
            }
            song.AddRange(encoded.Data);
        }
        if (loops.Count > 1)
        {
            throw new MusicException("Looping channels must have matching loop starts and durations to stay synchronized");
        }
        var modulationAddress = address + song.Count;
        WriteWord(song, 9, modulationAddress);
        foreach (var definition in modulations.Keys)
        {
            song.AddRange([(byte)definition.Period, (byte)((definition.Step << 5) | definition.Reversals), 0x80, 0]);
        }
        if (address + song.Count > addressLimit)
        {
            throw new MusicException("Song exceeds the selected music address range");
        }
        return new CompiledSong(song.ToArray(), address, new Report
        {
            ["title"] = score.Title, ["fps"] = score.Fps.ToDouble(), ["fps_fraction"] = score.Fps.ToString(),
            ["speed_multiplier"] = multiplier.ToDouble(),
            ["speed_method"] = "scale note, rest and loop times before frame quantization",
            ["cpu_address"] = address, ["byte_count"] = song.Count,
            ["modulation_address"] = modulationAddress, ["channels"] = reports,
            ["modulation_definitions"] = modulations.Keys.Select(definition => definition.Values).ToArray(),
            ["encoding"] = "original Rockman 2 driver; frame-quantized MML"
        });
    }

    private static void WriteWord(List<byte> data, int offset, int value)
    {
        if (value is < 0 or > 65535)
        {
            throw new MusicException("Song exceeds the selected music address range");
        }
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static (int Index, double Cents) PitchIndex(int pitch, string channel, IReadOnlyList<int> periods)
    {
        var divider = channel == "TRI" ? 32 : 16;
        var target = 440.0 * Math.Pow(2, (pitch - 69) / 12.0);
        var candidates = periods.Select((period, index) => (Period: period, Index: index))
            .Where(item => item.Period is > 0 and <= 0x7FF)
            .Select(item => (item.Index, Frequency: 1789772.5 / (divider * (item.Period + 1)))).ToArray();
        if (candidates.Length == 0 || target < candidates.Min(item => item.Frequency) * 0.99 ||
            target > candidates.Max(item => item.Frequency) * 1.01)
        {
            throw new MusicException($"{channel} MIDI pitch {pitch} is outside the original period table range");
        }
        var closest = candidates.MinBy(item => Math.Abs(Math.Log2(item.Frequency / target)));
        return (closest.Index, 1200 * Math.Log2(closest.Frequency / target));
    }

    private static List<DurationPiece> DurationParts(int frames, int? currentTempo)
    {
        if (frames < 1)
        {
            throw new MusicException("Event lasts less than one frame after quantization");
        }
        var pieces = new List<DurationPiece>();
        var remaining = frames;
        while (remaining > 0)
        {
            var exact = DurationCodes.Where(item => remaining % item.Unit == 0 && remaining / item.Unit is >= 1 and <= 127)
                .Select(item => new DurationPiece(2 * (remaining / item.Unit), item.Code, remaining)).ToArray();
            DurationPiece piece;
            if (exact.Length > 0)
            {
                piece = exact.FirstOrDefault(item => item.Tempo == currentTempo) ?? exact[0];
            }
            else
            {
                var multiple = Math.Min(127, remaining / 32);
                if (multiple == 0)
                {
                    throw new MusicException($"Cannot encode note duration: {frames} frames");
                }
                piece = new DurationPiece(2 * multiple, 0xE0, 32 * multiple);
            }
            pieces.Add(piece);
            if (pieces.Count > 8)
            {
                throw new MusicException("This event needs more than eight tied segments; shorten the note or rest");
            }
            currentTempo = piece.Tempo;
            remaining -= piece.Frames;
        }
        return pieces;
    }

    private static byte[] SettingBytes(Setting setting, string channel, Dictionary<Modulation, int> modulations)
    {
        var values = setting.Values;
        return setting.Name switch
        {
            "volume" => [0x03, (byte)(0x30 | values[0])],
            "duty" => [0x02, (byte)(values[0] << (channel == "NOI" ? 7 : 6))],
            "gate" => [0x03, (byte)values[0]],
            "vibrato" => [0x08, (byte)modulations[Modulation.From(setting)]],
            "envelope" => [0x07, (byte)(values[0] == 0 ? 0 : ((values.Length == 3 ? values[2] : 1) << 7) | values[0]), (byte)(values[1] << 4)],
            _ => throw new MusicException("Unsupported setting: " + setting.Name)
        };
    }

    private static int TiedSegmentCount(List<MusicEvent> events, int start, Rational fps)
    {
        var count = 0;
        Note? previous = null;
        for (var index = start; index < events.Count; index++)
        {
            if (events[index] is Setting)
            {
                continue;
            }
            if (events[index] is not Note note || (previous is not null && !note.Continues))
            {
                break;
            }
            if (previous is not null && (previous.IsRest || note.IsRest || previous.End != note.Start ||
                previous.Pitch != note.Pitch || previous.Noise != note.Noise))
            {
                throw new MusicException("A sustained continuation must have the same pitch and no time gap");
            }
            count += DurationParts(Rational.FrameAt(note.End, fps) - Rational.FrameAt(note.Start, fps), null).Count;
            previous = note;
        }
        if (count > 8)
        {
            throw new MusicException("This sustained note needs more than eight driver segments");
        }
        return count;
    }

    private static EncodedChannel EncodeChannel(string name, List<MusicEvent> events, Rational fps,
        IReadOnlyList<int> periods, Dictionary<Modulation, int> modulations)
    {
        var data = new List<byte> { 0x00, 0x02, 0x01, 0x00 };
        var state = new Dictionary<string, Setting>();
        if (name == "TRI")
        {
            // The control flag and nonzero reload keep the triangle gate audible.
            data.AddRange([0x03, 0xFF]);
            state["gate"] = new Setting("gate", [0xFF], 0);
        }
        else
        {
            data.AddRange([0x02, (byte)(name == "NOI" ? 0 : 0x80), 0x03, 0x3C, 0x07, 0, 0]);
            state["duty"] = new Setting("duty", [name == "NOI" ? 0 : 2], 0);
            state["volume"] = new Setting("volume", [12], 0);
            state["envelope"] = new Setting("envelope", [0, 0], 0);
        }
        if (name is "SQ1" or "SQ2")
        {
            data.AddRange([0x08, 0x00]);
            state["vibrato"] = new Setting("vibrato", [0, 0, 0], 0);
        }
        var pitches = new Dictionary<int, (int Index, double Cents)>();
        foreach (var note in events.OfType<Note>())
        {
            if (note.Pitch is int pitch)
            {
                pitches[pitch] = PitchIndex(pitch, name, periods);
            }
        }
        var indices = pitches.Values.Select(item => item.Index).ToArray();
        int? sharedBase = indices.Length > 0 && indices.Max() - indices.Min() <= 30 ? indices.Min() - 1 : null;
        int? currentBase = null;
        int? currentTempo = 2;
        int? loopOffset = null;
        int? loopFrame = null;
        var fixups = new List<(int Location, int Target)>();
        var noteReports = new List<Report>();
        var lastFrame = 0;
        for (var eventIndex = 0; eventIndex < events.Count; eventIndex++)
        {
            var item = events[eventIndex];
            if (item is Setting setting)
            {
                data.AddRange(SettingBytes(setting, name, modulations));
                state[setting.Name] = setting;
                continue;
            }
            if (item is LoopPoint loop)
            {
                loopOffset = data.Count;
                loopFrame = Rational.FrameAt(loop.Time, fps);
                // Restore the marker's instrument when the stream loops back.
                foreach (var saved in state.Values)
                {
                    data.AddRange(SettingBytes(saved, name, modulations));
                }
                currentBase = null;
                currentTempo = null;
                continue;
            }
            var note = (Note)item;
            var begin = Rational.FrameAt(note.Start, fps);
            var end = Rational.FrameAt(note.End, fps);
            var frames = end - begin;
            if (frames < 1)
            {
                throw new MusicException($"{name} line {note.Line}: event lasts less than one frame after quantization");
            }
            var relativePitch = 0;
            int? tableIndex = null;
            if (note.Pitch is int midiPitch)
            {
                tableIndex = pitches[midiPitch].Index;
                if (currentBase is null || tableIndex - currentBase < 1 || tableIndex - currentBase > 31)
                {
                    currentBase = sharedBase ?? Math.Max(0, tableIndex.Value - 16);
                    data.AddRange([0x05, unchecked((byte)currentBase.Value)]);
                }
                relativePitch = tableIndex.Value - currentBase.Value;
            }
            else if (note.Noise is int noise)
            {
                relativePitch = noise;
            }
            var pieces = DurationParts(frames, currentTempo);
            if (!note.Continues && !note.IsRest)
            {
                var tiedCount = TiedSegmentCount(events, eventIndex, fps);
                if (tiedCount > 1)
                {
                    data.Add((byte)(0x20 + tiedCount - 1));
                }
            }
            foreach (var piece in pieces)
            {
                if (piece.Tempo != currentTempo)
                {
                    data.AddRange([0x00, (byte)piece.Tempo]);
                    currentTempo = piece.Tempo;
                }
                data.Add((byte)(piece.Code | relativePitch));
            }
            noteReports.Add(new Report
            {
                ["source_line"] = note.Line, ["start_frame"] = begin, ["end_frame"] = end,
                ["duration_frames"] = frames, ["midi_pitch"] = note.Pitch, ["noise"] = note.Noise,
                ["table_index"] = tableIndex, ["pieces"] = pieces.Count, ["continues"] = note.Continues,
                ["segment_frames"] = pieces.Select(piece => piece.Frames).ToArray()
            });
            lastFrame = end;
        }
        if (loopOffset is int target)
        {
            if (lastFrame <= loopFrame)
            {
                throw new MusicException(name + " has no positive duration after the loop point");
            }
            data.AddRange([0x04, 0]);
            fixups.Add((data.Count, target));
            data.AddRange([0, 0]);
        }
        else
        {
            data.Add(0x09);
        }
        return new EncodedChannel(data, fixups, new Report
        {
            ["bytes"] = data.Count, ["duration_frames"] = lastFrame, ["loop_start_frame"] = loopFrame,
            ["loop_duration_frames"] = loopFrame is null ? null : lastFrame - loopFrame,
            ["pitch_error_cents"] = pitches.ToDictionary(item => item.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item => Math.Round(item.Value.Cents, 3)), ["events"] = noteReports
        });
    }
}
