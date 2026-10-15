// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using RockTune.Domain;

namespace RockTune.IO;

public static class OutputFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ReadText(string path) => File.ReadAllText(path, Utf8);

    public static void ProtectInputs(string destination, params string[] inputs)
    {
        var fullDestination = ResolvePath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var input in inputs)
        {
            if (string.Equals(fullDestination, ResolvePath(input), comparison))
            {
                throw new MusicException("Output must not overwrite an input file: " + fullDestination);
            }
        }
    }

    private static string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (segment.Length == 0)
            {
                continue;
            }
            current = Path.Combine(current, segment);
            FileSystemInfo? entry = Directory.Exists(current) ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (entry is not null)
            {
                current = entry.ResolveLinkTarget(true)?.FullName ?? current;
            }
        }
        return current;
    }

    public static void RequireExtension(string path, string extension)
    {
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new MusicException("Output file extension must be " + extension);
        }
    }

    private static void CreateParent(string path) => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    public static void WriteText(string path, string text)
    {
        CreateParent(path);
        File.WriteAllText(path, text, Utf8);
    }
    public static void WriteBytes(string path, byte[] data)
    {
        CreateParent(path);
        File.WriteAllBytes(path, data);
    }
    public static void SaveReport(string path, Report report) => WriteText(path, JsonSerializer.Serialize(report, JsonOptions) + "\n");
}
