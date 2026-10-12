# Engine map

Where things live in the rules engine and how a new mechanic goes in. The script vocabulary itself is in
[card-scripts.md](card-scripts.md); this file is about the code behind it.

## Projects

| Project | What it holds |
|---------|---------------|
| `src/Arcanum.Engine` | The rules: game state, turn structure, priority, stack, combat, abilities, mana. No card names. |
| `src/Arcanum.Data` | Card data import (`CardData/`), script parsing (`Scripts/`), formats and deck checks (`Formats/`), boosters and limited (`Limited/`), career, modules. |
| `src/Arcanum.Bots` | The computer players (`BotController`). |
| `scripts/` | The Godot client (UI, board, menus). |
| `tools/SupportDump` | Lists which cards are fully supported and why not (`support.tsv`, `why.tsv`). |
| `tests/Arcanum.Engine.Tests` | Rules tests with invented cards (`Scenario`, `TestController`). |
| `tests/Arcanum.Data.Tests` | Parsing, vocabulary, decks, boosters and the whole-set game soak (`SetGamesTests`). |

Game content (scripts, sets, decks) lives in the content module repository, not here.

## From card data to a definition

1. `OracleJsonl.Import` reads card records.
2. `CardFactory.Create(record, script)` (`Arcanum.Data/CardData/CardFactory.cs`) builds a `CardDefinition`: it derives keywords and
   simple abilities from the oracle text, merges the module's script, and decides `CardSupport`. `CardFactory.Explain` says why a card
   isn't fully supported. Keywords a script must spell out are in `ScriptedKeywords`; derived ones in `SupportedKeywords`. Flavor and
   ability words before " — " are labels and are stripped before keywords are derived.
3. `CardDatabase` (`CardData/CardDatabase.cs`) holds every card by name, links pairs that need each other (meld), and lists what can
   be searched, collected and built with.

## Scripts

`Arcanum.Data/Scripts/CardScript.cs` parses the JSON:

| Piece | Parser | Engine type |
|-------|--------|-------------|
| Effects | `ParseEffect` | records deriving `Effect` (`Abilities/Effects*.cs`) |
| Quantities | `ParseQuantity` | `Quantity` / `QuantityKind` (`Abilities/Quantity.cs`) |
| Conditions | `ParseCondition` | `Condition` records (`Abilities/Conditions.cs`) |
| Subjects ("target", "each ...") | `ParseSubject` | `Subject` / `SubjectKind` (`Abilities/Targets.cs`) |
| Filters | `ParseFilter` | `ObjectFilter` |
| Triggers | `ParseTrigger` | `TriggerEvent` (`Abilities/AbilityDefinition.cs`) |
| Card-wide flags | root properties | `CardDefinition` properties and the `Replacements` flags (`Cards/CardDefinition.cs`) |

Large groups of related vocabulary get their own partial files so the main switch stays readable: e.g. `CardScript.Heroes.cs`
(`ParseHeroesEffect` / `ParseHeroesQuantity`, tried first by `ParseEffect` / `ParseQuantity`), `Effects.Heroes.cs` (the records) and
`Game.Heroes.cs` (`ApplyHeroesAsync`, `EvalHeroes`). Follow that pattern for a new group.

`Replacements` is a `long` flag set and is full; new card-wide rules are `bool` (or value) properties on `CardDefinition`, parsed in
`CardScript` and merged in its `Merge`.

## Where the rules run

| Concern | Where |
|---------|-------|
| Effects | `Game.ApplyAsync` (`Game.Abilities.cs`), one `case` per effect record; groups delegate to their partial file |
| Quantities, conditions, subjects | `Eval`, `HoldsIn`, `CardsFor` / `PlayersFor` (`Game.Abilities.cs`) |
| Filters | `Matches` (`Game.Abilities.cs`), targeting in `Game.FiltersAndTargeting.cs` |
| Triggered abilities | `Observers` and `QueueObservers` (`Game.Abilities.cs`); trigger bookkeeping in `Game.TriggerTracking.cs` |
| Static abilities, layers | `RecomputeContinuousEffects` (`Game.Abilities.cs`) |
| Casting, costs, modes, X | `Game.Priority.cs` (casting flow), `Game.CastingCosts.cs` (ways to cast, `CanPayToCast`) |
| Mana payment | `Rules/ManaPayment.cs`, `Mana/ManaCost.cs`, `Mana/ManaPool.cs` |
| Zone changes and their replacements | `MoveCardAsync` (`Game.Zones.cs`) |
| Damage, prevention, redirection | `DealDamageEventAsync` (`Game.Damage.cs`), `Game.ReplacementsAndLife.cs` |
| Tapping / untapping | `Tap`, `Untap`, `EnterTapped` (`Game.Permanents.cs`) — always go through these |
| State-based actions | `CheckStateBasedActionsAsync` (`Game.StateBasedActions.cs`) |
| Turn structure, extra steps | `Game.Turn.cs` |
| Combat | `Game.Combat.cs` |
| Commander rules | `Game.Commander.cs` |
| Per-card and per-game state | `State/Card.cs`, `State/Player.cs`, `State/GameState.cs`, `State/StackItem.cs` |
| Events the UI and log see | `Events/GameEvent.cs` |

## Adding a mechanic

1. Read the rule in the comprehensive rules and decide the exact behaviour, choices and UI it implies (no approximations).
2. Add the engine record (effect, quantity kind, condition, trigger event or definition property) with a doc comment quoting the
   rules text it implements.
3. Parse it in `CardScript` (or the group's partial file).
4. Execute it in the matching place from the table above. Choices go through the player controller (`ChooseYesNoAsync`,
   `ChooseCardsAsync`, ...) so bots and the UI both handle them.
5. If the mechanic is a keyword, make `CardFactory` derive it or list it in `ScriptedKeywords`.
6. Write a test in `tests/Arcanum.Engine.Tests` with invented cards (see `HeroesTests.Make` for building one from a script).
7. Document the vocabulary in `card-scripts.md`.
8. Run `dotnet build Arcanum.csproj`, the test projects, and a soak of the set that needs it (`SetGamesTests`, environment variables
   `ARCANUM_MODULE_PATH` (absolute), `ARCANUM_CARD_FILE`, `ARCANUM_SMOKE_SET`, `ARCANUM_SMOKE_GAMES`).
