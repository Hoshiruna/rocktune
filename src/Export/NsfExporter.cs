// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RockTune.Domain;

namespace RockTune.Export;

public static class NsfExporter
{
    public const int SongLimit = 0xFF00;

    // Ricoh 2A03 INIT. Clear only driver RAM
    // ($E0..$F5, $0500..$057F) and override flag $41; preserve the caller's stack.
    private static readonly byte[] InitCode =
    [
        0xD8, 0xA9, 0x00, 0x8D, 0x15, 0x40, 0x8D, 0x10, 0x40, 0x8D, 0x11, 0x40,
        0x85, 0x41, 0xA2, 0x15, 0x95, 0xE0, 0xCA, 0x10, 0xFB,
        0xA2, 0x7F, 0x9D, 0x00, 0x05, 0xCA, 0x10, 0xFA,
        0xA2, 0x0F, 0x9D, 0x00, 0x40, 0xCA, 0x10, 0xFA,
        0xA9, 0x40, 0x8D, 0x17, 0x40, 0xA9, 0x0F, 0x8D, 0x15, 0x40,
        0xA9, 0x00, 0xA0, 0x00, 0x4C, 0x03, 0x80
    ];

    public static (byte[] Data, Report Report) Export(Rockman2Rom rom, CompiledSong song, Rational fps)
    {
        if (song.Address != Rockman2Rom.Track00Address)
        {
            throw new MusicException("NSF export requires a song compiled at $8AD6");
        }
        var capacity = SongLimit - song.Address;
        if (song.Data.Length > capacity)
        {
            throw new MusicException("Song exceeds the NSF linear address space");
        }
        var initAddress = Math.Max(0xC000, (song.Address + song.Data.Length + 15) & ~15);
        var period = Rational.FrameAt(1000000 / fps, 1);
        if (period is < 1 or > 65535)
        {
            throw new MusicException("Playback rate cannot be represented by the NSF v1 speed field");
        }
        var data = new byte[128 + initAddress - 0x8000 + InitCode.Length];
        "NESM\u001a\u0001\u0001\u0001"u8.CopyTo(data);
        Word(data, 8, 0x8000);
        Word(data, 10, initAddress);
        Word(data, 12, 0x8000);
        var speed = (double)song.Report["speed_multiplier"]!;
        var title = (string)song.Report["title"]!;
        if (speed != 1)
        {
            title += " [" + speed.ToString("G", CultureInfo.InvariantCulture) + "x]";
        }
        TextField(data, 14, title);
        TextField(data, 46, "<?>");
        TextField(data, 78, "Driver: Capcom; MIDI: <?>");
        Word(data, 110, period);
        Word(data, 120, 19997);
        rom.Data.AsSpan(Rockman2Rom.MusicBankFile, 0x4000).CopyTo(data.AsSpan(128));
        song.Data.CopyTo(data, 128 + song.Address - 0x8000);
        InitCode.CopyTo(data, 128 + initAddress - 0x8000);
        var channels = (Dictionary<string, Report>)song.Report["channels"]!;
        return (data, new Report
        {
            ["format"] = "NSF v1", ["song_count"] = 1, ["starting_song"] = 1,
            ["load_address"] = 0x8000, ["init_address"] = initAddress, ["play_address"] = 0x8000,
            ["region"] = "NTSC", ["expansion_audio"] = false, ["bankswitching"] = false,
            ["play_period_microseconds"] = period, ["actual_play_rate_hz"] = 1000000.0 / period,
            ["speed_multiplier"] = speed, ["score_update_rate_hz"] = fps.ToDouble(),
            ["speed_method"] = "compiled event durations; standard NSF update period",
            ["playback_duration_seconds"] = channels.Values.Max(channel => (int)channel["duration_frames"]!) * (double)period / 1000000,
            ["song_capacity_bytes"] = capacity, ["file_bytes"] = data.Length,
            ["driver_source_rom"] = rom.Path, ["driver_code_unchanged"] = true, ["original_rom_modified"] = false
        });
    }

    private static void Word(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), checked((ushort)value));

    private static void TextField(byte[] data, int offset, string value)
    {
        // Replace each non-ASCII code point with one '?' in NSF text fields.
        var ascii = value.EnumerateRunes().Take(31).Select(rune => (byte)(rune.IsAscii ? rune.Value : '?')).ToArray();
        ascii.CopyTo(data, offset);
    }
}
