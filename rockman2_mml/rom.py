# SPDX-License-Identifier: MIT
"""Read the supported Japanese ROM layout and replace track 00 in a copy."""

from pathlib import Path

from .model import MusicError


MUSIC_BANK_FILE = 0x30010
TRACK_TABLE_FILE = 0x30A60
TRACK00_ADDRESS = 0x8AD6
TRACK00_NEXT_ADDRESS = 0x8E1D
TRACK00_FILE = 0x30AE6
TRACK00_END_FILE = 0x30E2D
TRACK00_CAPACITY = TRACK00_END_FILE - TRACK00_FILE


class Rockman2Rom:
    def __init__(self, path):
        self.path = Path(path)
        self.data = self.path.read_bytes()
        header = self.data[:16]
        if len(header) != 16 or header[:4] != b"NES\x1a":
            raise MusicError("Input is not an iNES ROM")
        mapper = (header[6] >> 4) | (header[7] & 0xF0)
        if len(self.data) != 0x40010 or header[4:6] != bytes((16, 0)) or mapper != 1 or header[6] & 4 or header[7] & 0x0C:
            raise MusicError("This version only supports the original Japanese layout with 256 KiB PRG, CHR RAM, and no trainer")
        if self.data[MUSIC_BANK_FILE:MUSIC_BANK_FILE + 3] != bytes((0x4C, 0x35, 0x82)):
            raise MusicError("Music driver entry point does not match the supported layout")
        if self.word(TRACK_TABLE_FILE) != TRACK00_ADDRESS or self.word(TRACK_TABLE_FILE + 2) != TRACK00_NEXT_ADDRESS:
            raise MusicError("Track 00/01 addresses do not match the supported original layout")
        if self.data[TRACK00_FILE] != 0x0F or self.data[TRACK00_END_FILE] != 0x0F:
            raise MusicError("Track boundaries do not match the supported layout")

    def word(self, offset):
        return self.data[offset] | self.data[offset + 1] << 8

    def pitch_periods(self):
        start = MUSIC_BANK_FILE + 0x8985 - 0x8000
        return [self.word(start + 2 * index) for index in range(0x60)]

    def replace_track00(self, song, destination):
        destination = Path(destination)
        if destination.resolve() == self.path.resolve():
            raise MusicError("Output must be a ROM copy and must not overwrite the input file")
        if song.address != TRACK00_ADDRESS:
            raise MusicError("Track 00 must be compiled for CPU address $8AD6")
        if len(song.data) > TRACK00_CAPACITY:
            raise MusicError("Track 00 capacity is {} bytes; the new song is {} bytes and exceeds it by {} bytes".format(
                TRACK00_CAPACITY, len(song.data), len(song.data) - TRACK00_CAPACITY))
        result = bytearray(self.data)
        result[TRACK00_FILE:TRACK00_END_FILE] = song.data + bytes(TRACK00_CAPACITY - len(song.data))
        if result[:TRACK00_FILE] != self.data[:TRACK00_FILE] or result[TRACK00_END_FILE:] != self.data[TRACK00_END_FILE:]:
            raise MusicError("Patching changed data outside track 00")
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(result)
        return {"source_rom": str(self.path.resolve()), "output_rom": str(destination.resolve()),
                "track_id": 0, "file_start": TRACK00_FILE, "file_end_exclusive": TRACK00_END_FILE,
                "capacity_bytes": TRACK00_CAPACITY, "song_bytes": len(song.data),
                "unused_bytes": TRACK00_CAPACITY - len(song.data),
                "other_rom_bytes_unchanged": True}
