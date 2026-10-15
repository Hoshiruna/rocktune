# RockTune

RockTune is a C# command line program targeting .NET 10. It converts selected
MIDI tracks to MML, encodes music for the original Japanese Rockman 2 driver,
exports a single-song NSF, replaces a selected music ID in a ROM copy, and
checks Japanese ROM identity and music-layout compatibility.

The C# executable provides `midi-info`, `midi-import`, `compile`, `patch`,
`nsf` and `rom-check`. It uses only .NET libraries. Its implementation in `src/` defines the
supported commands, options and file formats.

## Usage

See [USAGE.md](USAGE.md) for EXE commands, parameters, MIDI arrangements and
MML syntax for the original Rockman 2 driver.

## Development

See [architecture.md](docs/architecture.md) for the C# module layout and format
boundaries. `RockTune.csproj` contains the application project. When available
on PATH, `clang-format` checks the C# sources before compilation.

The C# application has not yet been built or run. Command execution, output
compatibility and playback remain unverified.

