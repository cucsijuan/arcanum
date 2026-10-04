# Building Arcanum

## Requirements
- .NET SDK 8
- Godot 4.6 **.NET** build (the standard build can't compile C#), with the matching export templates installed
  (Editor → Manage Export Templates).

## Running from source
- `dotnet test tests/Arcanum.Engine.Tests` and `dotnet test tests/Arcanum.Data.Tests` run the test suites.
- Open the project folder in Godot .NET and press Play, or run `godot --path .` from a terminal.
- Content modules: in editor builds a module checked out next to the project folder (`../arcanum-classic`) is
  found automatically; otherwise set `ARCANUM_MODULE_PATH` or install it under `user://modules/<module-id>`.

## Desktop exports
Create the presets once in Project → Export (they are stored in `export_presets.cfg`, which is not committed
because it can contain signing credentials):

| Preset | Notes |
|--------|-------|
| Linux (x86_64) | Ships `Arcanum.x86_64` plus the `data_Arcanum_linuxbsd_x86_64` folder; keep them together. |
| Windows Desktop (x86_64) | Ships `Arcanum.exe` plus `data_Arcanum_windows_x86_64`. Exporting from Linux works. |
| macOS (universal) | Exports from Linux unsigned; on a Mac, open it once with right-click → Open. |

Command-line export (after creating the presets): `godot --headless --path . --export-release "Linux" build/linux/Arcanum.x86_64`.

## Debug options
Environment variables useful while developing and testing:

| Variable | Effect |
|----------|--------|
| `ARCANUM_SEED=<n>` | Reproducible first game |
| `ARCANUM_VS_BOT=1` | The demo game's second player is the computer |
| `ARCANUM_SANDBOX=1` | Demo game starts from the prepared sandbox board |
| `ARCANUM_DECKS=<a>,<b>` | Demo game uses these module decks |
| `ARCANUM_COMMANDER=1` | Demo game is a commander game with the module's commander decks |
| `ARCANUM_LIMITED=draft\|sealed` | Opening the limited screen runs a whole event on its own (with `ARCANUM_AUTOPLAY=1`) |
| `ARCANUM_LIMITED_SOURCE=<text>` | That event uses the first set or cube whose id contains the text (`cube`, `set:abc`) |
| `ARCANUM_PLAYERS=<2-4>` | Number of players for `ARCANUM_COMMANDER` (default 4) |
| `ARCANUM_AUTOPLAY=1` | Local seats play themselves (smoke tests) |
| `ARCANUM_AUTOPLAY=showcase:<Decision>` | Autoplay until the first decision of that type, then freeze (screenshots) |
| `ARCANUM_TEST_UNDO=1` | With autoplay, undo now and then to exercise replay |
| `ARCANUM_OPEN_DECK=<n>` | Deck builder opens the n-th deck |
| `ARCANUM_MODULE_PATH=<dir>` | Use a content module from this folder |
