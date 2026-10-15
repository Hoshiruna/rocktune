// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using RockTune.Domain;

namespace RockTune.Mml;

public static class MmlParser
{
    public static Score Parse(string text)
    {
        var score = new Score();
        var sections = new Dictionary<string, List<string>>();
        string? current = null;
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var content = lines[index].Split(';', 2)[0].Trim();
            if (content.Length == 0)
            {
                continue;
            }
            if (content.StartsWith("#title ", StringComparison.Ordinal))
            {
                score.Title = content[7..].Trim();
                continue;
            }
            if (content.StartsWith("#fps ", StringComparison.Ordinal))
            {
                score.Fps = Rational.Parse(content[5..]);
                if (score.Fps < 1 || score.Fps > 240)
                {
                    throw new MusicException("#fps must be between 1 and 240");
                }
                continue;
            }
            var section = Regex.Match(content, @"^(SQ1|SQ2|TRI|NOI)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            if (section.Success)
            {
                current = section.Groups[1].Value.ToUpperInvariant();
                if (sections.ContainsKey(current))
                {
                    throw new MusicException("Duplicate channel section: " + current);
                }
                sections[current] = Enumerable.Repeat("", index).ToList();
                sections[current].Add(section.Groups[2].Value);
            }
            else
            {
                if (current is null)
                {
                    throw new MusicException($"Line {index + 1}: declare SQ1:, SQ2:, TRI:, or NOI: first");
                }
                while (sections[current].Count < index)
                {
                    sections[current].Add("");
                }
                sections[current].Add(content);
            }
        }
        foreach (var name in MusicDefaults.Channels)
        {
            if (sections.TryGetValue(name, out var contents))
            {
                score.Channels[name] = new ChannelParser(name, string.Join('\n', contents), score.Fps).Parse();
            }
        }
        if (score.Channels.Count == 0)
        {
            throw new MusicException("MML contains no channels");
        }
        return score;
    }

    private sealed class ChannelParser(string name, string text, Rational fps)
    {
        private int position;
        private Rational tempo = 120;
        private int octave = 4;
        private int defaultLength = 4;
        private Rational time;
        private readonly List<MusicEvent> events = [];
        private bool pendingTie;
        private bool hasLoop;
        private int Line => 1 + text.AsSpan(0, position).Count('\n');
        private MusicException Error(string message) => new($"{name} line {Line}: {message}");

        private void SkipSpace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }

        private string? NumberText(bool required = true, bool decimalValue = false)
        {
            SkipSpace();
            var match = Regex.Match(text[position..], decimalValue ? @"^\d+(?:\.\d+)?" : @"^\d+");
            if (!match.Success)
            {
                if (required)
                {
                    throw Error("Expected a number here");
                }
                return null;
            }
            position += match.Length;
            return match.Value;
        }

        private int Number() => int.Parse(NumberText()!, System.Globalization.CultureInfo.InvariantCulture);

        private Rational Duration(int denominator)
        {
            if (position < text.Length && text[position] == '%')
            {
                position++;
                var frames = Number();
                if (frames < 1)
                {
                    throw Error("Frame length must be positive");
                }
                return frames / fps;
            }
            if (denominator is < 1 or > 256)
            {
                throw Error("Note length denominator must be between 1 and 256");
            }
            var dots = 0;
            while (position < text.Length && text[position] == '.')
            {
                position++;
                dots++;
            }
            return new Rational(240, denominator) / tempo * (2 - new Rational(1, System.Numerics.BigInteger.Pow(2, dots)));
        }

        private void ReadNote(char character, int line)
        {
            int? pitch = null;
            int? noise = null;
            var denominator = defaultLength;
            if (character == 'n')
            {
                if (name != "NOI")
                {
                    throw Error("n notes are only supported on the NOI channel");
                }
                noise = Number();
                if (noise < 1 || noise > 15)
                {
                    throw Error("Noise number must be between 1 and 15");
                }
                SkipSpace();
                if (position < text.Length && text[position] == '/')
                {
                    position++;
                    denominator = Number();
                }
            }
            else
            {
                if (character != 'r')
                {
                    if (name == "NOI")
                    {
                        throw Error("Use n1 through n15 for NOI notes and r for rests");
                    }
                    var offset = character switch { 'c' => 0, 'd' => 2, 'e' => 4, 'f' => 5, 'g' => 7, 'a' => 9, _ => 11 };
                    pitch = 12 * (octave + 1) + offset;
                    if (position < text.Length && "+#-".Contains(text[position]))
                    {
                        pitch += text[position++] == '-' ? -1 : 1;
                    }
                    if (pitch < 0 || pitch > 127)
                    {
                        throw Error("Pitch is outside the MIDI note number range");
                    }
                }
                var length = NumberText(false);
                if (length is not null)
                {
                    denominator = int.Parse(length, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            var end = time + Duration(denominator);
            var note = new Note(time, end, pitch, noise, line);
            if (pendingTie)
            {
                var previous = events.OfType<Note>().LastOrDefault();
                if (previous is null || previous.IsRest || note.IsRest)
                {
                    throw Error("Both ends of a tie must be notes");
                }
                if (previous.Pitch != pitch || previous.Noise != noise)
                {
                    throw Error("& can only tie notes with the same pitch or noise number");
                }
                if (ReferenceEquals(events[^1], previous))
                {
                    events[^1] = previous with { End = end };
                }
                else
                {
                    events.Add(note with { Continues = true });
                }
                pendingTie = false;
            }
            else
            {
                events.Add(note);
            }
            time = end;
        }

        private void Comma(string syntax)
        {
            SkipSpace();
            if (position >= text.Length || text[position++] != ',')
            {
                throw Error(syntax);
            }
        }

        private void Control(int line)
        {
            SkipSpace();
            var match = Regex.Match(text[position..], @"^[a-zA-Z]+");
            if (!match.Success)
            {
                throw Error("Expected a control name after @, such as @d or @env");
            }
            position += match.Length;
            var control = match.Value.ToLowerInvariant();
            var value = Number();
            switch (control)
            {
                case "d":
                    if (name == "TRI" || value < 0 || value > (name == "NOI" ? 1 : 3))
                    {
                        throw Error("@d must be 0 to 3 for square waves or 0 to 1 for NOI; TRI has no duty control");
                    }
                    events.Add(new Setting("duty", [value], line));
                    break;
                case "gate":
                    if (name != "TRI" || value is < 0 or > 255)
                    {
                        throw Error("@gate applies to TRI, with a register value from 0 to 255");
                    }
                    events.Add(new Setting("gate", [value], line));
                    break;
                case "env":
                case "rise":
                    Comma($"Envelope syntax is @{control} period,step");
                    var step = Number();
                    if (name == "TRI" || value is < 0 or > 127 || step is < 0 or > 15)
                    {
                        throw Error("Envelopes apply to square waves and NOI; period must be 0 to 127 and step 0 to 15");
                    }
                    if ((value == 0) != (step == 0))
                    {
                        throw Error($"Use @{control}0,0 to disable the envelope; otherwise both values must be positive");
                    }
                    events.Add(new Setting("envelope", [value, step, control == "env" ? 1 : 0], line));
                    break;
                case "vib":
                    Comma("Vibrato syntax is @vib period,step,reversals");
                    var vibratoStep = Number();
                    Comma("Vibrato syntax is @vib period,step,reversals");
                    var reversals = Number();
                    if (name is not ("SQ1" or "SQ2") || value is < 0 or > 127 || vibratoStep is < 0 or > 7 || reversals is < 0 or > 31)
                    {
                        throw Error("@vib applies to square waves; limits are period 127, step 7 and reversals 31");
                    }
                    if ((value != 0 || vibratoStep != 0 || reversals != 0) && (value == 0 || vibratoStep == 0 || reversals == 0))
                    {
                        throw Error("Use @vib0,0,0 to disable vibrato; otherwise all three values must be positive");
                    }
                    events.Add(new Setting("vibrato", [value, vibratoStep, reversals], line));
                    break;
                default:
                    throw Error("Unsupported control name @" + control);
            }
        }

        public List<MusicEvent> Parse()
        {
            while (true)
            {
                SkipSpace();
                if (position == text.Length)
                {
                    break;
                }
                var line = Line;
                var character = char.ToLowerInvariant(text[position++]);
                if ("cdefgabrn".Contains(character))
                {
                    ReadNote(character, line);
                }
                else if ("tolv".Contains(character))
                {
                    var value = Rational.Parse(NumberText(decimalValue: character == 't')!);
                    if (character == 't')
                    {
                        if (value <= 0)
                        {
                            throw Error("Tempo must be greater than zero");
                        }
                        tempo = value;
                    }
                    else
                    {
                        var integer = checked((int)value.Numerator);
                        switch (character)
                        {
                            case 'o':
                                if (integer is < 0 or > 9)
                                {
                                    throw Error("Octave must be between 0 and 9");
                                }
                                octave = integer;
                                break;
                            case 'l':
                                if (integer is < 1 or > 256)
                                {
                                    throw Error("Default length denominator must be between 1 and 256");
                                }
                                defaultLength = integer;
                                break;
                            case 'v':
                                if (name == "TRI" || integer is < 0 or > 15 || pendingTie)
                                {
                                    throw Error("v applies to square waves and NOI, with values from 0 to 15; volume cannot change within a tie");
                                }
                                events.Add(new Setting("volume", [integer], line));
                                break;
                        }
                    }
                }
                else
                {
                    switch (character)
                    {
                        case '<':
                        case '>':
                            octave += character == '>' ? 1 : -1;
                            if (octave is < 0 or > 9)
                            {
                                throw Error("Octave must be between 0 and 9");
                            }
                            break;
                        case '@':
                            Control(line);
                            break;
                        case '&':
                            if (pendingTie || events.Count == 0 || events[^1] is not Note { IsRest: false })
                            {
                                throw Error("Expected a note before &");
                            }
                            pendingTie = true;
                            break;
                        case '|':
                            if (pendingTie || hasLoop)
                            {
                                throw Error("Each channel allows one loop point, which cannot occur within a tie");
                            }
                            events.Add(new LoopPoint(time, line));
                            hasLoop = true;
                            break;
                        default:
                            throw Error("Unsupported character '" + character + "'");
                    }
                }
            }
            if (pendingTie)
            {
                throw Error("Missing note after &");
            }
            if (!events.OfType<Note>().Any())
            {
                throw Error("Channel needs at least one note or rest");
            }
            return events;
        }
    }
}
