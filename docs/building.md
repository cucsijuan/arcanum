# Building Arcanum

## Requirements
- .NET SDK 8
- Godot 4.7 **.NET** build (the standard build can't compile C#), with the matching export templates installed
  (Editor → Manage Export Templates).

## Running from source
- `dotnet test Arcanum.sln` runs every test suite (engine, data, network).
- Open the project folder in Godot .NET and press Play, or run `godot --path .` from a terminal.
- Content modules: the game knows no module by name. `ContentLoader.FindModuleDirectory` takes `ARCANUM_MODULE_PATH`
  first, then in editor builds the first folder next to the project folder that holds `manifest.json` and `sources.json`
  (a module checkout), then the installed module under `user://modules`. With several
  installed, the one installed last is used: the installer sets the time of the module's `manifest.json` to the install
  time. Install with Extras → "Install module from zip…" (from a module's release zip) or by copying the module into
  `user://modules/<module-id>`. A module folder copied in is read as it is: its scripts come from `scripts/`, and its card
  data is downloaded and made at the first start unless the folder holds `card-data.bin`.

## Interface art
The gold frames, buttons and dividers come from a private art pack that is not in this repository (`ui-art/` is
ignored). The game uses only its baked kit (`kit/`: each piece cropped and scaled to the size it is drawn at, with
`kit.json` giving the 9-slice margins that keep corners whole); `tools/bake_kit.py` in the art repository rebuilds it
from the source art (`python tools/bake_kit.py --preview out.png` draws every piece stretched, to check the slicing).
To get it locally, run `tools/ui-art.sh` (it copies the kit from `../arcanum-ui-art`, or from the folder in
`ARCANUM_UI_ART`) and re-import the project. Without it the game runs and draws plain panels and buttons. Release
builds clone the pack with the `EOS_SDK_TOKEN` secret, keep only the kit, and warn when that fails.

The kit also holds the picture playmats, as `kit/mats/<id>.jpg` (locally `ui-art/kit/mats/<id>.jpg`). Without them the
playmats show the grid. To add one: use a JPG about 3.5:1 (e.g. 3200×920 px) with the key art in the upper middle (about
2100 px wide) and about 40 px free at the edges, no baked corners or transparency. A picture playmat fills its area without
stretching. When the area is wider than the picture, the picture is cut a little above the middle (`Playmat.PictureFocus`
= 0.3: 30% of the cut comes off the top, 70% off the bottom), so keep heads and key figures in the upper middle and expect
the bottom to sit under the hand. Each playmat can have its own cut in `kit/mats/mats.json` (next to the pictures): `{"focus": {"<id>": 0.55}}`, from 0 (keeps the top) to 1 (keeps the bottom); 0.5 cuts both equally. A playmat not listed uses 0.3 (`Playmat.FocusOf` in `scripts/UI/Board/Playmat.cs`). Release exports include `ui-art/kit/mats/*.json`. The far seat's playmat (the top half, and the career table's top mat) lies turned round
(rotated 180°), as across a table. Then put the file in the art pack,
add the id and label to `BoardStyle.Playmats` and the id to `BoardStyle.PicturePlaymats` (`scripts/UI/Board/BoardStyle.cs`),
and give it a price in the module's `career/career.json` (`playmats`).

## Desktop exports
Create the presets once in Project → Export (they are stored in `export_presets.cfg`, which is not committed
because it can contain signing credentials):

| Preset | Notes |
|--------|-------|
| Linux (x86_64) | Ships `Arcanum.x86_64` plus the `data_Arcanum_linuxbsd_x86_64` folder; keep them together. |
| Windows Desktop (x86_64) | Ships `Arcanum.exe` plus `data_Arcanum_windows_x86_64`. Exporting from Linux works. |
| macOS (universal) | Exports from Linux unsigned; on a Mac, open it once with right-click → Open. |

Command-line export (after creating the presets): `godot --headless --path . --export-release "Linux" build/linux/Arcanum.x86_64`.

Releases are built by GitHub Actions (`.github/workflows/release.yml`): pushing a tag `vX.Y.Z` (matching
`config/version` in `project.godot`) runs the tests, exports Linux and Windows with the presets in
`.github/export/export_presets.cfg` and publishes a release with both zips and the notes in `docs/releases/vX.Y.Z.md`.
Running the workflow by hand only exports, keeping the zips as workflow artifacts.

`tools/release.sh` does the whole release. It asks for the version, sets it in `project.godot`, creates the release notes from the last notes' Install section and opens them in `$EDITOR` when they're missing, builds and runs the tests, optionally tests the export on GitHub, commits `Version X.Y.Z` with any changed tracked files (after asking), pushes, and after confirmation pushes the tag and waits for the release.

When the workflow has published the release and `../godot-switch` is there, `tools/release.sh` adds the console version. It runs `build_arcanum.sh --no-module` in that checkout with `ARCANUM_SRC` set to this one, and uploads `Arcanum-vX.Y.Z-console.zip` (the NRO at `switch/arcanum-nx/arcanum.nro`, laid out as on the SD card). The build runs on this machine because it needs the console toolchain. A failed console build only prints a warning. The home screen entry (NSP) is never published, since it is made with a console's keys. The game's update check on the console takes this zip (see "Updates from the game" in that checkout's README).

The release workflow also deploys the career service after a tag's release is published (`.github/workflows/deploy-service.yml`), so it checks careers of the new version. A manual `server/Arcanum.CareerService/deploy/deploy.sh` is only needed when the service's systemd unit changed. The deploy also keeps the version it replaces and the time of the change, so careers played offline with the previous version can still be checked after the update (see [career-mode.md](career-mode.md), *The career service that seals careers*). For a new content module version, run `deploy/publish-content.sh <module dir>` (see [career-mode.md](career-mode.md), *Career service hosting*).

The project wiki (https://github.com/cucsijuan/arcanum/wiki) mirrors docs/: the player guide (`docs/guide/`), the developer docs (building, engine-map, card-scripts, career-mode, online-services, roadmap; review-backlog stays out) and the release notes. `tools/wiki_sync.py` builds the pages: links between docs become wiki links, other relative links point at the file on GitHub, a Home page and a sidebar are generated, and each page names its source file. Pages that don't come from docs/ are removed. Without options it writes the pages to a temporary clone and shows what changed; `--push` publishes them. `tools/release.sh` runs it with `--push` at the end of a release. Edit docs/, never the wiki.

## Debug options
Environment variables useful while developing and testing:

| Variable | Effect |
|----------|--------|
| `ARCANUM_SEED=<n>` | Reproducible first game |
| `ARCANUM_VS_BOT=1` | The demo game's second player is the computer |
| `ARCANUM_BOT_SEARCH=off\|on\|<milliseconds>` | Forces the computer's thinking ahead (see the **Computer thinks ahead** setting): `off`, `on`, or on with that many milliseconds as the most one decision may take. Unset, the setting decides |
| `ARCANUM_BOT_SEARCH_SHAPE=<rollouts>x<turns>` | With thinking ahead on: how many games each option is played on, and for how many turns (for example `2x2`). Unset, 4x4 with six or more processors, else 2x2 |
| `ARCANUM_SANDBOX=1` | Demo game (and a career match) starts from the prepared sandbox board |
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
| `ARCANUM_SUGGEST=1` | With `ARCANUM_OPEN_DECK`: the deck builder opens on the suggestions (commander decks, and decks in a format the module's deck search covers); with `ARCANUM_OPEN=career ARCANUM_CAREER_PAGE=decks`, the career deck desk does, for the first career deck |
| `ARCANUM_DESK_FORMATS=<id>,<id>,...` | With `ARCANUM_OPEN=career ARCANUM_CAREER_PAGE=decks`: switches the first career deck's format to each id in turn (half a second apart) |
| `ARCANUM_OPEN=career` | The main menu opens the career at once; `ARCANUM_CAREER_PAGE=<page>` then opens a page (`decks`, `event`, `enter` enters today's first event, paying with boosters for a draft that takes them, else cash; the full list is in `CareerScreen.cs`) |
| `ARCANUM_CAREER_PAGE=online:<event id>` | With `ARCANUM_OPEN=career`: registers online for today's run of that event and opens it. The deck is a commander deck for commander events, the first other deck for constructed events, none for drafts (paid with boosters for a draft that takes them, else cash) |
| `ARCANUM_CAREER_AUTO=online` | On an online event's screen, checks in as soon as check-in opens (once per run) |
| `ARCANUM_CAREER_TABLE=<n>` | Every career match and event is played at the store's n-th table (from 1) |
| `ARCANUM_CAREER_MATS=<mine>,<theirs>` | Career matches show those playmat ids on the two seats (`none` for no playmat) |
| `ARCANUM_MODULE_PATH=<dir>` | Use a content module from this folder (it must hold `manifest.json` and `sources.json`); the module zips next to the game are not offered |
| `ARCANUM_UPDATE_FROM=<version>` | The update check runs as if the game were that version (shows the update dialog; outside an exported build it offers the download page) |
| `ARCANUM_OPEN=online` | The main menu opens the online screen at once (exported builds can't be given a scene) |
| `ARCANUM_OPEN=game` | The main menu opens a game with the module's sample decks at once (devices without a command line, e.g. automatic test runs; `ARCANUM_VS_BOT`, `ARCANUM_AUTOPLAY` and the other quick-start switches apply) |
| `ARCANUM_OPEN=booster` | The main menu opens a booster of the module's first set (the first with a pack image) over the title screen once the cards are loaded. Normally the pack cuts itself open after a short delay; this one waits for a swipe however long it takes, so its look can be checked with screenshots |
| `ARCANUM_SHOTS=<folder>[,seconds]` | Saves a numbered picture of the screen (`0001.png`, `0002.png`…) every 20 s, or every `seconds`, while the game plays on. Unlike `ARCANUM_SHOT` it doesn't quit (long test runs) |
| `ARCANUM_PAD_SOAK=<seconds>[,ms]` | Plays with nothing but controller buttons pressed at random (the arrows, confirm and back, weighted roughly as a player uses them), one press every 300 ms or every `ms`, for that many seconds, then quits. It starts a game with the module's sample decks (use with `ARCANUM_VS_BOT=1`), and starts another whenever the table is left. It prints lines starting with `PAD SOAK:`: a stall when the main thread stops for over two seconds, each time the table was left, and a summary at the end (presses per button, games started, stalls, last turn). `ARCANUM_SEED` makes the presses repeatable; with `ARCANUM_SHOTS` it leaves pictures to look over. To keep the player's own settings and career untouched, run it with a separate HOME / XDG_DATA_HOME (copy card_data and settings.json there) |
| `ARCANUM_PAD_KEYS="w5 r r d a b"` | Presses the controller's buttons in that order once the cards are loaded: `l` `r` `u` `d` the d-pad, `a` confirm, `b` back, `x` the left face button, `w<seconds>` waits. It prints `PAD KEYS: <step> -> <what has the focus>` after each press, then quits. With `ARCANUM_SHOTS=<folder>[,seconds]` it also saves a picture after each press (`pad_01_r.png`…) |
| `ARCANUM_PAD_PLAY=1` (with `ARCANUM_VS_BOT=1`) | The player's side plays the way the autopilot would (lands, then spells, attacking with everything, the first legal targets), but only by pressing controller buttons: the arrows move the focus, confirm presses it, and a choice the buttons can't make is printed as `PAD PLAY: stuck` (with what was wanted) and answered directly so the game goes on. With `ARCANUM_SHOTS` it saves a picture of each stuck choice (with and without the player's own half). Each choice made with the buttons is printed as `PAD PLAY: <decision type>: <steps pressed> (<presses> presses)`. When the game ends it prints `PAD PLAY: game over on turn <n>` with the choices made, the stuck ones and the presses, then quits after three seconds |
| `ARCANUM_PAD_PLAY_GAMES=<n>` | With `ARCANUM_PAD_PLAY=1`: plays n games (default 1). Each game after the first is dealt from the next `ARCANUM_SEED` (default 1, plus one per game) and the scene is reloaded; the game quits after the last one |

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

Online career events can be tested with a local career service (`ARCANUM_CAREER_SERVICE=http://127.0.0.1:<port>`). Give each
instance its own `HOME` (its own user data and device account; add `ARCANUM_EOS_DEVICE=1`). The game draws nothing while
the desktop is locked or the windows are hidden, so screenshots come out blank: run each instance in its own virtual
compositor, e.g. `kwin_wayland --virtual --xwayland --width 1280 --height 720 --socket <name> --exit-with-session <script>`,
with the game started with `--display-driver x11`.

The computer player measures are tests, not game options. They need `ARCANUM_MODULE_PATH` (and the card data as in
`RealCardData`, see [engine-map.md](engine-map.md), *Computer players*). Without their variables they do nothing:

| Variable | Effect |
|----------|--------|
| `ARCANUM_MATCHUP=<deck prefix>\|all` | `BotMatchupTests`: the bot with a deck plan plays the plain bot on the module's 60-card decks whose names start with the prefix (`all`: every deck). Prints its share of the wins, overall and per deck |
| `ARCANUM_MATCHUP_GAMES=<n>` | Games per pairing and seat order (default 2) |
| `ARCANUM_MATCHUP_SAME=1` | Both seats get the same bot (the measure's own noise) |
| `ARCANUM_MATCHUP_GUESS=set\|seen\|none` | Which cards the searching bot imagines in an opponent's hidden cards (see [engine-map.md](engine-map.md), *Computer players*): `set` the set in the deck prefix (the part before a dash), as in a limited game; `seen` the sets of the cards seen (default); `none` only the cards seen |
| `ARCANUM_MATCHUP_PARTS=<names>` | The `PlanParts` in use (for example `Lands`), to measure each part alone (default `All`) |
| `ARCANUM_MATCHUP_PLANS=1` | Prints each deck's plan (`DeckPlan.ToString`) |
| `ARCANUM_MATCHUP_LOG=<folder>` | Writes a readable log of each game into that folder |
| `ARCANUM_MATCHUP_SEARCH=<rollouts>x<horizon>` | The plan bot also searches, with that many rollouts and turns (for example `4x4`) |
| `ARCANUM_MATCHUP_COPY=0` | The searching plan bot rebuilds the game for each rollout by replay instead of copying it (see [game-copy.md](game-copy.md)) |
| `ARCANUM_MATCHUP_BUDGET=<milliseconds>` | The most time the searching plan bot may spend on one decision (no limit when unset) |
| `ARCANUM_MATCHUP_WORKERS=<n>` | How many rollouts may run at once (the `Simulator.Workers` semaphore, by default the processors less one) |
| `ARCANUM_MATCHUP_SERIAL=1` | One game at a time (the default runs one fewer than the processors). The summary prints the searched decisions' average and longest time |
| `ARCANUM_MATCHUP_COMMANDER=<prefix>\|all` | `PlanBotInCommanderWhenProvided`: four of the module's commander decks at a table, the plan bot in each seat in turn (equal bots score 25%). `ARCANUM_MATCHUP_GAMES` gives the number of tables (default 20) |
| `ARCANUM_FORK_COST=<deck prefix>` | `ForkCostTests`: plays recorded bot games, replays each recording into a fresh game and checks that it ends the same way. Prints the times of playing, thinking and replaying |
| `ARCANUM_COPY_CHECK=<deck prefix>\|all` | `GameCopyTests`: plays recorded bot games, copies the game at each copy point and plays the copy on with the same answers. It must end with the same events and state as the original. Needs `ARCANUM_MODULE_PATH`; prints the time of a copy next to a replay |
| `ARCANUM_COPY_GAMES=<n>` | With `ARCANUM_COPY_CHECK`: games to check (default 20) |
| `ARCANUM_WRITE_COPIERS=1` | Writes `src/Arcanum.Engine/Game.Copiers.g.cs` again (the generated copiers) when `GeneratedCopiersTests` runs, e.g. `ARCANUM_WRITE_COPIERS=1 dotnet test tests/Arcanum.Engine.Tests --filter GeneratedCopiers`. Needed after a field of the game's types changes. Does not need `ARCANUM_MODULE_PATH` |

## Online play
A player hosts from the Online screen and gives the others an address. The host listens on TCP port 47013 (by
default): players on the same network join with its local address; players elsewhere need the port forwarded to the
host (it is tried automatically with UPnP), and a firewall on the host must allow it (on Fedora:
`sudo firewall-cmd --add-port=47013/tcp`). Every player needs the same game version and content module.

On the same network the host's lobby also shows up in every player's **Open lobbies** list and can be joined with its
invite code: lobbies are announced on UDP port 47014 and players connect to a TCP port the system picks, so a firewall
on the host must allow those too. See [online-services.md](online-services.md).
