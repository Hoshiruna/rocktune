# SPDX-License-Identifier: MIT
"""Command-line entry points for compiling MML and exporting a patched copy."""

import argparse
import json
import sys
from pathlib import Path

from .compiler import compile_song
from .mml import parse_mml
from .model import MusicError
from .rom import Rockman2Rom


def save_report(destination, report):
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main(argv=None):
    # Use UTF-8 for MML titles and file paths on Windows.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Rockman 2 (Japan): basic MML compilation and track 00 replacement")
    commands = parser.add_subparsers(dest="command")
    for command, help_text in (("compile", "Generate binary music data"), ("patch", "Create a ROM copy with track 00 replaced")):
        subparser = commands.add_parser(command, help=help_text)
        subparser.add_argument("mml", type=Path, help="UTF-8 MML file")
        subparser.add_argument("--rom", type=Path, required=True, help="Japanese ROM with the original layout")
        subparser.add_argument("--output", type=Path, required=True, help="Output .bin or .nes file")
    arguments = parser.parse_args(argv)
    if arguments.command is None:
        parser.print_help()
        return 2
    try:
        output = arguments.output
        expected_extension = ".bin" if arguments.command == "compile" else ".nes"
        if output.suffix.lower() != expected_extension:
            raise MusicError("Output file extension must be " + expected_extension)
        if output.resolve() in (arguments.rom.resolve(), arguments.mml.resolve()):
            raise MusicError("Output must not overwrite an input file")
        report_path = output.with_suffix(".json")
        if report_path.resolve() in (arguments.rom.resolve(), arguments.mml.resolve()):
            raise MusicError("Report must not overwrite an input file")
        rom = Rockman2Rom(arguments.rom)
        score = parse_mml(arguments.mml.read_text(encoding="utf-8-sig"))
        song = compile_song(score, rom.pitch_periods())
        report = dict(song.report)
        report["source_mml"] = str(arguments.mml.resolve())
        if arguments.command == "compile":
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(song.data)
            print("Music data: {} bytes at $8AD6".format(len(song.data)))
        else:
            report["patch"] = rom.replace_track00(song, output)
            print("Track 00: {} / {} bytes".format(len(song.data), report["patch"]["capacity_bytes"]))
        save_report(report_path, report)
        print("Output: " + str(output.resolve()))
        print("Report: " + str(report_path.resolve()))
        return 0
    except (MusicError, OSError, UnicodeError) as error:
        print("Error: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
