# RockTune Usage

These commands assume `rocktune.exe`, `Rockman2.nes`, and your input files are in the current directory.

Rockman 2 and Mega Man 2 are supported. For the US version, use `Megaman2.nes`
in place of `Rockman2.nes` in the commands below. No region option is needed.
`rom-check Megaman2.nes` checks the US PRG checksum; `rom-check Rockman2.nes`
checks the JP PRG checksum. Filenames are case-insensitive. Other filenames
allow either supported checksum.

## Basic workflow

```powershell
# 1. Check the ROM and list available music IDs
.\rocktune.exe rom-check Rockman2.nes

# 2. List MIDI tracks
.\rocktune.exe midi-info input.mid

# 3. Select tracks and convert them to MML
.\rocktune.exe midi-import input.mid --sq1 2 --tri 3 --loop --output song.mml

# 4. Export an NSF for listening
.\rocktune.exe nsf song.mml --rom Rockman2.nes --output preview.nsf

# 5. Replace music ID 8 in a new ROM copy
.\rocktune.exe patch song.mml --rom Rockman2.nes --track 8 --output patched.nes
```

`--sq1 2 --tri 3` is an example. Choose tracks using the `midi-info` results.

## Other commands

```powershell
# Show version
.\rocktune.exe -version

# Generate binary music data only
.\rocktune.exe compile song.mml --rom Rockman2.nes --output music.bin

# Export an NSF at 1.25x speed
.\rocktune.exe nsf song.mml --rom Rockman2.nes --speed 1.25 --output preview_fast.nsf

# Show all options for a command
.\rocktune.exe midi-import --help
.\rocktune.exe patch --help
```

## Notes

See [Music IDs and slot capacities](docs/music-slots.md) for the full address and size table.

- MIDI track numbers start at **1**. ROM music IDs start at **0**, range from `0..32`, and also accept `0x00..0x20`. Omitting `--track` selects ID 0.
- **IDs 15 and 24..32 share music data.** Replacing any one changes all of them.
- Use `--noi` for drums and `--noise-map` for explicit drum mappings. Standard import rejects overlapping notes; use `--arrangement` to select voices explicitly.
- MIDI import writes short, indented MML lines, with NOI volume changes beside their notes. `%N` durations are exact frame counts.
- Music that exceeds the selected slot's capacity is rejected before writing. Output cannot overwrite the source ROM.
- A ROM with replaced music no longer matches the original checksum, but conversion can continue if its music layout remains compatible.
- `rom-check` returns code 0 when the PRG matches the checksum selected by its filename and its music layout is compatible. A wrong region, modified or unknown PRG returns code 1. The report includes the expected and detected regions. A compatible music layout still permits `compile`, `patch`, and `nsf`. Modified ROMs report an unknown region.
- Imports and exports write JSON reports with the same filename stem. Use different output names, as shown above, to keep separate reports.

## Build the EXE

Install the .NET 10 SDK, then run from the project root:

```powershell
dotnet publish RockTune.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts\win-x64
```

The EXE is at `artifacts\win-x64\rocktune.exe`. Use that path in the commands above, or place the EXE in your working directory.
