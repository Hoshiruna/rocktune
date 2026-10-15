// SPDX-License-Identifier: MIT
namespace RockTune.Domain;

public sealed class MusicException(string message) : Exception(message);

public static class MusicDefaults
{
    public static readonly IReadOnlyList<string> Channels = Array.AsReadOnly(new[] { "SQ1", "SQ2", "TRI", "NOI" });
    public static readonly Rational Fps = Rational.Parse("60.0988");
}

public abstract record MusicEvent(int Line);
public sealed record Note(Rational Start, Rational End, int? Pitch, int? Noise, int Line,
    bool Continues = false) : MusicEvent(Line)
{
    public bool IsRest => Pitch is null && Noise is null;
}
public sealed record Setting(string Name, int[] Values, int Line) : MusicEvent(Line);
public sealed record LoopPoint(Rational Time, int Line) : MusicEvent(Line);

public sealed class Score
{
    public string Title { get; set; } = "Untitled";
    public Rational Fps { get; set; } = MusicDefaults.Fps;
    public Dictionary<string, List<MusicEvent>> Channels { get; } = [];
}

public sealed record CompiledSong(byte[] Data, int Address, Report Report);
