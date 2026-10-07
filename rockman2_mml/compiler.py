# SPDX-License-Identifier: MIT
"""Encode timed events as original-driver commands with relocated pointers."""

import math
from dataclasses import dataclass
from .model import CHANNELS, LoopPoint, MusicError, Note, Setting


NTSC_CPU_HZ = 1789772.5
DURATION_CODES = ((32, 0xE0), (16, 0xC0), (8, 0xA0), (4, 0x80), (2, 0x60), (1, 0x40))


@dataclass
class CompiledSong:
    data: bytes
    address: int
    report: dict


def frame_at(time, fps):
    """Round absolute timestamps to the nearest frame, with halves upward."""
    value = time * fps
    return (2 * value.numerator + value.denominator) // (2 * value.denominator)


def pitch_index(midi_pitch, channel, periods):
    divider = 32 if channel == "TRI" else 16
    target = 440.0 * 2.0 ** ((midi_pitch - 69) / 12.0)
    candidates = [(index, NTSC_CPU_HZ / (divider * (period + 1)))
                  for index, period in enumerate(periods) if 0 < period <= 0x7FF]
    minimum = min(frequency for _, frequency in candidates)
    maximum = max(frequency for _, frequency in candidates)
    if target < minimum * 0.99 or target > maximum * 1.01:
        raise MusicError("{} MIDI pitch {} is outside the original period table range".format(channel, midi_pitch))
    index, actual = min(candidates, key=lambda item: abs(math.log2(item[1] / target)))
    return index, 1200 * math.log2(actual / target)


def duration_parts(frames, current_tempo):
    """Represent frame lengths with even tempo bytes and up to eight pieces.

    At T=2*k, the six ordinary lengths last k times 1,2,4,8,16,32 frames.
    A tempo command may occur between tied pieces without retriggering pitch.
    """
    pieces = []
    remaining = frames
    while remaining:
        exact = [(2 * (remaining // unit), code, remaining)
                 for unit, code in DURATION_CODES
                 if remaining % unit == 0 and 1 <= remaining // unit <= 127]
        if exact:
            preferred = next((piece for piece in exact if piece[0] == current_tempo), None)
            piece = preferred or exact[0]
        else:
            unit = 32
            multiplier = min(127, remaining // unit)
            if multiplier == 0:
                raise MusicError("Cannot encode note duration: {} frames".format(frames))
            piece = (2 * multiplier, 0xE0, unit * multiplier)
        pieces.append(piece)
        if len(pieces) > 8:
            raise MusicError("This event needs more than eight tied segments; shorten the note or rest")
        current_tempo = piece[0]
        remaining -= piece[2]
    return pieces


def setting_bytes(event, channel):
    if event.name == "volume":
        return bytes((0x03, 0x30 | event.values[0]))
    if event.name == "duty":
        shift = 7 if channel == "NOI" else 6
        return bytes((0x02, event.values[0] << shift))
    period, step = event.values
    return bytes((0x07, (0x80 | period) if period else 0, step << 4))


def encode_channel(name, events, fps, periods):
    data = bytearray((0x00, 0x02, 0x01, 0x00))
    if name == "TRI":
        # Control=1 keeps the gate open; a nonzero reload makes it audible.
        data.extend((0x03, 0xFF))
        state = {}
    else:
        data.extend((0x02, 0 if name == "NOI" else 0x80, 0x03, 0x3C, 0x07, 0, 0))
        state = {"duty": Setting("duty", (0 if name == "NOI" else 2,), 0),
                 "volume": Setting("volume", (12,), 0),
                 "envelope": Setting("envelope", (0, 0), 0)}
    pitch_results = {}
    for event in events:
        if isinstance(event, Note) and event.pitch is not None:
            pitch_results[event.pitch] = pitch_index(event.pitch, name, periods)
    indices = [item[0] for item in pitch_results.values()]
    shared_base = min(indices) - 1 if indices and max(indices) - min(indices) <= 30 else None
    current_base = None
    current_tempo = 2
    loop_offset = loop_frame = None
    loop_fixups = []
    notes_report = []
    last_frame = 0
    for event in events:
        if isinstance(event, Setting):
            data.extend(setting_bytes(event, name))
            state[event.name] = event
            continue
        if isinstance(event, LoopPoint):
            loop_offset = len(data)
            loop_frame = frame_at(event.time, fps)
            # Repeat the settings at the marker so the end of the loop cannot
            # accidentally change the beginning's instrument or base pitch.
            for setting in state.values():
                data.extend(setting_bytes(setting, name))
            current_base = None
            current_tempo = None
            continue
        start_frame = frame_at(event.start, fps)
        end_frame = frame_at(event.end, fps)
        frames = end_frame - start_frame
        if frames < 1:
            raise MusicError("{} line {}: event lasts less than one frame after quantization".format(name, event.line))
        relative_pitch = 0
        table_index = None
        if event.pitch is not None:
            table_index = pitch_results[event.pitch][0]
            if current_base is None or not 1 <= table_index - current_base <= 31:
                current_base = shared_base if shared_base is not None else max(0, table_index - 16)
                data.extend((0x05, current_base))
            relative_pitch = table_index - current_base
        elif event.noise is not None:
            relative_pitch = event.noise
        pieces = duration_parts(frames, current_tempo)
        if len(pieces) > 1 and not event.is_rest:
            data.append(0x20 + len(pieces) - 1)
        for tempo, duration_code, part_frames in pieces:
            if tempo != current_tempo:
                data.extend((0x00, tempo))
                current_tempo = tempo
            data.append(duration_code | relative_pitch)
        notes_report.append({"source_line": event.line, "start_frame": start_frame,
                             "end_frame": end_frame, "duration_frames": frames,
                             "midi_pitch": event.pitch, "noise": event.noise,
                             "table_index": table_index, "pieces": len(pieces)})
        last_frame = end_frame
    if loop_offset is not None:
        if last_frame <= loop_frame:
            raise MusicError(name + " has no positive duration after the loop point")
        data.extend((0x04, 0x00))
        loop_fixups.append((len(data), loop_offset))
        data.extend((0, 0))
    else:
        data.append(0x09)
    return data, loop_fixups, {"bytes": len(data), "duration_frames": last_frame,
                              "loop_start_frame": loop_frame,
                              "loop_duration_frames": last_frame - loop_frame if loop_frame is not None else None,
                              "pitch_error_cents": {str(pitch): round(item[1], 3) for pitch, item in pitch_results.items()},
                              "events": notes_report}


def compile_song(score, periods, address=0x8AD6):
    if not 0x8000 <= address < 0xC000:
        raise MusicError("Song address must be between $8000 and $BFFF")
    song = bytearray((0x0F,)) + bytearray(10)
    channel_reports = {}
    loop_signatures = []
    for index, name in enumerate(CHANNELS):
        if name not in score.channels:
            continue
        data, fixups, report = encode_channel(name, score.channels[name], score.fps, periods)
        channel_address = address + len(song)
        song[1 + 2 * index:3 + 2 * index] = channel_address.to_bytes(2, "little")
        for location, target in fixups:
            data[location:location + 2] = (channel_address + target).to_bytes(2, "little")
        report["cpu_address"] = channel_address
        report["loop_addresses"] = [channel_address + target for _, target in fixups]
        channel_reports[name] = report
        if report["loop_start_frame"] is not None:
            loop_signatures.append((report["loop_start_frame"], report["loop_duration_frames"]))
        song.extend(data)
    if len(set(loop_signatures)) > 1:
        raise MusicError("Looping channels must have matching loop starts and durations to stay synchronized")
    modulation_address = address + len(song)
    song[9:11] = modulation_address.to_bytes(2, "little")
    song.extend((0, 0, 0x80, 0))
    if address + len(song) > 0xC000:
        raise MusicError("Song exceeds the current music bank")
    report = {"title": score.title, "fps": float(score.fps), "fps_fraction": str(score.fps),
              "cpu_address": address, "byte_count": len(song),
              "modulation_address": modulation_address, "channels": channel_reports,
              "encoding": "original Rockman 2 driver; frame-quantized MML"}
    return CompiledSong(bytes(song), address, report)
