# Alexander.Gui (Uno Platform)

Cross-platform front end for the Alexander Shell: Windows, macOS and Linux
from one `net10.0-desktop` build (Uno Platform, Skia rendering).

The GUI never touches Core. Each command window launches the Shell with
`--host` and talks to it over stdin/stdout (Alexander.Hosting); the Shell
builds, loads and rebuilds Core itself.

## Requirements

- .NET 10 SDK
- The main Alexander solution next to this one (see `Directory.Build.props`),
  with Alexander.Graphics, Alexander.Hosting and the Shell's host mode.
- Linux: an X11 session (or the framebuffer), plus the usual Skia/fontconfig
  libraries.

## Run

    dotnet run --project Alexander.Gui -f net10.0-desktop

## Publish

    dotnet publish Alexander.Gui -c Release -f net10.0-desktop -r win-x64   --self-contained
    dotnet publish Alexander.Gui -c Release -f net10.0-desktop -r osx-arm64 --self-contained
    dotnet publish Alexander.Gui -c Release -f net10.0-desktop -r linux-x64 --self-contained

## Files

Settings and state (`settings.json`, `state.json`, `gui.log`) live in
`Alexander/Gui` under the per-user application-data folder:
`%AppData%` on Windows, `~/.config` on Linux,
`~/Library/Application Support` on macOS.
