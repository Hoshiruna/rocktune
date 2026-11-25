// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using RockTune.Domain;
using RockTune.Export;

namespace RockTune.Mml;

public sealed record NativeAudioProgram(MusicSlot Slot, byte[] Data);

// The #rockman2 dialect keeps the original driver's timing and loop encoding.
// Ordinary RockTune scores continue to use MmlParser and MusicCompiler.
public static class NativeAudioMml
{
    internal static readonly int[] Lengths = [32, 16, 8, 4, 2, 1];
    internal static readonly string[] Notes = ["c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b"];
    private static readonly Regex IdLine = new(@"^#rockman2\s+(\S+)$", RegexOptions.IgnoreCase);
    private static readonly Regex Control = new(@"^@([a-z]+)(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex Note = new(@"^([cdefgabr])([+#-]?)(\d+)(\.)?$", RegexOptions.IgnoreCase);
    private static readonly Regex Noise = new(@"^n(\d+)/(\d+)(\.)?$", RegexOptions.IgnoreCase);
    private static readonly Regex Period = new(@"^p(\d+)(?:%(\d+))?$", RegexOptions.IgnoreCase);
    private static readonly Regex Label = new(@"^[a-z_]\w*$", RegexOptions.IgnoreCase);

    public static bool IsNative(string text) =>
        Lines(text).Any(line => IdLine.IsMatch(line));

    private static IEnumerable<string> Lines(string text) => text.TrimStart('\uFEFF').Split('\n')
        .Select(line => line.Split(';', 2)[0].Trim());

    private static int Number(string text, int min, int max)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number) || number < min || number > max)
        {
            throw new MusicException($"Expected {min}..{max}, got '{text}'");
        }
        return number;
    }

    public static NativeAudioProgram Compile(string text)
    {
        MusicSlot? slot = null;
        int? priority = null;
        int? mask = null;
        var sections = new Dictionary<string, List<(string Token, int Line)>>(StringComparer.OrdinalIgnoreCase);
        List<(string Token, int Line)>? section = null;
        var lineNumber = 0;
        foreach (var line in Lines(text))
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }
            var id = IdLine.Match(line);
            if (id.Success && slot is null && section is null)
            {
                slot = Rockman2Layout.GetAudioSlot(Number(id.Groups[1].Value, 0, Rockman2Layout.LastSoundEffectId));
            }
            else if (line.StartsWith("#priority ", StringComparison.OrdinalIgnoreCase) && priority is null && section is null)
            {
                priority = Number(line[10..].Trim(), 1, 15);
            }
            else if (line.StartsWith("#channels ", StringComparison.OrdinalIgnoreCase) && mask is null && section is null)
            {
                mask = ParseChannels(line[10..]);
            }
            else if (line.EndsWith(':') && !line.StartsWith('#'))
            {
                var name = line[..^1].ToUpperInvariant();
                if (!MusicDefaults.Channels.Contains(name) && name is not ("FX" or "MOD"))
                {
                    throw new MusicException($"Line {lineNumber}: unknown section {name}");
                }
                section = [];
                if (!sections.TryAdd(name, section))
                {
                    throw new MusicException($"Line {lineNumber}: duplicate section {name}");
                }
            }
            else if (section is not null && !line.StartsWith('#'))
            {
                foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    section.Add((token, lineNumber));
                }
            }
            else
            {
                throw new MusicException($"Line {lineNumber}: unexpected input '{line}'");
            }
        }

        if (slot is null)
        {
            throw new MusicException("Declare #rockman2 with the ROM audio ID");
        }
        var encoder = new Encoder(slot);
        if (slot.Id >= Rockman2Layout.FirstSoundEffectId)
        {
            if (priority is null || mask is null || sections.Count != 1 ||
                !sections.TryGetValue("FX", out var effect) || effect.Count == 0 || effect[^1].Token != "!")
            {
                throw new MusicException("An SFX needs #priority, #channels and one FX section ending with !");
            }
            encoder.Bytes(priority.Value << 4, mask.Value);
            encoder.Emit("FX", effect);
        }
        else
        {
            if (priority is not null || mask is not null || sections.ContainsKey("FX") ||
                !MusicDefaults.Channels.Any(sections.ContainsKey))
            {
                throw new MusicException("Music needs at least one of SQ1, SQ2, TRI and NOI");
            }
            encoder.Bytes(0x0F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            for (var i = 0; i < MusicDefaults.Channels.Count; i++)
            {
                var channel = MusicDefaults.Channels[i];
                if (!sections.TryGetValue(channel, out var notes))
                {
                    continue;
                }
                if (notes.Count == 0)
                {
                    throw new MusicException($"Empty {channel} section");
                }
                encoder.SetPointer(1 + i * 2, encoder.Address);
                encoder.Emit(channel, notes);
            }
            encoder.SetPointer(9, encoder.Address);
            if (sections.TryGetValue("MOD", out var waves))
            {
                encoder.Emit("MOD", waves);
            }
        }
        return encoder.Finish();
    }

    private static int ParseChannels(string text)
    {
        var mask = 0;
        foreach (var name in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var index = Array.IndexOf(MusicDefaults.Channels.ToArray(), name.ToUpperInvariant());
            if (index < 0 || (mask & (1 << index)) != 0)
            {
                throw new MusicException("Invalid or duplicate SFX channel " + name);
            }
            mask |= 1 << index;
        }
        if (mask == 0)
        {
            throw new MusicException("#channels needs at least one channel");
        }
        return mask;
    }

    private sealed class Encoder(MusicSlot slot)
    {
        private readonly List<byte> data = [];
        private readonly Dictionary<string, int> labels = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Offset, string Target)> loops = [];
        private readonly HashSet<int> commandStarts = [];
        private int octave;
        private int key;
        public int Address => slot.Address + data.Count;

        public void Bytes(params int[] values)
        {
            foreach (var value in values)
            {
                data.Add(checked((byte)value));
            }
        }

        public void SetPointer(int offset, int address)
        {
            data[offset] = (byte)(address & 0xFF);
            data[offset + 1] = checked((byte)(address >> 8));
        }

        public void Emit(string channel, IEnumerable<(string Token, int Line)> tokens)
        {
            octave = 4;
            key = 0;
            var first = Address;
            foreach (var (token, line) in tokens)
            {
                try
                {
                    var address = Address;
                    EncodeToken(channel, token.ToLowerInvariant());
                    if (channel != "MOD" && address != Address)
                    {
                        commandStarts.Add(address);
                    }
                    if (data.Count > slot.Capacity)
                    {
                        throw new MusicException($"Audio ID 0x{slot.Id:X2} exceeds its {slot.Capacity}-byte ROM slot");
                    }
                }
                catch (MusicException error)
                {
                    throw new MusicException($"{channel} line {line}: {error.Message}");
                }
            }
            if (channel != "MOD" && Address == first)
            {
                throw new MusicException($"Empty {channel} sequence");
            }
        }

        private void EncodeToken(string channel, string token)
        {
            var effect = channel == "FX";
            var control = Control.Match(token);
            if (control.Success)
            {
                EncodeControl(channel, control.Groups[1].Value, control.Groups[2].Value);
                return;
            }
            if (channel == "MOD")
            {
                throw new MusicException("Use @wave in MOD");
            }
            if (token.StartsWith(':'))
            {
                if (!Label.IsMatch(token[1..]) || !labels.TryAdd(token[1..], Address))
                {
                    throw new MusicException("Invalid or duplicate loop label " + token);
                }
                return;
            }
            if (token == "!")
            {
                Bytes(effect ? 6 : 9);
                return;
            }
            if (token.StartsWith('v'))
            {
                Bytes(3, 0x30 | Number(token[1..], 0, 15));
                return;
            }
            if (effect)
            {
                EncodeEffect(token);
                return;
            }
            if (token.StartsWith('o'))
            {
                octave = Number(token[1..], 0, 9);
                return;
            }
            var noise = Noise.Match(token);
            if (channel == "NOI" && noise.Success)
            {
                WriteNote(Number(noise.Groups[1].Value, 1, 31), noise.Groups[2].Value, noise.Groups[3].Success);
                return;
            }
            var note = Note.Match(token);
            if (!note.Success || (channel == "NOI" && note.Groups[1].Value != "r") ||
                (note.Groups[1].Value == "r" && note.Groups[2].Length != 0))
            {
                throw new MusicException("Invalid note " + token);
            }
            var degree = 0;
            if (note.Groups[1].Value != "r")
            {
                var semitone = Array.IndexOf(Notes, note.Groups[1].Value);
                semitone += note.Groups[2].Value switch { "+" or "#" => 1, "-" => -1, _ => 0 };
                var pitch = 12 * (octave + 1) + semitone;
                degree = pitch - (channel == "TRI" ? 12 : 24) - key;
                if (degree is < 1 or > 31)
                {
                    throw new MusicException("Note is outside the current @key range");
                }
            }
            WriteNote(degree, note.Groups[3].Value, note.Groups[4].Success);
        }

        private void WriteNote(int degree, string length, bool dotted)
        {
            var index = Array.IndexOf(Lengths, Number(length, 1, 32));
            if (index < 0)
            {
                throw new MusicException("Use MML note lengths 1, 2, 4, 8, 16 or 32");
            }
            if (dotted)
            {
                Bytes(6);
            }
            Bytes(0x40 + index * 0x20 + degree);
        }

        private void EncodeEffect(string token)
        {
            if (token == "~")
            {
                Bytes(0x8F, 0xFF);
                return;
            }
            if (token.StartsWith("w%", StringComparison.Ordinal))
            {
                Bytes(0, Number(token[2..], 0, 255));
                return;
            }
            var match = Period.Match(token);
            if (!match.Success)
            {
                throw new MusicException("Use p<period>%<frames>, ~ or w%<frames> in FX");
            }
            var period = Number(match.Groups[1].Value, 0, 0x7FF);
            Bytes(0x80 | (period >> 8), period & 0xFF);
            if (match.Groups[2].Success)
            {
                Bytes(0, Number(match.Groups[2].Value, 0, 255));
            }
        }

        private void EncodeControl(string channel, string name, string text)
        {
            string[] parts = text.Length == 0 ? [] : text.Split(',');
            var effect = channel == "FX";
            int Value(int index = 0, int min = 0, int max = 255) => Number(parts[index], min, max);
            void Require(int count)
            {
                if (parts.Length != count)
                {
                    throw new MusicException($"@{name} expects {count} arguments");
                }
            }
            if (channel == "MOD" && name != "wave")
            {
                throw new MusicException("Use @wave in MOD");
            }

            switch (name)
            {
                case "tick" when !effect:
                    Require(1);
                    Bytes(0, Value());
                    break;
                case "pitch":
                    Require(1);
                    Bytes(1, Value(min: -128, max: 127) & 0xFF);
                    break;
                case "d" when channel != "TRI":
                    Require(1);
                    Bytes(2, Value(max: channel == "NOI" ? 1 : 3) << (channel == "NOI" ? 7 : 6));
                    break;
                case "duty":
                    Require(1);
                    Bytes(2, Value());
                    break;
                case "volume":
                case "gate" when channel == "TRI":
                    Require(1);
                    Bytes(3, Value());
                    break;
                case "loop":
                    Require(2);
                    Bytes(4, Value(), 0, 0);
                    loops.Add((data.Count - 2, parts[1]));
                    break;
                case "key" when !effect:
                    Require(1);
                    key = Value();
                    Bytes(5, key);
                    break;
                case "dot" when !effect:
                    Require(0);
                    Bytes(6);
                    break;
                case "env" when !effect:
                case "rise" when !effect:
                    Require(2);
                    Bytes(7, (name == "env" ? 0x80 : 0) | Value(max: 127), Value(1, max: 15) << 4);
                    break;
                case "envelope" when !effect:
                    Require(2);
                    Bytes(7, Value(), Value(1));
                    break;
                case "mod" when effect:
                    Require(4);
                    Bytes(5, Value(), Value(1), Value(2), Value(3));
                    break;
                case "mod":
                    Require(1);
                    Bytes(8, Value(max: 31));
                    break;
                case "tie" when !effect:
                    Require(1);
                    Bytes(0x1F + Value(min: 1, max: 8));
                    break;
                case "tuplet" when !effect:
                    Require(0);
                    Bytes(0x30);
                    break;
                case "wave" when channel == "MOD":
                    Require(4);
                    Bytes(Value(), Value(1), Value(2), Value(3));
                    break;
                default:
                    throw new MusicException($"Unsupported control @{name} in {channel}");
            }
        }

        public NativeAudioProgram Finish()
        {
            foreach (var (offset, target) in loops)
            {
                if (!labels.TryGetValue(target, out var address) || !commandStarts.Contains(address))
                {
                    throw new MusicException("Unknown or empty loop target " + target);
                }
                SetPointer(offset, address);
            }
            return new NativeAudioProgram(slot, data.ToArray());
        }
    }
}
