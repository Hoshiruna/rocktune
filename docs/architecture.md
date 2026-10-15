# RockTune architecture

`RockTune.csproj` builds the .NET 10 command line executable, named `rocktune.exe`
on Windows. Application code lives in `src/` and uses only .NET libraries.
See [usage.md](../usage.md) for publishing instructions, commands and examples.

## Modules

| Directory | Responsibility |
| --- | --- |
| `src/Cli` | Arguments, help, command execution and exit codes |
| `src/Domain` | Musical events and exact rational timestamps |
| `src/Mml` | MML sections, controls, lengths and ties |
| `src/Compilation` | Original-driver commands, pitch selection and loop pointers |
| `src/Midi` | SMF reading, tempo integration, analysis, import and arrangement |
| `src/Export` | Japanese ROM identity, documented music slots, bounded patching and NSF packaging |
| `src/IO` | UTF-8 files, JSON reports and input overwrite checks |

## Supported operations

The C# command handlers define six operations: `midi-info`, `midi-import`,
`compile`, `patch`, `nsf` and `rom-check`. Music conversion uses a shared event model.
MIDI import produces editable MML; compilation converts timed MML events to
commands for the original Japanese Rockman 2 driver.

MIDI reading supports synchronous formats 0 and 1 with PPQN timing. The tempo map
integrates changes across tracks. Sustain, velocity-zero note-offs and controllers
120/123 affect note releases. Nonzero pitch bends, unfinished notes, overlapping
notes and subframe events are rejected by strict import. Explicit arrangements
select voices with `strict`, `highest`, `lowest` or `drum-priority` policies and
record omitted voices and note provenance.

NES channels use fixed default instruments. MML controls select the original
driver's volume, duty, envelopes, triangle gate and pulse modulation. MIDI
programs, velocity and effects do not select output timbres. No song-specific
instrument preset is applied.

Timestamps use exact fractions until absolute frame rounding. `--speed` scales
note, rest and loop times before quantization. The compiler restores instrument
settings at loop markers and supports control changes inside sustained notes.

ROM patching selects music IDs `0..32` through `--track`, defaulting to 0.
Each song is compiled at the selected slot's address and must fit before the
next distinct pointer. IDs `0x18..0x20` alias ID `0x0F`; all shared IDs are
reported. The last physical music slot stops at `$BB22`, before sound effect
`0x21`. Table pointers and bytes outside the selected slot remain unchanged.
NSF export produces one NTSC song using the supplied ROM's original driver.
Music can extend through `$FEFF`, with INIT moved after the music when necessary.
The CLI does not expose sound-effect replacement, padding relocation or
external-driver note extraction.

`Rockman2RomValidator` separates original PRG identity from music-layout
compatibility. `rom-check` returns success only when both match. Conversion
commands accept compatible layouts, including music patched by this tool.
Validation checks header format, file size, mapper, original driver code,
all music/SFX pointers, pitch words and channel/MOD header pointers.

The original Japanese PRG SHA-1 is checked without the 16-byte iNES header.
The driver-code SHA-256 covers CPU `$8000..$8984`, independently of editable
music. The PRG identity was cross-checked against
[Dorando's published metadata](https://dorando.emuverse.com/html/rockman-2.html).

The supplied references define the layout constants: `r2musicaddress.txt` for
music/SFX pointers, `r2musicfreq.txt` for pitch words,
`MegaMan2MusicDataDefinition.txt` for song headers and commands, and
`r2musicmemuse.txt` for driver entry points and state. File addresses in those
references omit the iNES header; actual file offsets add 16 bytes. The original
documents remain external reference material and are not runtime dependencies.

The SMF reader follows the [MIDI Association's Standard MIDI Files specification](https://midi.org/standard-midi-files).
It rejects malformed chunks, invalid data bytes and variable-length values longer
than four bytes. Meta and SysEx events cancel running status.

## Project boundaries and validation

The application compiles only `src/**/*.cs`. Local experiments, retired tooling,
generated media and ROMs are excluded from project items. Archived files under
the ignored `local_music_experiments/` directory are outside the application.
Their APIs and checks do not define or validate C# behavior.

No C# build or executable validation has been run. Runtime compatibility and
playback remain unverified. The project checks C# formatting before compilation
when `clang-format` is available on PATH, using the root `.clang-format` file.
