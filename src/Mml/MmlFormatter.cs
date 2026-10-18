// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.RegularExpressions;
using RockTune.Domain;

namespace RockTune.Mml;

/// <summary>Groups MML commands while preserving the parsed musical events.</summary>
public static class MmlFormatter
{
    private static readonly Regex Tokens = new(
        @"(?<comment>;[^\r\n]*)|(?<directive>\#[^\r\n;]*)|" +
        @"(?<section>(?:SQ1|SQ2|TRI|NOI)\s*:)|" +
        @"(?<control>@\s*[a-z]+\s*\d+(?:\s*,\s*\d+)*)|" +
        @"(?<setting>[tolv]\s*\d+(?:\.\d+)?)|" +
        @"(?<note>n\s*\d+(?:\s*/\s*\d+)?(?:%\s*\d+|\.*)|" +
        @"[cdefgabr][+#-]?(?:\s*\d+)?(?:%\s*\d+|\.*))|" +
        @"(?<symbol>[<>|&])|(?<space>\s+)|(?<other>\d+|.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Format(string text)
    {
        var original = MmlParser.Parse(text);
        var writer = new LayoutWriter();
        var source = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (Match token in Tokens.Matches(source))
        {
            if (token.Groups["space"].Success)
            {
                continue;
            }
            if (token.Groups["comment"].Success)
            {
                var lineStart = source.LastIndexOf('\n', token.Index) + 1;
                var inline = source.AsSpan(lineStart, token.Index - lineStart).Trim().Length > 0;
                writer.Comment(token.Value.TrimEnd(), inline);
            }
            else if (token.Groups["directive"].Success)
            {
                writer.Directive(token.Value.Trim());
            }
            else if (token.Groups["section"].Success)
            {
                writer.Section(Regex.Replace(token.Value, @"\s", "").ToUpperInvariant());
            }
            else
            {
                var value = Regex.Replace(token.Value, @"\s", "").ToLowerInvariant();
                if (token.Groups["control"].Success || (token.Groups["setting"].Success && value[0] != 'o'))
                {
                    writer.Control(value);
                }
                else if (token.Groups["setting"].Success)
                {
                    writer.Octave(int.Parse(value[1..], CultureInfo.InvariantCulture));
                }
                else if (value == "|")
                {
                    writer.Loop();
                }
                else
                {
                    writer.Body(value, token.Groups["note"].Success);
                }
            }
        }
        var formatted = writer.Finish();
        RequireSameEvents(original, MmlParser.Parse(formatted));
        return formatted;
    }

    private static void RequireSameEvents(Score original, Score formatted)
    {
        if (original.Title != formatted.Title || original.Fps != formatted.Fps ||
            !original.Channels.Keys.SequenceEqual(formatted.Channels.Keys))
        {
            throw new MusicException("MML formatting changed score metadata");
        }
        foreach (var (name, before) in original.Channels)
        {
            var after = formatted.Channels[name];
            if (before.Count != after.Count || before.Where((item, index) => !SameEvent(item, after[index])).Any())
            {
                throw new MusicException("MML formatting changed musical events on " + name);
            }
        }
    }

    private static bool SameEvent(MusicEvent before, MusicEvent after) => (before, after) switch
    {
        (Note a, Note b) => a.Start == b.Start && a.End == b.End && a.Pitch == b.Pitch &&
            a.Noise == b.Noise && a.Continues == b.Continues,
        (Setting a, Setting b) => a.Name == b.Name && a.Values.SequenceEqual(b.Values),
        (LoopPoint a, LoopPoint b) => a.Time == b.Time,
        _ => false
    };

    private sealed class LayoutWriter
    {
        private const int MaximumNotesPerLine = 8;
        private const int MaximumLineWidth = 96;
        private readonly List<string> lines = [];
        private readonly List<string> buffer = [];
        private bool inChannel;
        private bool controls;
        private bool noiseChannel;
        private bool hasNotes;
        private int noteCount;
        private int lineWidth = 4;
        private int octave = 4;
        private bool octaveWritten;

        public void Directive(string text)
        {
            Flush();
            lines.Add(text);
        }

        public void Section(string text)
        {
            Flush();
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add("");
            }
            lines.Add(text);
            inChannel = true;
            noiseChannel = text == "NOI:";
            hasNotes = false;
            octave = 4;
            octaveWritten = false;
        }

        public void Comment(string text, bool inline)
        {
            if (inline && buffer.Count > 0)
            {
                Flush(text);
            }
            else
            {
                Flush();
                lines.Add((inChannel ? "    " : "") + text);
            }
        }

        public void Control(string text)
        {
            if (noiseChannel && hasNotes && text.StartsWith('v'))
            {
                Body(text, false);
                return;
            }
            if (!controls)
            {
                Flush();
            }
            controls = true;
            Append(text);
        }

        public void Octave(int value)
        {
            if (octaveWritten && value == octave)
            {
                return;
            }
            var text = octaveWritten && Math.Abs(value - octave) == 1
                ? value > octave ? ">" : "<"
                : "o" + value.ToString(CultureInfo.InvariantCulture);
            Body(text, false);
            octave = value;
            octaveWritten = true;
        }

        public void Loop()
        {
            Flush();
            lines.Add("    |");
        }

        public void Body(string text, bool note)
        {
            if (controls || (noteCount >= MaximumNotesPerLine && text != "&"))
            {
                Flush();
            }
            controls = false;
            Append(text, note);
            if (note)
            {
                noteCount++;
                hasNotes = true;
            }
            if (text is ">" or "<")
            {
                octave += text == ">" ? 1 : -1;
                octaveWritten = true;
            }
        }

        private void Append(string text, bool note = false)
        {
            if (noiseChannel && note && buffer.Count > 0 && Regex.IsMatch(buffer[^1], @"^v\d+$"))
            {
                var volume = buffer[^1];
                buffer.RemoveAt(buffer.Count - 1);
                lineWidth -= volume.Length + (buffer.Count > 0 ? 1 : 0);
                text = volume + " " + text;
            }
            if (buffer.Count > 0 && lineWidth + 1 + text.Length > MaximumLineWidth && text != "&")
            {
                Flush();
            }
            lineWidth += (buffer.Count > 0 ? 1 : 0) + text.Length;
            buffer.Add(text);
        }

        private void Flush(string? comment = null)
        {
            if (buffer.Count > 0)
            {
                lines.Add("    " + string.Join(' ', buffer) + (comment is null ? "" : " " + comment));
                buffer.Clear();
            }
            noteCount = 0;
            lineWidth = 4;
        }

        public string Finish()
        {
            Flush();
            return string.Join('\n', lines) + "\n";
        }
    }
}
