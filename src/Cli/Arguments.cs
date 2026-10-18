// SPDX-License-Identifier: MIT
namespace RockTune.Cli;

internal sealed class ArgumentException(string message) : Exception(message);

internal sealed class Arguments
{
    private static readonly HashSet<string> Commands = ["midi-info", "midi-import", "compile", "patch", "nsf", "rom-check"];
    private static readonly HashSet<string> ImportValues = ["output", "arrangement", "sq1", "sq2", "tri", "noi", "noise-map", "transpose", "start", "end", "fps"];
    private readonly Dictionary<string, List<string>> values = [];
    public string Command { get; private set; } = "";
    public string Input { get; private set; } = "";
    public bool Help { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool Loop { get; private set; }
    public string? Value(string name) => values.TryGetValue(name, out var entries) ? entries[0] : null;
    public IEnumerable<string> All(string name) => values.TryGetValue(name, out var entries) ? entries : [];
    public string Required(string name) => Value(name) ?? throw new ArgumentException("Missing required option --" + name);

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        if (args.Length == 0)
        {
            return result;
        }
        if (args[0] == "-version")
        {
            result.ShowVersion = true;
            return result;
        }
        if (args[0] is "-h" or "--help")
        {
            result.Help = true;
            return result;
        }
        result.Command = args[0];
        if (!Commands.Contains(result.Command))
        {
            throw new ArgumentException("Unknown command: " + result.Command);
        }
        HashSet<string> allowed = result.Command switch
        {
            "midi-info" or "rom-check" => ["output"],
            "midi-import" => ImportValues,
            "nsf" => ["rom", "output", "speed"],
            "patch" => ["rom", "output", "track"],
            _ => ["rom", "output"]
        };
        var positionalOnly = false;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!positionalOnly && argument == "--")
            {
                positionalOnly = true;
                continue;
            }
            if (!positionalOnly && (argument is "-h" or "--help"))
            {
                result.Help = true;
                continue;
            }
            if (!positionalOnly && argument.StartsWith("--", StringComparison.Ordinal))
            {
                var parts = argument[2..].Split('=', 2);
                var name = parts[0];
                if (name == "loop" && result.Command == "midi-import")
                {
                    if (parts.Length != 1 || result.Loop)
                    {
                        throw new ArgumentException("--loop is a flag and may be specified once");
                    }
                    result.Loop = true;
                    continue;
                }
                if (!allowed.Contains(name))
                {
                    throw new ArgumentException($"Unknown option for {result.Command}: --{name}");
                }
                string value;
                if (parts.Length == 2)
                {
                    value = parts[1];
                }
                else
                {
                    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Missing value for --" + name);
                    }
                    value = args[++index];
                }
                if (value.Length == 0)
                {
                    throw new ArgumentException("Missing value for --" + name);
                }
                if (!result.values.TryGetValue(name, out var entries))
                {
                    entries = [];
                    result.values[name] = entries;
                }
                if (entries.Count > 0 && name is not ("noise-map" or "transpose"))
                {
                    throw new ArgumentException("Duplicate option --" + name);
                }
                entries.Add(value);
            }
            else
            {
                if (result.Input.Length != 0)
                {
                    throw new ArgumentException("Unexpected argument: " + argument);
                }
                result.Input = argument;
            }
        }
        if (result.Help)
        {
            return result;
        }
        if (result.Input.Length == 0)
        {
            throw new ArgumentException("Missing input file");
        }
        if (result.Command is not ("midi-info" or "rom-check"))
        {
            result.Required("output");
        }
        if (result.Command is "compile" or "patch" or "nsf")
        {
            result.Required("rom");
        }
        if (result.Command == "patch")
        {
            result.TrackId();
        }
        return result;
    }

    public int TrackId()
    {
        var text = Value("track") ?? "0";
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(hex ? text[2..] : text,
            hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var id) || id is < 0 or > 32)
        {
            throw new ArgumentException("--track requires a ROM music ID from 0 to 32 (0x00..0x20); 0x21 and above are sound effects");
        }
        return id;
    }
}
