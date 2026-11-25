// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text;
using RockTune.Domain;
using RockTune.Export;

namespace RockTune.Mml;

public static class NativeAudioDump
{
    private sealed record Command(int Address, byte[] Bytes)
    {
        public byte Code => Bytes[0];
        public int Target => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(2));
    }

    public static string Dump(Rockman2Rom rom, MusicSlot slot)
    {
        var bytes = rom.Data.AsSpan(slot.FileStart, slot.Capacity);
        var text = new StringBuilder();
        text.AppendLine($"#rockman2 0x{slot.Id:X2}");
        if (slot.SharedTrackIds.Count > 1)
        {
            text.AppendLine("; Shared IDs: " + string.Join(", ", slot.SharedTrackIds.Select(id => $"0x{id:X2}")));
        }
        if (slot.Id >= Rockman2Layout.FirstSoundEffectId)
        {
            DumpEffect(bytes, slot, text);
        }
        else
        {
            DumpMusic(bytes, slot, text);
        }
        return text.ToString();
    }

    private static void DumpMusic(ReadOnlySpan<byte> data, MusicSlot slot, StringBuilder text)
    {
        if (data.Length < 11 || data[0] != 0x0F)
        {
            throw new MusicException($"Invalid music header for ID 0x{slot.Id:X2}");
        }
        var modulation = BinaryPrimitives.ReadUInt16LittleEndian(data[9..]) - slot.Address;
        var tracks = new List<(string Channel, int Start)>();
        for (var channel = 0; channel < MusicDefaults.Channels.Count; channel++)
        {
            var pointer = BinaryPrimitives.ReadUInt16LittleEndian(data[(1 + 2 * channel)..]);
            if (pointer != 0)
            {
                tracks.Add((MusicDefaults.Channels[channel], pointer - slot.Address));
            }
        }
        if (tracks.Count == 0 || tracks[0].Start != 11 || modulation > data.Length)
        {
            throw new MusicException($"Invalid channel layout for ID 0x{slot.Id:X2}");
        }
        var sections = new List<(string Channel, List<Command> Commands)>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var (channel, start) = tracks[i];
            var end = i + 1 < tracks.Count ? tracks[i + 1].Start : modulation;
            sections.Add((channel, Decode(data, slot.Address, start, end, false)));
        }
        var labels = LoopLabels(sections.SelectMany(section => section.Commands));
        foreach (var (channel, commands) in sections)
        {
            WriteSection(text, channel, MusicTokens(commands, channel, labels));
        }

        var waves = new List<string>();
        for (var i = modulation; i + 4 <= data.Length; i += 4)
        {
            waves.Add($"@wave{data[i]},{data[i + 1]},{data[i + 2]},{data[i + 3]}");
        }
        var remainder = (data.Length - modulation) % 4;
        if (remainder != 0 && data[^remainder..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new MusicException($"Incomplete modulation table for ID 0x{slot.Id:X2}");
        }
        WriteSection(text, "MOD", waves);
    }

    private static void DumpEffect(ReadOnlySpan<byte> data, MusicSlot slot, StringBuilder text)
    {
        if (data.Length < 3 || (data[0] & 0x0F) != 0 || data[0] == 0 || data[1] is 0 or > 0x0F)
        {
            throw new MusicException($"Invalid SFX header for ID 0x{slot.Id:X2}");
        }
        var channels = new List<string>();
        for (var i = 0; i < MusicDefaults.Channels.Count; i++)
        {
            if ((data[1] & (1 << i)) != 0)
            {
                channels.Add(MusicDefaults.Channels[i]);
            }
        }
        text.AppendLine($"#priority {data[0] >> 4}");
        text.AppendLine("#channels " + string.Join(' ', channels));
        var commands = Decode(data, slot.Address, 2, data.Length, true);
        if (commands[^1].Code != 6)
        {
            throw new MusicException($"SFX ID 0x{slot.Id:X2} is missing its end marker");
        }
        WriteSection(text, "FX", EffectTokens(commands, LoopLabels(commands)));
    }

    private static List<Command> Decode(ReadOnlySpan<byte> data, int address, int start, int end, bool effect)
    {
        if (start < 0 || start >= end || end > data.Length)
        {
            throw new MusicException($"Invalid sequence at 0x{address + start:X4}");
        }
        var commands = new List<Command>();
        for (var cursor = start; cursor < end;)
        {
            var code = data[cursor];
            var size = effect
                ? code switch
                {
                    <= 3 or >= 0x80 and <= 0x87 or 0x8F => 2,
                    4 => 4,
                    5 => 5,
                    6 => 1,
                    _ => 0
                }
                : code switch
                {
                    <= 3 or 5 or 8 => 2,
                    4 => 4,
                    7 => 3,
                    6 or 9 or 0x30 or >= 0x20 and <= 0x27 or >= 0x40 => 1,
                    _ => 0
                };
            if (size == 0 || cursor + size > end)
            {
                throw new MusicException($"Unsupported or truncated opcode 0x{code:X2} at 0x{address + cursor:X4}");
            }
            commands.Add(new Command(address + cursor, data.Slice(cursor, size).ToArray()));
            cursor += size;
            if (effect && code == 6 && data[cursor..end].IndexOfAnyExcept((byte)0) < 0)
            {
                break;
            }
        }
        return commands;
    }

    private static Dictionary<int, string> LoopLabels(IEnumerable<Command> source)
    {
        var commands = source.ToArray();
        var addresses = commands.Select(command => command.Address).ToHashSet();
        var labels = new Dictionary<int, string>();
        foreach (var target in commands.Where(command => command.Code == 4).Select(command => command.Target))
        {
            if (!addresses.Contains(target))
            {
                throw new MusicException($"Loop target 0x{target:X4} does not point to a command");
            }
            labels.TryAdd(target, $"loop_{target:x4}");
        }
        return labels;
    }

    private static IEnumerable<string> MusicTokens(List<Command> commands, string channel, Dictionary<int, string> labels)
    {
        var key = 0;
        var octave = -1;
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (labels.TryGetValue(command.Address, out var label))
            {
                yield return ":" + label;
            }
            var data = command.Bytes;
            var code = command.Code;
            var dotted = code == 6 && i + 1 < commands.Count && commands[i + 1].Code >= 0x40 &&
                !labels.ContainsKey(commands[i + 1].Address);
            if (dotted)
            {
                code = commands[++i].Code;
            }
            if (code >= 0x40)
            {
                var length = NativeAudioMml.Lengths[(code - 0x40) >> 5];
                var degree = code & 31;
                var suffix = dotted ? "." : "";
                if (degree == 0)
                {
                    yield return $"r{length}{suffix}";
                }
                else if (channel == "NOI")
                {
                    yield return $"n{degree}/{length}{suffix}";
                }
                else
                {
                    var pitch = key + degree + (channel == "TRI" ? 12 : 24);
                    var nextOctave = pitch / 12 - 1;
                    if (nextOctave != octave)
                    {
                        octave = nextOctave;
                        yield return "o" + octave;
                    }
                    yield return NativeAudioMml.Notes[pitch % 12] + length + suffix;
                }
                continue;
            }
            if (code == 5)
            {
                key = data[1];
            }
            yield return code switch
            {
                0 => $"@tick{data[1]}",
                1 => "@pitch" + unchecked((sbyte)data[1]),
                2 => Duty(data[1], channel),
                3 => Volume(data[1], channel),
                4 => $"@loop{data[1]},{labels[command.Target]}",
                5 => $"@key{key}",
                6 => "@dot",
                7 => Envelope(data[1], data[2]),
                8 => $"@mod{data[1]}",
                9 => "!",
                0x30 => "@tuplet",
                _ => "@tie" + ((code & 7) + 1)
            };
        }
    }

    private static IEnumerable<string> EffectTokens(List<Command> commands, Dictionary<int, string> labels)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (labels.TryGetValue(command.Address, out var label))
            {
                yield return ":" + label;
            }
            var data = command.Bytes;
            if (command.Code is >= 0x80 and <= 0x87)
            {
                var period = ((data[0] & 7) << 8) | data[1];
                if (i + 1 < commands.Count && commands[i + 1].Code == 0 && !labels.ContainsKey(commands[i + 1].Address))
                {
                    yield return $"p{period}%{commands[++i].Bytes[1]}";
                }
                else
                {
                    yield return $"p{period}";
                }
                continue;
            }
            yield return command.Code switch
            {
                0 => $"w%{data[1]}",
                1 => "@pitch" + unchecked((sbyte)data[1]),
                2 => Duty(data[1], "FX"),
                3 => Volume(data[1], "FX"),
                4 => $"@loop{data[1]},{labels[command.Target]}",
                5 => $"@mod{data[1]},{data[2]},{data[3]},{data[4]}",
                6 => "!",
                0x8F when data[1] == 0xFF => "~",
                _ => throw new MusicException($"Unsupported period encoding at 0x{command.Address:X4}")
            };
        }
    }

    private static string Duty(int value, string channel)
    {
        var shift = channel == "NOI" ? 7 : 6;
        return channel != "TRI" && (value & ((1 << shift) - 1)) == 0 ? $"@d{value >> shift}" : $"@duty{value}";
    }

    private static string Volume(int value, string channel)
    {
        if (channel == "TRI")
        {
            return $"@gate{value}";
        }
        return (value & 0xF0) == 0x30 ? $"v{value & 15}" : $"@volume{value}";
    }

    private static string Envelope(int rate, int step)
    {
        if ((step & 15) != 0)
        {
            return $"@envelope{rate},{step}";
        }
        var name = (rate & 0x80) != 0 ? "env" : "rise";
        return $"@{name}{rate & 0x7F},{step >> 4}";
    }

    private static void WriteSection(StringBuilder text, string channel, IEnumerable<string> tokens)
    {
        text.AppendLine().AppendLine(channel + ":");
        var line = new List<string>();
        foreach (var token in tokens)
        {
            if (token.StartsWith(':') || line.Count == 8)
            {
                if (line.Count > 0)
                {
                    text.Append("    ").AppendLine(string.Join(' ', line));
                }
                line.Clear();
            }
            if (token.StartsWith(':'))
            {
                text.Append("    ").AppendLine(token);
            }
            else
            {
                line.Add(token);
            }
        }
        if (line.Count > 0)
        {
            text.Append("    ").AppendLine(string.Join(' ', line));
        }
    }
}
