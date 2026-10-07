# SPDX-License-Identifier: MIT
"""Check the sample against original driver instructions, without a build."""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from reference_cpu import Cpu6502
from rockman2_mml.compiler import compile_song
from rockman2_mml.mml import parse_mml
from rockman2_mml.model import MusicError
from rockman2_mml.rom import Rockman2Rom


CHANNEL_BASES = {0x500: "SQ1", 0x51F: "SQ2", 0x53E: "TRI", 0x55D: "NOI"}
COMMAND_LENGTHS = {0: 2, 1: 2, 2: 2, 3: 2, 4: 4, 5: 2, 6: 1, 7: 3, 8: 2, 9: 1, 0x30: 1}


def memory_from_rom(data):
    memory = bytearray(65536)
    memory[0x8000:0xC000] = data[0x30010:0x34010]
    return memory


def note_addresses(data, start, end):
    addresses = set()
    instruction_starts = set()
    loop_targets = []
    address = start
    while address < end:
        instruction_starts.add(address)
        offset = 0x30010 + address - 0x8000
        opcode = data[offset]
        if opcode >= 0x40:
            addresses.add(address)
            length = 1
        elif 0x20 <= opcode <= 0x27:
            length = 1
        elif opcode in COMMAND_LENGTHS:
            length = COMMAND_LENGTHS[opcode]
            if opcode == 4:
                loop_targets.append(data[offset + 2] | data[offset + 3] << 8)
        else:
            raise ValueError("Unknown command at ${:04X}".format(address))
        address += length
    if address != end or any(target not in instruction_starts for target in loop_targets):
        raise ValueError("An instruction or loop target crosses a channel boundary")
    return addresses


def sample_playback(patched, report, insert_sfx):
    note_sets = {}
    for name, details in report["channels"].items():
        start = details["cpu_address"]
        boundaries = [value["cpu_address"] for value in report["channels"].values()
                      if value["cpu_address"] > start] + [report["modulation_address"]]
        note_sets[name] = note_addresses(patched, start, min(boundaries))
    cpu = Cpu6502(memory_from_rom(patched))
    fetched = {name: [] for name in report["channels"]}
    frame = 0

    def observe(machine):
        if machine.pc != 0x8935:
            return
        base = machine.read_word(0xEC)
        name = CHANNEL_BASES.get(base)
        if name in note_sets and machine.read_word(base) in note_sets[name]:
            fetched[name].append(frame)

    cpu.before_step = observe
    cpu.call(0x8003, a=0)
    loop_frames = next(iter(report["channels"].values()))["loop_duration_frames"]
    if loop_frames is None:
        raise ValueError("Validation sample must have a loop")
    frame_limit = 1 + loop_frames * 3
    triangle_reload = None
    for frame in range(1, frame_limit + 1):
        if insert_sfx and frame == 200:
            cpu.call(0x8003, a=0x21)
        cpu.call(0x8000)
        # The driver applies volume registers on the update after note setup.
        if frame == 2:
            triangle_reload = cpu.memory[0x4008] & 0x7F
    for name, details in report["channels"].items():
        expected = []
        for turn in range(4):
            expected.extend(1 + turn * details["loop_duration_frames"] + event["start_frame"]
                            for event in details["events"])
        expected = [value for value in expected if value <= frame_limit]
        if fetched[name] != expected:
            raise ValueError("Driver event times differ from the score: " + name)
    if triangle_reload == 0:
        raise ValueError("Triangle linear-counter reload is zero")
    if insert_sfx and cpu.memory[0xE1] != 0:
        raise ValueError("Sound effect did not terminate")
    return {"frames": frame_limit, "event_counts": {name: len(values) for name, values in fetched.items()},
            "triangle_reload": triangle_reload, "sfx21_inserted": insert_sfx,
            "event_times_match": True}


def check_long_tie(rom):
    score = parse_mml("#fps 60\nSQ1: t120 o4 c1&c4. d8\n")
    song = compile_song(score, rom.pitch_periods())
    memory = memory_from_rom(rom.data)
    memory[song.address:song.address + len(song.data)] = song.data
    cpu = Cpu6502(memory)
    frame = 0
    lookups = []

    def observe(machine):
        if machine.pc == 0x8766 and machine.read_word(0xEC) == 0x500:
            lookups.append(frame)

    cpu.before_step = observe
    cpu.call(0x8003, a=0)
    for frame in range(1, 182):
        cpu.call(0x8000)
    if lookups != [1, 166]:
        raise ValueError("A split tied note retriggered or ended at the wrong time")
    return {"sustained_frames": 165, "pitch_lookup_frames": lookups,
            "parts": song.report["channels"]["SQ1"]["events"][0]["pieces"]}


def check_input_errors():
    cases = ("SQ1: c0", "SQ1: c & d", "NOI: c4", "TRI: v12 c4")
    for text in cases:
        try:
            parse_mml(text)
        except MusicError:
            continue
        raise ValueError("Invalid MML was accepted: " + text)
    return len(cases)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rom", type=Path, required=True)
    parser.add_argument("--patched", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rom = Rockman2Rom(args.rom)
    patched = args.patched.read_bytes()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    if len(patched) != len(rom.data) or patched[:0x30AE6] != rom.data[:0x30AE6] or patched[0x30E2D:] != rom.data[0x30E2D:]:
        raise ValueError("The ROM changed outside track 00")
    results = {"method": "isolated original-driver instruction execution; no PPU/APU or whole-game emulation",
               "outside_track00_unchanged": True,
               "sample_without_sfx": sample_playback(patched, report, False),
               "sample_with_sfx": sample_playback(patched, report, True),
               "automatic_tie": check_long_tie(rom),
               "invalid_mml_cases_rejected": check_input_errors(),
               "build_verification_run": False, "whole_game_playback_verified": False}
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
