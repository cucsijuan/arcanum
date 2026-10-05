# Building Arcanum

## Requirements
- .NET SDK 8
- Godot 4.6 **.NET** build (the standard build can't compile C#), with the matching export templates installed
  (Editor → Manage Export Templates).

## Running from source
- `dotnet test Arcanum.sln` runs every test suite (engine, data, network).
- Open the project folder in Godot .NET and press Play, or run `godot --path .` from a terminal.
- Content modules: in editor builds a module checked out next to the project folder (`../arcanum-classic`) is
  found automatically; otherwise set `ARCANUM_MODULE_PATH` or install it under `user://modules/<module-id>`
  (Extras → "Install module from zip…" does this from a module's release zip).

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
| `ARCANUM_COMMANDER_DECKS=<prefix>` | With `ARCANUM_COMMANDER`: use the module decks whose names start with the prefix |
| `ARCANUM_LIMITED=draft\|sealed` | Opening the limited screen runs a whole event on its own (with `ARCANUM_AUTOPLAY=1`) |
| `ARCANUM_LIMITED_SOURCE=<text>` | That event uses the first set or cube whose id contains the text (`cube`, `set:abc`) |
| `ARCANUM_PLAYERS=<2-4>` | Number of players for `ARCANUM_COMMANDER` (default 4) |
| `ARCANUM_AUTOPLAY=1` | Local seats play themselves (smoke tests) |
| `ARCANUM_AUTOPLAY=showcase:<Decision>` | Autoplay until the first decision of that type, then freeze (screenshots) |
| `ARCANUM_TEST_UNDO=1` | With autoplay, undo now and then to exercise replay |
| `ARCANUM_OPEN_DECK=<n>` | Deck builder opens the n-th deck |
| `ARCANUM_MODULE_PATH=<dir>` | Use a content module from this folder |

Online play can be tested with two instances on one machine (run `res://scenes/online/Online.tscn`; add
`ARCANUM_AUTOPLAY=1` so the players play themselves):

| Variable | Effect |
|----------|--------|
| `ARCANUM_ONLINE_HOST=<port>[,<players>]` | Hosts at once with the first deck; free seats go to the computer after `ARCANUM_ONLINE_WAIT` seconds (default 20); starts when the lobby is complete |
| `ARCANUM_ONLINE_EVENT=draft\|sealed` | With `ARCANUM_ONLINE_HOST`: hosts a limited event with the first booster source instead |
| `ARCANUM_ONLINE_JOIN=<address>` | Joins the game at that address (`127.0.0.1:47013`) |
| `ARCANUM_ONLINE_CODE=<code>` | Joins the lobby with that invite code (the host prints it) |
| `ARCANUM_ONLINE_BROWSE=1` | Joins the first lobby the lobby browser finds |
| `ARCANUM_ONLINE_DECK=<n>` | Deck to bring (n-th in the list) |
| `ARCANUM_ONLINE_REJOIN=1` | Gets back into the online game this device last joined |

## Online play
A player hosts from the Online screen and gives the others an address. The host listens on TCP port 47013 (by
default): players on the same network join with its local address; players elsewhere need the port forwarded to the
host (it is tried automatically with UPnP), and a firewall on the host must allow it (on Fedora:
`sudo firewall-cmd --add-port=47013/tcp`). Every player needs the same game version and content module.

On the same network the host's lobby also shows up in every player's **Open lobbies** list and can be joined with its
invite code: lobbies are announced on UDP port 47014 and players connect to a TCP port the system picks, so a firewall
on the host must allow those too. See [online-services.md](online-services.md).
