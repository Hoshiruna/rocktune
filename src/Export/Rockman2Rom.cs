// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using RockTune.Domain;
using RockTune.IO;

namespace RockTune.Export;

public sealed class Rockman2Rom
{
    public const int MusicBankFile = Rockman2Layout.MusicBankFile;
    public const int Track00Address = 0x8AD6;
    public string Path { get; }
    public byte[] Data { get; }
    public RomValidationResult Validation { get; }

    public Rockman2Rom(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Data = File.ReadAllBytes(path);
        Validation = Rockman2RomValidator.Check(Data);
        if (!Validation.IsCompatible)
        {
            throw new MusicException("Unsupported Japanese ROM layout: " + Validation.Checks.First(check => !check.Passed).Detail);
        }
    }

    private int Word(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(offset, 2));

    public int[] PitchPeriods() => Enumerable.Range(0, Rockman2Layout.PitchCount)
        .Select(index => Word(MusicBankFile + Rockman2Layout.PitchTableAddress - 0x8000 + 2 * index)).ToArray();

    public Report ReplaceTrack(CompiledSong song, string destination, int trackId = 0)
    {
        OutputFiles.ProtectInputs(destination, Path);
        var slot = Rockman2Layout.GetSlot(trackId);
        if (song.Address != slot.Address)
        {
            throw new MusicException($"Track {trackId} (0x{trackId:X2}) must be compiled for CPU address ${slot.Address:X4}");
        }
        if (song.Data.Length > slot.Capacity)
        {
            throw new MusicException($"Track {trackId} (0x{trackId:X2}) capacity is {slot.Capacity} bytes; the new song is {song.Data.Length} bytes and exceeds it by {song.Data.Length - slot.Capacity} bytes");
        }
        var result = (byte[])Data.Clone();
        result.AsSpan(slot.FileStart, slot.Capacity).Clear();
        song.Data.CopyTo(result, slot.FileStart);
        OutputFiles.WriteBytes(destination, result);
        return new Report
        {
            ["source_rom"] = Path, ["output_rom"] = System.IO.Path.GetFullPath(destination),
            ["track_id"] = trackId, ["cpu_address"] = slot.Address,
            ["file_start"] = slot.FileStart, ["file_end_exclusive"] = slot.FileEnd,
            ["shared_track_ids"] = slot.SharedTrackIds,
            ["capacity_bytes"] = slot.Capacity, ["song_bytes"] = song.Data.Length,
            ["unused_bytes"] = slot.Capacity - song.Data.Length, ["other_rom_bytes_unchanged"] = true
        };
    }
}
