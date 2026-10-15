// SPDX-License-Identifier: MIT
using RockTune.Domain;

namespace RockTune.Export;

public sealed record MusicSlot(int Id, int Address, int EndAddress, IReadOnlyList<int> SharedTrackIds)
{
    public int Capacity => EndAddress - Address;
    public int FileStart => Rockman2Layout.MusicBankFile + Address - 0x8000;
    public int FileEnd => FileStart + Capacity;

    public Report ToReport() => new()
    {
        ["track_id"] = Id, ["cpu_address"] = Address, ["end_address_exclusive"] = EndAddress,
        ["file_start"] = FileStart, ["file_end_exclusive"] = FileEnd,
        ["capacity_bytes"] = Capacity, ["shared_track_ids"] = SharedTrackIds
    };
}

public static class Rockman2Layout
{
    public const int HeaderSize = 16;
    public const int PrgSize = 0x40000;
    public const int MusicBankFile = 0x30010;
    public const int TrackTableFile = 0x30A60;
    public const int PitchTableAddress = 0x8985;
    public const int PitchCount = 0x60;
    public const int LastMusicId = 0x20;
    public const int FirstSoundEffectId = 0x21;

    // The supplied address reference omits the 16-byte iNES header.
    // IDs $18..$20 alias the physical song stored at ID $0F.
    public static IReadOnlyList<int> TrackPointers { get; } = Array.AsReadOnly(new[]
    {
        0x8AD6, 0x8E1D, 0x90C8, 0x9487, 0x9698, 0x9A42, 0x9E48, 0xA191,
        0xA4A8, 0xA960, 0xABA0, 0xAC58, 0xAE14, 0xAEF4, 0xB1B4, 0xB357,
        0xB3D5, 0xB4A6, 0xB553, 0xB58E, 0xB6DC, 0xB9EA, 0xBA52, 0xBAED,
        0xB357, 0xB357, 0xB357, 0xB357, 0xB357, 0xB357, 0xB357, 0xB357,
        0xB357,
        0xBB22, 0xBB5B, 0xBB8E, 0xBBB6, 0xBBC8, 0xBBD5, 0xBC03, 0xBC2D,
        0xBC4C, 0xBC62, 0xBC9B, 0xBCB5, 0xBCF1, 0xBD00, 0xBD25, 0xBD3D,
        0xBD5D, 0xBD78, 0xBD8F, 0xBD98, 0xBDB7, 0xBDCC, 0xBDE1, 0xBDF6,
        0xBE15, 0xBE35, 0xBE88, 0xBEA4, 0xBEBC, 0xBEF3, 0xBF2A, 0xBF3B,
        0xBF48, 0xBF8F
    });

    // r2musicfreq.txt: 96 pitch entries, followed by five sentinel words.
    public static IReadOnlyList<int> PitchWords { get; } = Array.AsReadOnly(new[]
    {
        0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
        0x0000, 0x07F2, 0x07D6, 0x0714, 0x06AE, 0x064E, 0x05F3, 0x0594,
        0x054D, 0x0501, 0x04BB, 0x0475, 0x0436, 0x03F9, 0x03BF, 0x038A,
        0x0357, 0x0327, 0x02FA, 0x02CF, 0x02A7, 0x0281, 0x025D, 0x023B,
        0x021A, 0x01FC, 0x01E0, 0x01C5, 0x01AB, 0x0193, 0x017D, 0x0167,
        0x0153, 0x0140, 0x012E, 0x011D, 0x010D, 0x00FE, 0x00F0, 0x00E2,
        0x00D5, 0x00C9, 0x00BE, 0x00B3, 0x00A9, 0x00A0, 0x0097, 0x008E,
        0x0086, 0x007F, 0x0078, 0x0071, 0x006A, 0x0064, 0x005F, 0x0059,
        0x0054, 0x0050, 0x004B, 0x0047, 0x0043, 0x003F, 0x003C, 0x0038,
        0x0035, 0x0032, 0x002F, 0x002C, 0x002A, 0x0028, 0x0025, 0x0023,
        0x0021, 0x001F, 0x001E, 0x001C, 0x001A, 0x0019, 0x0017, 0x0016,
        0x0015, 0x0014, 0x0012, 0x0011, 0x0010, 0x000F, 0x000F, 0x000E,
        0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF
    });

    public static MusicSlot GetSlot(int trackId)
    {
        if (trackId is < 0 or > LastMusicId)
        {
            throw new MusicException("ROM music ID must be 0..32 (0x00..0x20); 0x21 and above are sound effects");
        }
        var address = TrackPointers[trackId];
        // Use the next distinct address, not the next table ID: aliases are not sorted.
        var end = TrackPointers.Where(pointer => pointer > address).Min();
        var aliases = Enumerable.Range(0, LastMusicId + 1).Where(id => TrackPointers[id] == address).ToArray();
        return new MusicSlot(trackId, address, end, Array.AsReadOnly(aliases));
    }

    public static IEnumerable<MusicSlot> MusicSlots() => Enumerable.Range(0, LastMusicId + 1).Select(GetSlot);
}
