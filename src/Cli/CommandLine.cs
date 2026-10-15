// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using System.Text.Json;
using RockTune.Compilation;
using RockTune.Domain;
using RockTune.Export;
using RockTune.IO;
using RockTune.Midi;
using RockTune.Mml;

namespace RockTune.Cli;

public static class CommandLine
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var arguments = Arguments.Parse(args);
            if (arguments.Help || arguments.Command.Length == 0)
            {
                Console.WriteLine(Help(arguments.Command));
                return arguments.Help ? 0 : 2;
            }
            if (arguments.Command == "rom-check")
            {
                return RunRomCheck(arguments);
            }
            if (arguments.Command.StartsWith("midi-", StringComparison.Ordinal))
            {
                RunMidi(arguments);
            }
            else
            {
                RunExport(arguments);
            }
            return 0;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine("Error: " + error.Message);
            Console.Error.WriteLine("Use rocktune --help or rocktune <command> --help.");
            return 2;
        }
        catch (Exception error) when (error is MusicException or IOException or UnauthorizedAccessException or
            System.ArgumentException or JsonException or FormatException or OverflowException)
        {
            Console.Error.WriteLine("Error: " + error.Message);
            return 1;
        }
    }

    private static int RunRomCheck(Arguments args)
    {
        var output = args.Value("output");
        if (output is not null)
        {
            OutputFiles.ProtectInputs(output, args.Input);
            OutputFiles.RequireExtension(output, ".json");
        }
        var validation = Rockman2RomValidator.Check(File.ReadAllBytes(args.Input));
        Console.WriteLine("Original Japanese PRG: " + (validation.IsOriginalJapaneseRom ? "PASS" : "FAIL (modified, unknown or unsupported ROM)"));
        Console.WriteLine("Music layout compatible: " + (validation.IsCompatible ? "PASS" : "FAIL"));
        foreach (var check in validation.Checks)
        {
            Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
        }
        if (validation.IsCompatible)
        {
            Console.WriteLine("ROM music IDs are 0-based; MIDI file track numbers are 1-based.");
            foreach (var slot in Rockman2Layout.MusicSlots())
            {
                var shared = slot.SharedTrackIds.Count > 1 ? "; shared IDs: " + string.Join(", ", slot.SharedTrackIds) : "";
                Console.WriteLine($"Track {slot.Id} (0x{slot.Id:X2}): ${slot.Address:X4}..${slot.EndAddress - 1:X4}, {slot.Capacity} bytes{shared}");
            }
        }
        if (output is not null)
        {
            OutputFiles.SaveReport(output, validation.ToReport(args.Input));
            Console.WriteLine("Report: " + Path.GetFullPath(output));
        }
        return validation.IsOriginalJapaneseRom ? 0 : 1;
    }

    private static void RunMidi(Arguments args)
    {
        var destination = args.Value("output");
        var arrangement = args.Value("arrangement");
        string[] inputs = arrangement is null ? [args.Input] : [args.Input, arrangement];
        if (destination is not null)
        {
            OutputFiles.ProtectInputs(destination, inputs);
            OutputFiles.RequireExtension(destination, args.Command == "midi-info" ? ".json" : ".mml");
        }
        if (args.Command == "midi-info")
        {
            var song = MidiReader.Read(args.Input);
            var report = MidiAnalysis.Analyze(song);
            if (destination is not null)
            {
                OutputFiles.SaveReport(destination, report);
            }
            Console.WriteLine($"MIDI: {song.Tracks.Count} tracks, {report["duration_seconds"]:F3}s, {song.TempoMap.TicksPerBeat} ticks per beat");
            Console.WriteLine("Track numbers include all file tracks and start at 1.");
            foreach (var track in (List<Report>)report["tracks"]!)
            {
                var range = track["pitch_range"] is int[] pitches ? $"[{pitches[0]}, {pitches[1]}]" : "None";
                Console.WriteLine($"Track {track["track"]}: {track["note_count"]} notes, pitch {range}, max voices {track["maximum_polyphony"]}, {((List<Report>)track["overlap_intervals"]!).Count} overlap intervals");
            }
            if (destination is not null)
            {
                Console.WriteLine("Report: " + Path.GetFullPath(destination));
            }
            return;
        }
        destination = args.Required("output");
        var reportPath = Path.ChangeExtension(destination, ".json");
        OutputFiles.ProtectInputs(reportPath, inputs);
        var options = ImportOptions(args);
        if (arrangement is not null && (options.Mapping.Count > 0 || options.NoiseMap.Count > 0 ||
            options.Transpose.Count > 0 || options.Start != 0 || options.End is not null || options.Loop || options.Fps != MusicDefaults.Fps))
        {
            throw new MusicException("Use either an arrangement file or individual track/clip options");
        }
        var source = MidiReader.Read(args.Input);
        MidiImportResult result;
        if (arrangement is not null)
        {
            using var plan = JsonDocument.Parse(OutputFiles.ReadText(arrangement));
            result = MidiArranger.Arrange(source, plan.RootElement);
            result.Report["source_arrangement"] = Path.GetFullPath(arrangement);
        }
        else
        {
            result = MidiImporter.Import(source, options);
        }
        OutputFiles.WriteText(destination, result.Text);
        OutputFiles.SaveReport(reportPath, result.Report);
        Console.WriteLine($"Imported {result.Report["duration_frames"]} frames from {result.Report["start_seconds"]:F3}s to {result.Report["end_seconds"]:F3}s");
        Console.WriteLine("Excluded MIDI tracks: " + string.Join(", ", (int[])result.Report["excluded_tracks"]!));
        PrintPaths(destination, reportPath);
    }

    private static ImportOptions ImportOptions(Arguments args)
    {
        var options = new ImportOptions
        {
            Start = Rational.Parse(args.Value("start") ?? "0"),
            End = args.Value("end") is string end ? Rational.Parse(end) : null,
            Fps = Rational.Parse(args.Value("fps") ?? MusicDefaults.Fps.ToString()),
            Loop = args.Loop
        };
        foreach (var channel in MusicDefaults.Channels)
        {
            if (args.Value(channel.ToLowerInvariant()) is string number)
            {
                options.Mapping[channel] = int.Parse(number, CultureInfo.InvariantCulture);
            }
        }
        foreach (var assignment in args.All("noise-map"))
        {
            var (key, value) = Assignment(assignment);
            var pitch = int.Parse(key, CultureInfo.InvariantCulture);
            if (!options.NoiseMap.TryAdd(pitch, value))
            {
                throw new MusicException("Duplicate mapping: " + key);
            }
        }
        foreach (var assignment in args.All("transpose"))
        {
            var (key, value) = Assignment(assignment);
            if (!options.Transpose.TryAdd(key.ToUpperInvariant(), value))
            {
                throw new MusicException("Duplicate mapping: " + key);
            }
        }
        return options;
    }

    private static (string Key, int Value) Assignment(string text)
    {
        var parts = text.Split('=', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var value))
        {
            throw new MusicException("Expected KEY=NUMBER, got " + text);
        }
        return (parts[0], value);
    }

    private static void RunExport(Arguments args)
    {
        var output = args.Required("output");
        var romPath = args.Required("rom");
        var extension = args.Command switch { "compile" => ".bin", "patch" => ".nes", _ => ".nsf" };
        OutputFiles.RequireExtension(output, extension);
        OutputFiles.ProtectInputs(output, romPath, args.Input);
        var reportPath = Path.ChangeExtension(output, ".json");
        OutputFiles.ProtectInputs(reportPath, romPath, args.Input);
        var rom = new Rockman2Rom(romPath);
        var slot = args.Command == "patch" ? Rockman2Layout.GetSlot(args.TrackId()) : null;
        var score = MmlParser.Parse(OutputFiles.ReadText(args.Input));
        var speed = Rational.Parse(args.Value("speed") ?? "1");
        var song = MusicCompiler.Compile(score, rom.PitchPeriods(), speed,
            address: slot?.Address ?? Rockman2Rom.Track00Address,
            addressLimit: args.Command == "nsf" ? NsfExporter.SongLimit : 0xC000);
        var report = new Report(song.Report) { ["source_mml"] = Path.GetFullPath(args.Input) };
        switch (args.Command)
        {
            case "compile":
                OutputFiles.WriteBytes(output, song.Data);
                Console.WriteLine($"Music data: {song.Data.Length} bytes at $8AD6");
                break;
            case "patch":
                var selectedSlot = slot!;
                var patch = rom.ReplaceTrack(song, output, selectedSlot.Id);
                report["patch"] = patch;
                Console.WriteLine($"Track {selectedSlot.Id} (0x{selectedSlot.Id:X2}): {song.Data.Length} / {patch["capacity_bytes"]} bytes at ${selectedSlot.Address:X4}");
                if (selectedSlot.SharedTrackIds.Count > 1)
                {
                    Console.WriteLine("Shared music data replaced for IDs: " + string.Join(", ", selectedSlot.SharedTrackIds));
                }
                break;
            case "nsf":
                var nsf = NsfExporter.Export(rom, song, score.Fps);
                report["nsf"] = nsf.Report;
                OutputFiles.WriteBytes(output, nsf.Data);
                Console.WriteLine($"NSF: one NTSC song, {song.Data.Length} bytes of music data");
                break;
        }
        OutputFiles.SaveReport(reportPath, report);
        PrintPaths(output, reportPath);
    }

    private static void PrintPaths(string output, string report)
    {
        Console.WriteLine("Output: " + Path.GetFullPath(output));
        Console.WriteLine("Report: " + Path.GetFullPath(report));
    }

    private static string Help(string command) => command switch
    {
        "rom-check" => """
            Usage: rocktune.exe rom-check <Rockman2.nes> [--output report.json]

            Verify original Japanese PRG identity and supported music layout.
            Checks iNES metadata, original driver code, music/SFX pointers,
            pitch-table words and music headers. Lists music IDs and capacities.
            Exit 0: original Japanese PRG and compatible layout. Exit 1: check failed.
            Modified music may retain a compatible layout but fails original-PRG identity.
            The input ROM is read only.
            """,
        "patch" => """
            Usage: rocktune.exe patch <song.mml> --rom Rockman2.nes --output copy.nes [--track ID]

              --track ID   ROM music ID 0..32 (0x00..0x20); default 0

            IDs start at 0, unlike MIDI file track numbers. Decimal and 0x-prefixed
            hexadecimal are accepted. IDs 0x21 and above are sound effects.
            Music is compiled for the selected slot and must fit its capacity.
            IDs 15 and 24..32 share data; replacing any one changes all of them.
            Writes a ROM copy and JSON report without overwriting the source ROM.
            Use rom-check to list slot addresses and capacities.
            """,
        "midi-info" => """
            Usage: rocktune midi-info <file.mid> [--output report.json]

            List file tracks, notes, tempo changes, controllers and polyphony.
            Track numbers start at 1 and include the conductor track.
            """,
        "midi-import" => """
            Usage: rocktune midi-import <file.mid> --output song.mml [options]

              --sq1 N --sq2 N --tri N --noi N   Select 1-based MIDI file tracks
              --noise-map PITCH=NOISE          Drum pitch 0..127 to noise 1..15; repeatable
              --transpose CHANNEL=SEMITONES    Transpose a mapped melodic channel; repeatable
              --start SECONDS --end SECONDS    Select an interval (decimal or fraction)
              --fps RATE                      Update rate; default 60.0988
              --loop                          Loop the selected interval
              --arrangement plan.json         Select tracks and voices by section instead

            Overlapping notes require an explicit arrangement policy.
            MIDI channel 10 percussion requires NOI and explicit noise mappings.
            Writes editable MML and a JSON report with the same stem.
            """,
        "compile" or "nsf" => $"""
            Usage: rocktune {command} <song.mml> --rom Rockman2.nes --output <file{(command == "compile" ? ".bin" : command == "patch" ? ".nes" : ".nsf")}>

            Uses the original Japanese ROM layout and writes a JSON report.
            compile: encode MML for the original driver at $8AD6.
            nsf:     export one NTSC song; --speed FACTOR scales event durations.
            """,
        _ => """
            RockTune - MIDI, MML and Rockman 2 music tools

            Usage: rocktune <command> <input> [options]

              midi-info     List MIDI tracks, tempo and polyphony
              midi-import   Convert selected tracks or an arrangement to MML
              compile       Compile MML to original-driver binary music
              patch         Replace a selected music ID in a Japanese ROM copy
              nsf           Export MML as a single-song NSF
              rom-check     Check Japanese ROM identity, layout and music slots

            Use rocktune <command> --help for options.
            Exit codes: 0 success/help, 1 input or output error, 2 command-line usage.
            """
    };
}
