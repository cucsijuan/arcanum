# Game modes

A game mode is a plugin: a Lua script that adds rules to a game. It declares its deck rules and game settings,
gives every player a mode emblem, and changes the game at fixed points (hooks). A mode becomes a format: it
shows in the deck builder and the play menus, and online it is sent to the other players with the lobby.

The engine side is `src/Arcanum.Engine/GameModes/IGameMode.cs` and `src/Arcanum.Engine/Game.Modes.cs`. The script
side is `src/Arcanum.Data/GameModes/`. See [engine-map.md](engine-map.md) for where the hooks sit in the turn.

## Files and loading

A mode is one `*.lua` file. The game reads them from three folders, in this order:

| Folder | What it is for |
|--------|----------------|
| `res://modes` (`modes/` in the game folder) | The game's own example modes. |
| `modes/` in the content module | Modes the module ships with its cards. |
| `user://modes` | Modes a player adds. |

- A file that doesn't parse, or whose declaration is wrong, is left out. The reason goes to the Godot log as
  `Game mode: <file>: <message>`.
- A mode whose `id` is already taken (by a format or an earlier mode) is left out, with the same kind of message.
- The export includes `modes/*.lua` (`include_filter` in `.github/export/export_presets.cfg`).

## Declaration: `mode { … }`

A script must call `mode { … }` exactly once. Unknown fields are an error. Only the fields below exist.

| Field | Type | Default | Meaning |
|-------|------|---------|---------|
| `id` | text | required | Lowercase letters, digits and dashes, starting with a letter or digit. Unique. |
| `name` | text | required | The mode's name: its format name, and the title of its announcements. |
| `description` | text | `""` | Shown in the lobby ("Game mode: …") and as the format's description. |
| `api` | number | `1` | The plugin API the script needs. A number above this build's API (1) is refused. |
| `format` | text | `"casual"` | The id of a format to build on (not a limited one). Its deck rules are the start. |
| `deck` | table | from `format` | Deck rule changes: `legality`, `minDeckSize`, `maxDeckSize`, `maxCopies`, `sideboardMax`, `basicLandsUnlimited`, `commander`. |
| `game` | table | from the engine | `startingLife`, `handSize`, `maxHandSize`, `landsPerTurn`, `freeMulligan` (true/false), `skipFirstDraw` (true/false). |
| `players` | table | `min = 2`, `max = 4` | `min` (2 or more) and `max` (at least `min`): how many may play. The local setup menu limits the seats to this, and hosting an online game for another number of players is refused. |
| `emblem` | table | the mode's name and description | Each player's mode emblem (see below). |

- `game.startingLife` also sets the format's life total, which the deck builder shows.
- `deck.legality` is a key into each card's legalities. Without it, every card is allowed.
- `deck.commander = true` gives commander rules: a commander section and color identity.

```lua
mode {
  id = "overflow",
  name = "Overflow",
  description = "Any casual deck. Each player may play two lands each turn, draws an additional card in their draw step, and has a maximum hand size of eight.",
  format = "casual",
  game = { landsPerTurn = 2, maxHandSize = 8 },
  emblem = { text = "You may play an additional land each turn.\nAt the beginning of your draw step, draw an additional card.\nYour maximum hand size is eight." },
}
```

## The emblem

Every player gets the emblem as the game begins, before the opening hands. It is an emblem card in the command
zone (`EmblemCreated` is raised for each). Its abilities are card script abilities, and the emblem is the source
of what the mode does for that player: `game.apply` effects and the questions `game.ask` and `game.choose` use it.

- `name`: the emblem's name. Default: the mode's name.
- `text`: the rules text shown. Default: the texts of the abilities, one per line.
- Any other field is card script vocabulary, as in a card's script: `abilities` (with `trigger`, `static`,
  `effects` and so on). See [card-scripts.md](card-scripts.md).

Without an `emblem` table, the emblem has the mode's name and description as its text and no abilities.

## Hooks

Hooks are global functions. Each gets the `game` table first. A hook that isn't defined is not called. A hook name
bound to something that isn't a function stops the mode, except `on_event`, which is then ignored.

| Hook | When it runs | Arguments |
|------|--------------|-----------|
| `on_start(game)` | Once, after every player has kept an opening hand, before the first turn. | `game` |
| `on_step(game, step)` | At each step, after the step's turn-based actions and before anyone gets priority. In the cleanup step it runs before the cleanup's turn-based actions. | `game`, the step's name in camelCase (`"upkeep"`, `"precombatMain"`, `"cleanup"`, …) |
| `on_event(game, e)` | After state-based actions, for each event since the last time. Events are given in order. Events caused by the mode's own actions are given too. Also during the cleanup step. | `game`, the event table (see below) |
| `check(game)` | Each time state-based actions have been checked before priority, after the events were given, and during the cleanup step. A mode's own win and loss checks go here. | `game` |

- When a hook changes the game, state-based actions are checked again and the hooks run again. This repeats
  until nothing changes. The engine stops a mode that changes the game 1,000 times in a row.
- Nothing a hook does goes on the stack. Effects happen at once, like a turn-based action.
- `check` runs often (every time players would get priority). Keep it cheap.

## The `game` table

Players are numbers from 1, in seat order. Cards are their ids (numbers). The queries and actions below work only
inside a hook. Called anywhere else, they are errors.

### Queries

| Function | Returns |
|----------|---------|
| `game.players()` | List of all players. |
| `game.living()` | List of the players who haven't lost. |
| `game.opponents(p)` | List of the opponents of player `p`. |
| `game.player(p)` | Table: `id`, `name`, `life`, `poison`, `hand`, `library`, `graveyard`, `exile` (counts), `lost` (true/false), `monarch` (true/false). |
| `game.turn()` | The turn number. |
| `game.active()` | The active player. |
| `game.step()` | The current step's name in camelCase. |
| `game.card(id)` | A card table, or nil. Fields: `id`, `name`, `zone` (camelCase, such as `"battlefield"`), `owner`, `controller`, `types` (list, such as `"Creature"`), `supertypes`, `subtypes`, `colors`, `power` and `toughness` (creatures only), `tapped`, `token`, `counters` (table keyed by camelCase counter name, counts above 0 only). |
| `game.cards(p, zone)` | List of card ids of player `p` in `zone`: `"hand"`, `"library"` (top first), `"graveyard"`, `"exile"`, `"command"`, `"battlefield"` (the permanents they control). |
| `game.emblem_of(p)` | The id of player `p`'s mode emblem. |

### Actions

| Function | Does |
|----------|------|
| `game.apply(p, effects)` | Applies one effect table, or a list of them, as player `p`'s emblem would: "you" is `p`. The effects are card script effects without targets. An invalid effect stops the mode. |
| `game.draw(p, n)` | `p` draws `n` cards (default 1). Same as `apply` with `{ draw = n }`. |
| `game.gain_life(p, n)` | `p` gains `n` life. |
| `game.lose_life(p, n)` | `p` loses `n` life. |
| `game.mill(p, n)` | `p` mills `n` cards. |
| `game.emblem(p, emblem)` | Gives `p` an emblem with `name` (default `"Emblem"`), `text` and `abilities`. |
| `game.ask(p, question, bot)` | Asks `p` a yes/no question. Returns true or false. `bot` (true or false) is the computer's answer; without it the computer decides. |
| `game.choose(p, prompt, options, bot)` | Asks `p` to choose one of `options` (a list of texts). Returns the chosen index, counted from 1. `bot` is the computer's choice, as an index from 1. |
| `game.lose(p, reason)` | `p` loses the game, unless an effect says they can't lose. `reason` is optional (default: `lost by the rules of <mode name>`). It goes to the log. |
| `game.win(p)` | `p` wins: every opponent who can lose loses at once. |
| `game.extra_turn(p)` | `p` takes an extra turn after this one. |
| `game.announce(text)` | Tells every player `text`, under the mode's name, on the board and in the log. |

## Event tables

`on_event` gets one table per event. Its `type` is the event's name, as in `src/Arcanum.Engine/Events/GameEvent.cs`
(for example `"LifeChanged"`). The other fields are the event's fields in camelCase. Players are numbers from 1,
cards are ids, enum values are camelCase text, lists are arrays. A field with no value (such as a missing target) is nil.

| Event | Fields |
|-------|--------|
| `TurnBegan` | `turnNumber`, `activePlayer` |
| `StepBegan` | `step`, `activePlayer` |
| `CardMoved` | `card`, `owner`, `from`, `to` (zones), `lastController` |
| `CardDrawn` | `player`, `card` |
| `SpellCast` | `player`, `card` |
| `SpellResolved` | `card` |
| `LandPlayed` | `player`, `card` |
| `LifeChanged` | `player`, `oldLife`, `newLife` |
| `DamageDealt` | `source`, `targetCard`, `targetPlayer`, `amount`, `isCombat` |
| `CreatureDied` | `card` |
| `PermanentDestroyed` | `card` |
| `CountersPlaced` | `card`, `kind`, `count`, `by` |
| `TokenCreated` | `card`, `controller` |
| `AttacksDeclared` | `player`, `count` |
| `AttackerDeclared` | `attacker`, `defender` |
| `BlockerDeclared` | `blocker`, `attacker` |
| `ControlChanged` | `card`, `newController` |
| `MonarchChanged` | `player` |
| `PoisonGiven` | `player`, `count` |
| `EmblemCreated` | `emblem`, `owner` |
| `PlayerLost` | `player`, `reason` |
| `GameEnded` | `winner` (nil for a draw) |
| `ModeAnnounced` | `mode`, `text` |

Other events exist (see `GameEvent.cs`); they arrive with the same shape. Check `e.type` and ignore the rest.

## Sandbox and limits

The script runs in a Lua interpreter (MoonSharp, pure .NET) with a restricted set of libraries.

- Available: the basic functions (`print` writes to the debug output only), `string`, `table`, `math`, `bit32`,
  `pcall` and `error`, metatables and table iterators.
- Not available: files, the operating system, `io`, `os`, `debug`, `load`, `dofile`, `require` and coroutines.
  Each of these is nil.
- `math.random` uses the game's seed. `math.randomseed` does nothing.
- `string.rep` refuses a result over 1,000,000 characters.
- The script is at most 256 KB.
- Each hook call may run about two million Lua instructions. A hook that runs longer stops the mode.
- One delivery of events to a mode is capped at 10,000 events, counting the events its own reactions cause. More
  stops the mode.
- Top-level code runs once when the script is read (to find the declaration), and again at the start of each game.
  Keep it to definitions. Calling `game.*` there is an error.
- Globals start fresh in each game. A mode object belongs to one game.

## When a mode fails

- **A script that doesn't read:** the mode is not offered. The Godot log says why. An online player who can't read
  the host's mode sees the lobby without its deck rules, and gets a warning.
- **A failing hook:** a Lua error, an exhausted budget, an invalid effect, a bad argument, or an endless loop of
  events or changes stops the mode for the rest of the game. The board and the log announce it under the mode's
  name, with the text "The game mode stopped working, and the game goes on without it: <reason>". The emblems
  stay. Their abilities are normal card abilities and keep working.
- **A mode that can't start** (its setup throws): the game starts without it, with an announcement saying so.

## Determinism

The host's engine decides everything, so a mode must give the same result for the same game. Use only the game's
state, the mode's own variables and `math.random`. Do not depend on time, files or anything outside the game.
Each game (including a game played again to undo or to resume) makes a fresh mode and gets the same seed and
answers.

## Online

- The host's game runs the mode. Hooks run on the host only; other players' computers don't run the script.
- The host sends the script's text to each player who joins (`Joined.Mode`, at most 256 KB). Players parse it to
  show the mode's rules and to filter decks by its format.
- A hosted game saves the script it started with. A resumed game replays with that script, even if the file has
  changed since.
- `GameModeScript.Hash` is the SHA-256 of the script's text. Tests compare it.

## Testing

- `ARCANUM_MODE=<id>` in the environment: the board's sample-deck start (hotseat, with the module's sample
  decks) plays with that mode. Use it to try a script without the menus.
- In tests, `tests/Arcanum.Data.Tests/GameModeTests.cs` shows the pattern: `GameModeScript.Parse(source)`, then a
  game with computer players run to the end. Assert on `game.Log` (for example `ModeAnnounced`, `LifeChanged`).
- The example modes in `modes/` are played by a test with three seeds. Each must finish without stopping.
- The lobby test `TheHostSendsItsGameModeAndPlaysWithIt` (`tests/Arcanum.Net.Tests/LobbyTests.cs`) checks that
  a joining player gets the script and the host plays with its settings.

## Worked example

`modes/overflow.lua` changes the settings and adds a step hook:

```lua
function on_step(game, step)
  if step == "draw" then game.draw(game.active(), 1) end
end
```

For a mode with an emblem that has an ability, `modes/life-race.lua` declares an upkeep ability on the emblem
and a `check` hook that ends the game. `modes/sudden-death.lua` uses `on_step` to count turns, `game.announce`,
`game.win` and an `on_event` hook that makes a life loss lose the game.

To try one: copy the file to `user://modes` (or `modes/` in the game folder), start the game, and pick the
mode in the play menu, or start with `ARCANUM_MODE=<id>`.
