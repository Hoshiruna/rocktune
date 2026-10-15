// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RockTune.Export;

public sealed record RomCheck(string Name, bool Passed, string Detail);

public sealed record RomValidationResult(IReadOnlyList<RomCheck> Checks, string? PrgSha1)
{
    public bool IsCompatible => Checks.All(check => check.Passed);
    public bool IsOriginalJapaneseRom => IsCompatible && PrgSha1 == Rockman2RomValidator.JapanesePrgSha1;

    public Report ToReport(string path) => new()
    {
        ["source_rom"] = Path.GetFullPath(path),
        ["original_japanese_rom"] = IsOriginalJapaneseRom,
        ["music_layout_compatible"] = IsCompatible,
        ["prg_sha1"] = PrgSha1,
        ["expected_japanese_prg_sha1"] = Rockman2RomValidator.JapanesePrgSha1,
        ["identity_scope"] = "256 KiB PRG, excluding the 16-byte iNES header",
        ["checks"] = Checks.Select(check => new Report
        {
            ["name"] = check.Name, ["passed"] = check.Passed, ["detail"] = check.Detail
        }).ToArray(),
        ["music_tracks"] = IsCompatible ? Rockman2Layout.MusicSlots().Select(slot => slot.ToReport()).ToArray() : Array.Empty<Report>(),
        ["input_rom_modified"] = false
    };
}

public static class Rockman2RomValidator
{
    // PRG identity is independent of differing iNES header metadata.
    // Reference: https://dorando.emuverse.com/html/rockman-2.html
    public const string JapanesePrgSha1 = "3E9DC769E1B63E73834786EC7FE9EDE7E706C72F";
    private const string DriverSha256 = "86361A8FBA393798EA1A9078BAD5592AEBF9C113BDE7D587BF8ABEB764CDC626";

    public static RomValidationResult Check(ReadOnlySpan<byte> data)
    {
        var checks = new List<RomCheck>();
        var hasHeader = data.Length >= Rockman2Layout.HeaderSize && data[..4].SequenceEqual("NES\u001a"u8);
        checks.Add(new RomCheck("ines_header", hasHeader, hasHeader ? "iNES header found" : "Input is not an iNES ROM"));
        if (!hasHeader)
        {
            return new RomValidationResult(checks, null);
        }
        var mapper = (data[6] >> 4) | (data[7] & 0xF0);
        var layout = data.Length == Rockman2Layout.HeaderSize + Rockman2Layout.PrgSize &&
            data[4] == 16 && data[5] == 0 && mapper == 1 && (data[6] & 4) == 0 && (data[7] & 15) == 0;
        checks.Add(new RomCheck("ines_layout", layout, layout
            ? "256 KiB PRG, CHR RAM, mapper 1, iNES 1.0, no trainer"
            : "Expected 262160 bytes, 256 KiB PRG, CHR RAM, mapper 1, iNES 1.0 and no trainer"));
        if (!layout)
        {
            return new RomValidationResult(checks, null);
        }
        var prgSha1 = Convert.ToHexString(SHA1.HashData(data[Rockman2Layout.HeaderSize..]));
        var driver = data.Slice(Rockman2Layout.MusicBankFile, Rockman2Layout.PitchTableAddress - 0x8000);
        var driverMatches = Convert.ToHexString(SHA256.HashData(driver)) == DriverSha256;
        checks.Add(new RomCheck("original_driver", driverMatches, driverMatches
            ? "Original driver code at $8000..$8984 matches"
            : "Original music driver code at $8000..$8984 has changed"));

        var pointerErrors = new List<string>();
        for (var id = 0; id < Rockman2Layout.TrackPointers.Count; id++)
        {
            var actual = Word(data, Rockman2Layout.TrackTableFile + 2 * id);
            var expected = Rockman2Layout.TrackPointers[id];
            if (actual != expected)
            {
                pointerErrors.Add($"ID ${id:X2}: expected ${expected:X4}, found ${actual:X4}");
            }
        }
        checks.Add(new RomCheck("music_and_sfx_pointers", pointerErrors.Count == 0, pointerErrors.Count == 0
            ? "All 67 music and sound-effect pointers match r2musicaddress.txt"
            : string.Join("; ", pointerErrors)));

        var pitchErrors = new List<string>();
        for (var index = 0; index < Rockman2Layout.PitchWords.Count; index++)
        {
            var offset = Rockman2Layout.MusicBankFile + Rockman2Layout.PitchTableAddress - 0x8000 + 2 * index;
            if (Word(data, offset) != Rockman2Layout.PitchWords[index])
            {
                pitchErrors.Add($"Pitch-table word ${index:X2} differs");
            }
        }
        checks.Add(new RomCheck("pitch_table", pitchErrors.Count == 0, pitchErrors.Count == 0
            ? "All 101 pitch-table and sentinel words match r2musicfreq.txt"
            : string.Join("; ", pitchErrors)));

        var headerErrors = new List<string>();
        foreach (var slot in Rockman2Layout.MusicSlots().DistinctBy(slot => slot.Address))
        {
            if (data[slot.FileStart] != 0x0F)
            {
                headerErrors.Add($"Track ${slot.Id:X2}: missing $0F music header");
                continue;
            }
            for (var channel = 0; channel < 5; channel++)
            {
                var pointer = Word(data, slot.FileStart + 1 + 2 * channel);
                // Some original songs leave unused MOD pointers at the next slot.
                var valid = channel == 4
                    ? pointer >= slot.Address + 11 && pointer <= slot.EndAddress
                    : pointer == 0 || (pointer >= slot.Address + 11 && pointer < slot.EndAddress);
                if (!valid)
                {
                    headerErrors.Add($"Track ${slot.Id:X2}: header pointer {channel + 1} (${pointer:X4}) is outside its slot");
                }
            }
        }
        checks.Add(new RomCheck("music_headers", headerErrors.Count == 0, headerErrors.Count == 0
            ? "Music markers and channel/MOD pointers fit the documented slots"
            : string.Join("; ", headerErrors)));
        return new RomValidationResult(checks, prgSha1);
    }

    private static int Word(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
}
