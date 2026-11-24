# Engine map

Where things live in the rules engine and how a new mechanic goes in. The script vocabulary itself is in
[card-scripts.md](card-scripts.md); this file is about the code behind it.

## Projects

| Project | What it holds |
|---------|---------------|
| `src/Arcanum.Engine` | The rules: game state, turn structure, priority, stack, combat, abilities, mana. No card names. |
| `src/Arcanum.Data` | Card data import (`CardData/`), script parsing (`Scripts/`), formats and deck checks (`Formats/`), boosters and limited (`Limited/`), career, modules. |
| `src/Arcanum.Bots` | The computer players (`BotController`, its deck plan, and search by simulation in `Simulation/`). See [Computer players](#computer-players). |
| `scripts/` | The Godot client (UI, board, menus). |
| `tools/SupportDump` | Lists which cards are fully supported and why not (`support.tsv`, `why.tsv`). |
| `tools/PrepareCardData` | Makes a module's `card-data.bin` from the sources in its `sources.json` (`dotnet run --project tools/PrepareCardData -- <module folder> <output folder>`). The module's release workflow runs it. |
| `tests/Arcanum.Engine.Tests` | Rules tests with invented cards (`Scenario`, `TestController`). |
| `tests/Arcanum.Data.Tests` | Parsing, vocabulary, decks, boosters and the whole-set game soak (`SetGamesTests`). |

Game content (scripts, sets, decks) lives in the content module repository, not here.

## Content modules and updates

The game knows no module by name. It finds a module by its folder (one with `manifest.json` and `sources.json`).

| Piece | Where |
|-------|-------|
| Choosing the module folder (`FindModuleDirectory`): `ARCANUM_MODULE_PATH`, then in editor runs a sibling folder of the project, then the installed modules, the one installed last first | `scripts/Client/ContentLoader.cs` |
| Module zips next to the game: `*.zip` files in the executable's folder that hold a module not installed, or newer than the installed one, and not declined at that version (`Settings.DeclinedModuleZips`). Offered on the first screen and installed before the cards load. Skipped in editor runs and with `ARCANUM_MODULE_PATH` | `scripts/Client/ModuleZips.cs`, `App.OfferModuleZipsAsync` (`scripts/Client/App.cs`) |
| Reading a zip's manifest without extracting it (`ModuleInstaller.PeekManifest`); installing a zip (`InstallFromZip`), which sets the installed manifest's time to the install time. Files other than `scripts/` are extracted; the zip is copied whole as `scripts.zip`, and card scripts are read from it. `ModuleInstaller.BringsCardData` tells whether the zip holds `card-data.bin` (the install dialogs use it) | `src/Arcanum.Data/Modules/ModuleInstaller.cs` |
| A module's card scripts: `scripts/*.json` in a folder, else the entries of `scripts.zip` (`ContentModule.ScriptFiles`). `ScriptReader()` parses one card's script when that card is asked for; nothing is unpacked at start | `src/Arcanum.Data/Modules/ContentModule.cs` |
| Card data a zip brings already made (`card-data.bin`, `OracleJsonl.PreparedFileName`): the card records in the card cache's binary encoding (`CardCache.WriteRecords`, `ReadRecords`, `IsRecordsFile`), under the import-rules version `OracleJsonl.CompactFormat`. Made by `tools/PrepareCardData` (`OracleJsonl.Prepare`, then `ReadCompact`) | `src/Arcanum.Data/CardData/OracleJsonl.cs`, `src/Arcanum.Data/CardData/CardCache.cs` |
| `manifest.json` fields (`ModuleManifest`, `ContentModule.ReadManifest`), including the optional `updates` | `src/Arcanum.Data/Modules/ContentModule.cs` |
| Update checks: the game's newest release (`NewerGameAsync`), and the installed module's newest release at its `updates` URL (`NewerModuleAsync`; no `updates` field, no check). Both read a "latest release" document in GitHub's releases API format. A connection that can't be made is tried up to 4 times, 2, 4 and 6 s apart (`LatestAsync`) | `scripts/Client/Updater.cs` |
| Installing a downloaded game (`InstallGameAndRestart`). Desktop: the zip is unpacked, and a script copies it over the install folder once the game has exited, then starts the game. Console (`Switch`, asset `-console.zip`): `InstallConsoleGame` writes the NRO to the update slot not running (`update_a.bin` / `update_b.bin` beside it), makes an `update.install` marker and starts the slot with `OS.CreateProcess`; the console port then makes it the menu's `arcanum.nro` | `scripts/Client/Updater.cs`; the console side in `godot-switch`'s `port/platform-switch/` |

## From card data to a definition

1. `OracleJsonl.Import` reads card records. A printing whose `promo_types` has `fracturefoil` is marked `Fracture` (`CardRecord.Printings`), and the compact card file stores it as `fracture_foil`. The same record carries each printing's market prices (`PrintingPrices`: Cardmarket euros and TCGplayer dollars, foil and nonfoil) and its attributes (`Etched`, `Border`, `Frame`, `FrameEffects`, `PromoTypes`, ...); the compact card file keeps them as well, under `cards.v14-<module version>`. A reversible printing (layout `reversible_card`) whose two faces are the same card has its oracle id only on the faces; `ReversibleOracleId` (`OracleJsonl.cs`) takes that id, so the printing is kept under its card.
2. `CardFactory.Create(record, script)` (`Arcanum.Data/CardData/CardFactory.cs`) builds a `CardDefinition`: it derives keywords and
   simple abilities from the oracle text, merges the module's script, and decides `CardSupport`. `CardFactory.Explain` says why a card
   isn't fully supported. Keywords a script must spell out are in `ScriptedKeywords`; derived ones in `SupportedKeywords`. Flavor and
   ability words before " — " are labels and are stripped before keywords are derived.
3. `CardDatabase` (`CardData/CardDatabase.cs`) holds every card by name, links pairs that need each other (meld), and lists what can
   be searched, collected and built with. The client gets it from the card cache when the cache matches, and otherwise builds it
   with `CardDatabase.MadeOnDemand` (see below).

`ContentLoader` (`scripts/Client/ContentLoader.cs`) picks the card file: the module's `card-data.bin` when it is there and
readable (used where it is, nothing downloaded or read as text), else the compact file `cards.v14-<module version>.jsonl.gz`
under `user://card_data/<module id>/`. A missing compact file is made from the source's downloaded card and printing files
(`PrepareFromSourceAsync`). A module checkout normally has no `card-data.bin` and takes this path.

## Card cache

`CardCache` (`Arcanum.Data/CardData/CardCache.cs`) keeps the built `CardDatabase` on disk, as `user://card_data/<module id>/cards.v14-<module version>.cache`,
next to the compact card file. It stores the records (each string once), the lookups exactly as built (by name, including the
adventurer first-face aliases, the sorted order, tokens by name, meld links), each card's support and name, the sets, and the
nonbasic-land and creature name lists. Definitions are not stored.

- **Key** (`CardCache.Key`): a cache is used only when the format, the builds of `Arcanum.Data` and `Arcanum.Engine` (their module
  version ids), the card file it is read with (`card-data.bin` or the compact card file: name, size, last write) and every `scripts/*.json` file (name, size, last write; contents instead of last write where every file has the same time, as
  in a read-only image stamped with its mount time) match the
  ones it was written from. Any change makes the cache read as missing.
- **Rebuild** (`ContentLoader`, `scripts/Client/ContentLoader.cs`): a missing or unmatched cache makes the database with
  `CardDatabase.MadeOnDemand`, which is ready at once. Each card's definition and support are made when first asked for.
  `CardFactory.NameOf` gives a card's name without making it, so the start doesn't wait for the cards; only meld cards are made
  at once. When every card is made, the cache is written (through a temporary file) and older `cards*.cache` files are deleted.
  A database read from the cache is not written again.
- **Lazy definitions**: a `CardEntry` read from the cache makes its `Definition` the first time it is asked for (once, thread-safe).
  `CardDatabase.MakeDefinitionsAsync` makes the rest on the thread pool after loading. `ContentModule.ScriptReader()` parses one
  card's script by oracle id for this; it gives the same result as `ContentModule.LoadScripts`.
- **Keeping it right**: a new field on `CardRecord`, `Printing`, `CardFaceRecord`, `RelatedToken` or `PrintingPrices` needs
  `CardCache`'s writer and reader updated. `CardCacheTests.EveryRecordFieldIsKept` fails until it is. A change to the file's
  layout (what the writer and reader store) needs the cache's `Format` number bumped, so older files read as missing.

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
`Game.Heroes.cs` (`ApplyHeroesAsync`, `EvalHeroes`). The commander decks of the heroes set use the same split: `CardScript.HeroesCommander.cs` (`ParseHeroesCommanderEffect`,
`ParseHeroesCommanderQuantity`), `Effects.HeroesCommander.cs` and `Game.HeroesCommander.cs` (`ApplyHeroesCommanderAsync`,
`EvalHeroesCommander`). The academy set follows the same pattern: `CardScript.Academy.cs` (`ParseAcademyEffect`, `ParseAcademyQuantity`),
`Effects.Academy.cs` and `Game.Academy.cs` (`ApplyAcademyAsync`, `EvalAcademy`). Follow that pattern for a new group.

`Replacements` is a `long` flag set and is full; new card-wide rules are `bool` (or value) properties on `CardDefinition`, parsed in
`CardScript` and merged in its `Merge`.

## Where the rules run

| Concern | Where |
|---------|-------|
| Effects | `Game.ApplyAsync` (`Game.Effects.cs`) tries the four parts in order (`Game.Effects.Part1.cs` to `Part4.cs`), one `case` per effect record |
| Quantities, conditions, subjects | `Eval` (`Game.Quantities.cs`), `HoldsIn` (`Game.Conditions.cs`), `CardsFor` / `PlayersFor` (`Game.Effects.cs`) |
| Quantities by target loyalty, domain, distinct powers, graveyard mana values, cards milled or drawn this way | `EvalFracture` (`Game.Fracture.cs`); parsed by `ParseFractureQuantity` (`Arcanum.Data/Scripts/CardScript.Fracture.cs`) |
| Filters | `Matches` (`Game.Layers.cs`), targeting in `Game.FiltersAndTargeting.cs` |
| Triggered abilities | `Observers` and `QueueObservers` (`Game.Triggers.cs`); trigger bookkeeping in `Game.TriggerTracking.cs` |
| Static abilities, layers | `RecomputeContinuousEffects` (`Game.Layers.cs`). Every layer and sublayer runs through `ApplyLayer` (`Game.LayerDependencies.cs`), which orders its effects by dependency, then timestamp (rule 613.8); `NextInDependencyOrder` picks the next one. `EffectLocks` keeps an effect on the objects it first applied to in every later layer (rule 613.6), even after its ability is lost. The preliminary layer 6 pass (`ApplyAbilityLayer`) decides which abilities are granted for layers 2 to 5, and abilities lost in layer 6 still count there |
| Switching power and toughness (`switchPowerToughness`, layer 7d) | `SwitchPowerToughness` adds an until-end-of-turn effect with `SwitchesPowerToughness` (`Game.Effects.Part4.cs`); the 7d pass in `Game.Layers.cs` swaps them, two switches undo each other |
| Land types set by a static (`setLandType`, rule 305.7, in the land-type layer) | `LandTypeSetBy` (`Game.Layers.cs`), with `SetLandType` on `StaticAbility` (`Abilities/StaticAbility.cs`) |
| Casting, costs, modes, X | `Game.Priority.cs` (casting flow), `Game.CastingCosts.cs` (ways to cast, `CanPayToCast`) |
| Mana payment | `Rules/ManaPayment.cs`, `Mana/ManaCost.cs`, `Mana/ManaPool.cs` |
| Zone changes and their replacements | `MoveCardAsync` (`Game.Zones.cs`) |
| Cards outside the game (a sideboard; `revealTwoFromOutsideOpponentChooses`) | `Zone.Outside` (`State/Zone.cs`; hidden like the library and hand, `IsPublic`), `Player.Outside` (`State/Player.cs`). Game setup (`Game.cs`) puts each `PlayerSetup.Sideboard` (`GameConfig.cs`) there. A deck's sideboard comes from `DeckList.ResolveSideboard` (`Arcanum.Data/Decks/DeckList.cs`); in limited play, `EventSeat.SideboardFromPool` (`Arcanum.Data/Limited/LimitedEvent.cs`) gives the pool's cards not in the deck |
| Preparation cards and Empower Jace (the copy in exile, its cast, unpreparing) | `Game.Prepared.cs`; the cast list is `PreparedSpells` (`Game.Priority.cs`), leaving the battlefield calls `Unprepare` (`Game.Zones.cs`) |
| Modal double-faced cards (layout `modal_dfc`: either face is cast or played; the face up on the stack, on the battlefield and in other zones, rules 712.11 to 712.14) | `CardDefinition.IsModalDoubleFaced` (`Cards/CardDefinition.cs`), set in `CardFactory.Create` (`Arcanum.Data/CardData/CardFactory.cs`), which builds the card with `CreateTransforming`; `Card.Transformed` and `Card.ManaValue` (`State/Card.cs`); the back-face flags on `PlayLand` and `CastSpell` (`Players/PlayerAction.cs`); both faces listed in `GetLegalActions` (`Game.Priority.cs`), which runs the castable list again with each modal card's back face up; `CastSpellAsync`, `ChooseModalFaceAsync`, `CastableFaces` and `CanBeCastAnyFace` (`Game.Priority.cs`) for effects that cast it; the face on the battlefield in `MoveCardAsync` (`Game.Zones.cs`) |
| Damage, prevention, redirection | `DealDamageEventAsync` (`Game.Damage.cs`), `Game.ReplacementsAndLife.cs`. Each part of a damage event goes through `ModifyDamagePartAsync` (`Game.Damage.cs`), which takes its modifiers from `DamageModifiers` and asks the affected player for the order (rule 616.1). No question is asked when there is one kind of modifier, or when all of them are preventions that cost and cause nothing (`PlainPrevention`) |
| Prevention whose effects happen at once (`preventNextFromChosenSource` with `"immediately"`) | `Immediately` on the effect (`Abilities/Effects.cs`), set as `WhenPreventedImmediately` (`Game.SpecialEffects.cs`) on the shield (`PreventionShield`, `State/GameState.cs`); `_preventedDamageEffects` (`Game.Damage.cs`), run after the damage event |
| Impending (cast for its impending cost, enters with time counters, not a creature while it has one, a counter removed at its controller's end step) | `CastingWayKind.Impending` (`Game.CastingCosts.cs`); `SpellOnStack.Impended` and the enter counters (`Game.Priority.cs`, `_enteringImpended` and `MoveCardAsync`); `Card.Impended` (`State/Card.cs`, set in `Game.Zones.cs`); the type layer (`Game.Layers.cs`) and `ImpendingEndStep` (`Game.Triggers.cs`) |
| Morph and face-down spells and permanents (cast face down for {3} as a 2/2 with no name, text or mana cost; turned face up for the morph cost as a special action; revealed when they leave) | `CardDefinition.Morph` set from the rules text by `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `CastingWayKind.FaceDown` and `AsFaceDown` (`Game.CastingCosts.cs`); the face-down cast flow and the turn-up actions in the priority list (`Game.Priority.cs`); `TurnFaceUp` (`Players/PlayerAction.cs`), `TurnFaceUpActions` and `TurnFaceUpAsync` (`Game.Morph.cs`); `Card.TurnedFaceDown` and `Card.FaceDownCreature` (`State/Card.cs`); the reveal on a zone change in `MoveCardAsync` (`Game.Zones.cs`); `FaceDown` and `FaceDownCard` on `CardView` (`Views/GameView.cs`, built in `Views/ViewBuilder.cs`) |
| Megamorph (turned face up for its megamorph cost, it gets a +1/+1 counter, rule 702.37b) | `CardDefinition.Megamorph` (`Cards/CardDefinition.cs`), set together with `Morph` from the "Megamorph {cost}" line in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`, `MegamorphLine`); the counter is put in `TurnFaceUpAsync` (`Game.Morph.cs`), unless the turn-up was paid with a mana cost (`TurnFaceUp.ForManaCost`) |
| Choose a card type as it enters, and protection from it | `EnterChoice.CardType` (`Game.Priority.cs`); `ChoosableCardTypes`, `PlayerProtectionFromTypes`, `PlayerProtectedFromSource` (`Game.Targeting.cs`), the granted protection in the layers (`Game.Layers.cs`); damage from such sources in `Game.Damage.cs` |
| Toxic (combat damage to a player gives poison) and `hasToxic` | `Game.Damage.cs` (`Toxic` on `CardDefinition`, set from a token spec's `toxic`); the filter in `Matches` (`Game.Layers.cs`) |
| Incubate, tokens that enter with counters, predefined Incubator and Map tokens | `Incubate` (`Game.Effects.Part2.cs`); `CreateTokenEventAsync` and `CreateTokenAsync` (`enterCounters`); `PredefinedTokens.Incubator` and `PredefinedTokens.Map` (`Cards/PredefinedTokens.cs`) |
| "Whenever you create one or more tokens" | `TokensCreated` event (`Events/GameEvent.cs`), emitted once per batch by `CreateTokenEventAsync`; the trigger is queued in `Game.Triggers.cs` |
| Delayed "at the beginning of the next end step" | `AtNextUpkeep` with `EndStep` (`Abilities/Effects.cs`); `State.AtNextEndStepTriggers` (`State/GameState.cs`), released at the end step in `Game.Triggers.cs` |
| Cards exiled with this, put into a graveyard | `PutExiledWithThisIntoGraveyard` (`Game.Effects.Part2.cs`); `TriggeredInGraveyard` (`Abilities/Conditions.cs`, checked in `Game.Conditions.cs`) |
| Tapping / untapping | `Tap`, `Untap`, `EnterTapped` (`Game.Permanents.cs`) — always go through these |
| State-based actions | `CheckStateBasedActionsAsync` (`Game.StateBasedActions.cs`) |
| Role tokens (one Role per controller on a permanent, only the newest stays; rule 704.5y) | the Role check in `CheckStateBasedActionsAsync` (`Game.StateBasedActions.cs`); `"Role token"` in `ScriptedKeywords` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Turn structure, extra steps | `Game.Turn.cs` |
| Additional upkeep steps (`additionalUpkeepStep`, rule 500.8; added only during an upkeep step) | `_extraUpkeeps` and `_upkeepsThisTurn` (`Game.Turn.cs`), used like the additional end steps; `AdditionalUpkeepStep` (`Abilities/Effects.Heroes.cs`) is added in `Game.Heroes.cs` |
| End-of-match procedure when a round's time is up (`CallTime`, the additional turns, a draw) | `Game.cs` (game loop; `TimeCalledAt`, `AdditionalTurns`) |
| Combat | `Game.Combat.cs` |
| Goads (`goad`, `forRestOfGame`) | `Goad` in `Game.Effects.Part4.cs` adds to `State.Goads`, or to `State.GoadsForever` for the rest of the game (`State/GameState.cs`). `Game.Combat.cs` reads both; `State.Goads` entries of a goader are removed at that goader's next turn (`Game.Turn.cs`) |
| Planeswalker attack bans (`cantAttackYourPlaneswalkers`) | `State.PlaneswalkerAttackBans` (`State/GameState.cs`), added by `CantAttackYourPlaneswalkersThisTurn` in `Game.Effects.Part4.cs`; attack targets are checked against it in `Game.Combat.cs` |
| Life that can't change (`protectionAndLifeLock`) | `Player.LifeCantChange` (`State/Player.cs`), set by `ProtectionAndLifeLockUntilTheirTurn` in `Game.Effects.Part4.cs`. `ChangeLife` (`Game.Combat.cs`) and `GainLifeAsync` (`Game.ReplacementsAndLife.cs`) skip changes while it is set; `Game.Turn.cs` clears it at that player's turn |
| Commander rules | `Game.Commander.cs` |
| Per-card and per-game state | `State/Card.cs`, `State/Player.cs`, `State/GameState.cs`, `State/StackItem.cs` |
| Events the UI and log see | `Events/GameEvent.cs` |
| Game modes (Lua plugins: emblems, hooks, `ModeHost` actions) | `GameModes/IGameMode.cs` (the interface and `GameModeException`), `Game.Modes.cs` (`BeginMode` makes the emblems, `RunModeAsync` runs a hook and stops a failing mode, `DeliverModeEventsAsync`, `ModeHost`); `GameConfig.Mode` makes a fresh mode per game (`GameConfig.cs`). Hook points: `on_start` after the opening hands in `RunAsync` (`Game.cs`); `on_step` in `StepAsync` (`Game.Turn.cs`), before cleanup's turn-based actions and before priority otherwise; `on_event` and `check` in `SettleBeforePriorityAsync` (`Game.Priority.cs`) and in the cleanup loop (`Game.Turn.cs`); events are queued in `Emit` (`Game.cs`). `ModeAnnounced` (`Events/GameEvent.cs`) |
| Game mode scripts (declaration, deck rules, the Lua sandbox and `game` API, event tables) | `Arcanum.Data/GameModes/`: `GameModeScript.cs` (the `mode { … }` fields, `ApplyTo`, the emblem), `LuaGameMode.cs` (the sandbox, the `game` table, instruction budget, actions that wait for a player), `LuaValues.cs` (Lua tables to card script JSON, engine events to tables). Format rules of a mode: `FormatRules.Mode` (`Arcanum.Data/Formats/FormatRules.cs`). Reference: [game-modes.md](game-modes.md) |
| Game modes in the client and online | `App.LoadGameModes` (`scripts/Client/App.cs`, loads `res://modes`, the module's `modes/` and `user://modes`); `MatchSetup.Mode` and `PlaySetup` (player count); `GameBoard` (`ARCANUM_MODE`, announcement); `EventLogFormatter` (`ModeAnnounced`); `Joined.Mode` (`Net/Protocol/Messages.cs`) and `LobbyHost` (`Net/Lobby/LobbyHost.cs`, the host's copy of the mode runs the game); `OnlineService` (`LobbyModeFormat`, `HostedSave.Mode`) |
| Tournament rounds, Swiss standings and tiebreakers, playoffs, drops, round clock (`RoundMinutes`, `Deadline`, `TimeUp`, `EndOnTime`) (career and limited events) | `Arcanum.Data/Limited/LimitedEvent.cs`; time called on the game by `LimitedService.EndIfTimeUp` (`scripts/Client/LimitedService.cs`) |
| Career calendar: `when`, `months`, multi-day runs, attendance, `invitational` and invitations | `Arcanum.Data/Career/CareerRules.cs` (calendar section) |
| Offspring (the optional cost at cast; the 1/1 token copy when it enters, from last known information if it has left) | `OffspringTrigger` added by `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `CastingChoices.OffspringPaid` (`Game.CastingCosts.cs`) and `SpellOnStack.OffspringPaid` (`State/StackItem.cs`); `_enteringOffspring` in `MoveCardAsync` (`Game.Zones.cs`) sets `Card.OffspringPaid` (`State/Card.cs`); `LastKnown.OffspringPaid`; quantity `offspringPaid` (`Game.Quantities.cs`); `ResolvingSourceGone` (`Game.SourceObject.cs`) |
| Forage (rule 701.61a: exile three cards or sacrifice a Food, the player's choice) | `ExtraCost.Forage` and the `forage` cost token (`Abilities/Costs.cs`, `Arcanum.Data/Scripts/CardScript.cs`); `ForageWays`, `ChooseForageWayAsync`, `Foraging` (`Game.Priority.cs`); event `Foraged` (`Events/GameEvent.cs`); trigger `YouForage` (`Abilities/AbilityDefinition.cs`); the board's "X forages" line in `scripts/UI/Board/EventLogFormatter.cs` and `scripts/UI/Board/GameBoard.cs` |
| Class levels (level sections, level-up ability, `becomesLevel`, `cardRulesLevel`) | `Card.ClassLevel` and `Card.Abilities` (`State/Card.cs`); `AbilityDefinition.ClassLevel` and `InClassLevelOrder` (`Abilities/AbilityDefinition.cs`); `BecomeClassLevel` (`Abilities/Effects.cs`) and `ClassLevelIs` (`Abilities/Conditions.cs`), applied in `Game.Effects.Part2.cs` and `Game.Conditions.cs`; event `ClassLevelChanged` (`Events/GameEvent.cs`); `Card.HasTextAbilities` for `cardRulesLevel`; the status panel in `scripts/UI/Board/CardStatusPanel.cs` |
| Expend (mana spent on spells this turn, counted from what was actually paid) | `Player.ManaSpentOnSpellsThisTurn` (`State/Player.cs`), added to in `Game.Priority.cs` from the paid mana, reset in `Game.Turn.cs`; event `ManaSpentOnSpell` (`Events/GameEvent.cs`); `TriggerEvent.YouExpend` checked in `Game.Triggers.cs` |
| Valiant counting (first time each turn a spell or ability you control targets the permanent) | `GameState.TargetedThisTurn` (`State/GameState.cs`), recorded in `Game.Targeting.cs`; `TriggerEvent.BecomesTargetOfYours` and its `nth` count in `Game.Triggers.cs`; the trigger filter in `Game.TriggerTracking.cs` |
| Pawprint modes (a spell's budget, each mode's cost in pawprints, repeated modes) | `AbilityDefinition.PawprintBudget` and `Mode.Pawprints` (`Abilities/AbilityDefinition.cs`); parsed by `WithModes` (`Arcanum.Data/Scripts/CardScript.cs`); chosen with `PawprintBudget` in `Game.Targeting.cs` |
| Colored cost reductions (`spellCost` with `"color"`) | `ManaCost.MinusColored` (`Mana/ManaCost.cs`); applied in `Game.CastingCosts.cs` and `Game.Priority.cs` |
| Graveyard cast rights (`castFromGraveyard`, `mayCastFromGraveyard`, extra cost, finality) | `GraveyardCastRight` record and `State.GraveyardCastRights` (`State/GameState.cs`); added in `Game.Priority.cs` and `Game.Effects.Part1.cs`; `GraveyardCastRightsFor` and `GraveyardRightUsed` (`Game.Priority.cs`); `HasGraveyardCastRight` (`Game.Origins.cs`) |
| Free casts with a non-mana cost (the cast is free apart from the extra cost) | `_castFreeExtra` (`Game.CastingCosts.cs`, used in `Game.Priority.cs` and `Game.Effects.Part2.cs`) |
| Alternative cost that exiles a card from hand (`alternativeCost` `"extra"` with `exileFromHand`, with `life`) | `ExileFromHand` on `ExtraCost` (`Abilities/Costs.cs`), added to the costs in `Game.CastingCosts.cs` |
| Counters you put are doubled (`doublesCountersYouPut`) | `CountersYouPutDoublings` (`Game.Counters.cs`) |
| Sacrifice any number as an additional cost (`additionalSacrificeAny`) | `SacrificeAnyCounts` (`Game.CastingCosts.cs`, used in `Game.Priority.cs`); the filter is `CardDefinition.CastSacrificeAny` (`Cards/CardDefinition.cs`) |
| Escape (cast from the graveyard for the escape cost, exiling other cards; enters with counters) | `CastingWayKind.Escape` (`Game.CastingCosts.cs`); `CardDefinition.Escape`, `EscapeExtra` and `EscapesWithCounters` read in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); the counters are placed in `Game.Priority.cs` |
| Counters a permanent spell enters with (impending, X counters, ravenous, escape, "enters with additional counters"; placed as it enters, rule 122.6) | `EnteringCounters` (`Game.Priority.cs`); `_enteringCastX` (`Game.Priority.cs`, `Game.Zones.cs`) sets `Card.CastX` (0 if not cast, `State/Card.cs`) |
| Token creation replacements (twice that many, extra Food, Clue, Food and Treasure, Squirrels, Fish into Shark) | `CreateTokenEventAsync` (`Game.Effects.Helpers.cs`) |
| Auras on players | `Card.AttachedPlayer` (`State/Card.cs`); `_enteringAttachedPlayer` (`Game.Priority.cs`, `Game.Zones.cs`); the state-based check in `Game.StateBasedActions.cs`; the aura is shown on the enchanted player's side by `PlayerArea.SideOf` (`scripts/UI/Board/PlayerArea.cs`), with "Enchanting <player>" in `scripts/UI/Board/CardStatusPanel.cs` |
| Auras that move to another opponent (`attachToRandomOtherOpponent`) and the trigger `enchantedPlayerCastsSpell` | `AttachToRandomOtherOpponent` (`Abilities/Effects.cs`) runs in `Game.Effects.Part3.cs`: it picks another opponent of the controller at random and sets `Card.AttachedPlayer`, and announces it with `ChoiceMade`; `enchantedPlayerCastsSpell` is matched for spells in `Game.Triggers.cs` (the observer's `AttachedPlayer` is the caster) |
| Voting: each voter's permanent taken for the spell's controller (`gainVotersPermanents`) | `GainVotersPermanents` (`Abilities/Effects.cs`), run by `GainVotersPermanentsAsync` (`Game.Voting.cs`), which adds a `ControlEffect` for the chosen permanent; dispatched in `Game.Effects.Part4.cs` |
| Activated abilities from the command zone | legal actions in `Game.Priority.cs` (`FromCommandZone`); `FromCommandZone` on `AbilityDefinition` (`Abilities/AbilityDefinition.cs`) |
| Gained offspring (a creature spell gets offspring from a permanent's `creatureSpellsGainOffspring`) | `CastingChoices.GrantedOffspringPaid` (`Game.CastingCosts.cs`) and `GrantedOffspringPaid` (`State/StackItem.cs`); `GrantedOffspringTrigger` (`Game.Priority.cs`) |
| Every creature type (layer 4) | `RecomputeEveryCreatureType` (`Game.Effects.Helpers.cs`); `Card.EveryCreatureType` (`State/Card.cs`) |
| Next-combat attack rules (attacks if able, can't attack a player) | `GameState.NextCombatRules` (`State/GameState.cs`), checked in `Game.Combat.cs` |
| Delayed "at end of combat" triggers | `GameState.AtEndOfCombat` (`State/GameState.cs`), used in `Game.Combat.cs` |
| Damage log (each entry keeps `SourceController`) | `DamageLog` (`State/GameState.cs`), written in `Game.TriggerTracking.cs` |
| Hideaway (the rest go to the bottom in a random order, rule 702.75a; choice among several hidden cards) | `Hideaway` in `Game.Effects.Part4.cs`; `Hideaway` (`Abilities/Effects.cs`) |
| Draw limit ("can't draw more than one card each turn", `opponentsDrawAtMostOne`) | `DrawOneAsync` (`Game.Zones.cs`) |
| Blight (put N -1/-1 counters on a creature you control, as a cost or an effect) | `ExtraCost.Blight` and `ExtraCost.BlightX` (`Abilities/Costs.cs`); the `blight:N` cost token and `"blight"` in extra-cost objects (`Arcanum.Data/Scripts/CardScript.cs`); paid in `PayExtraAsync` (`Game.Priority.cs`); `Blight` effect (`Abilities/Effects.cs`), applied by `ApplyBlightAsync` and `Blight` (`Game.Blight.cs`); `BlightCandidates` (`Game.Blight.cs`) decides whether a cost can be paid; event `Blighted` (`Events/GameEvent.cs`); the board in `scripts/UI/Board/EventLogFormatter.cs` and `scripts/UI/Board/GameBoard.cs` |
| Optional additional cost ("you may [cost]" asked as the spell is cast; `whenAdditionalCostPaid`) | `CardDefinition.OptionalAdditionalCost` (`Cards/CardDefinition.cs`); `CastingChoices.AdditionalCostPaid` (`Game.CastingCosts.cs`), asked in `Game.Priority.cs`; `Card.AdditionalCostPaid` (`State/Card.cs`), copied with the spell in `Game.Effects.Helpers.cs`; condition `AdditionalCostWasPaid` (`Abilities/Conditions.cs`, checked in `Game.Conditions.cs`); `AbilityDefinition.WhenAdditionalCostPaid` (`Abilities/AbilityDefinition.cs`) |
| Maximum X (`maxX`) | `CardDefinition.MaxX` (`Cards/CardDefinition.cs`); the X choice in `Game.Priority.cs` |
| Spree (rule 702.172: modes with their own costs, one at a time while the total can still be paid, at least one) | the modes loop in the casting flow (`Game.Priority.cs`, a `ModeRequest` for each pick); `Mode.Cost` on `Mode` (`Abilities/AbilityDefinition.cs`); `Spree` in `ScriptedKeywords` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Wither (damage as -1/-1 counters) | `Keyword.Wither` (`Cards/Keyword.cs`); `ApplyDamage` (`Game.Damage.cs`) |
| Infect (damage to creatures as -1/-1 counters, to players as poison counters) | `Keyword.Infect` (`Cards/Keyword.cs`); `ApplyDamage` (`Game.Damage.cs`), with the wither check and the poison step |
| Evoke (cast for the evoke cost; the sacrifice trigger; the exile-a-card cost) | `EvokeLine`, `EvokeExileLine` and `EvokeTrigger` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `CardDefinition.Evoke` and `EvokeExtra` (`Cards/CardDefinition.cs`); `CastingWayKind.Evoke` (`Game.CastingCosts.cs`); `Card.Evoked` (`State/Card.cs`), set from `_enteringEvoked` (set in `Game.Priority.cs`, read in `MoveCardAsync`, `Game.Zones.cs`); `ExtraCost.ExileFromHand` (`Abilities/Costs.cs`) and `ExilableFromHand` (`Game.Evoke.cs`); condition `WasEvoked` (`Abilities/Conditions.cs`) |
| Encore (a token copy for each opponent that attacks them, sacrificed at the next end step) | `EncoreAbility` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `Encore` effect (`Abilities/Effects.cs`), run by `EncoreAsync` (`Game.Encore.cs`); `State.AttackPlayerRequirements` (`State/GameState.cs`) |
| Endure (the controller's choice of +1/+1 counters or an N/N white Spirit token; the token goes to the ability's controller when the permanent has left) | `Endure` effect (`Abilities/Effects.cs`), run by `EndureAsync` and the token `EndureSpirit` (`Game.Endure.cs`); `"endure"` in the card script (`Arcanum.Data/Scripts/CardScript.cs`); `Endure` in `ScriptedKeywords` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Mobilize (N 1/1 red Warrior tokens tapped and attacking, sacrificed at the next end step) | `MobilizeLine` and `MobilizeTrigger` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `Mobilize` effect (`Abilities/Effects.cs`), run by `MobilizeAsync` and the token `MobilizeWarrior` (`Game.Endure.cs`); the sacrifice in `State.AtNextEndStepEffects` (`State/GameState.cs`) |
| Harmonize (cast from the graveyard for its harmonize cost, a creature tapped for less, exiled when it leaves the stack) | `HarmonizeLine` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `CardDefinition.Harmonize` (`Cards/CardDefinition.cs`); `CastingWayKind.Harmonize`, `CastingChoices.HarmonizeTapped` and `HarmonizeTaps` (`Game.CastingCosts.cs`); `ChooseHarmonizeTapAsync` (`Game.Harmonize.cs`), called in the cast flow (`Game.Priority.cs`), which also sets `SpellOnStack.Flashback`; graveyard casts listed in `GetLegalActions` (`Game.Priority.cs`) |
| Compleated (fewer loyalty counters for Phyrexian symbols paid with life) | `ManaPaid.PhyrexianPaidWithLife` (`Game.Priority.cs`); `SpellOnStack.CompleatedFewer` (`State/StackItem.cs`), set in `Game.Priority.cs`; `_enteringFewerLoyalty` (`Game.Zones.cs`) lowers the loyalty counters as it enters |
| Clash (reveal, top or bottom, then the `ifWin` or `else` effects) | `Clash` effect (`Abilities/Effects.cs`), run by `ClashAsync` (`Game.Clash.cs`); event `Clashed` (`Events/GameEvent.cs`); the top-or-bottom choice uses `CardChoicePurpose.ScryToBottom`; `RepeatProcess` (`Abilities/Effects.cs`) repeats `Resolving` in `Game.Effects.Part4.cs` |
| Manifest (face-down 2/2 from the top of a library; turned face up for the mana cost) | `Manifest` effect (`Abilities/Effects.cs`); `ManifestTopAsync` and `ManifestTurnUpCost` (`Game.Morph.cs`); `TurnFaceUp.ForManaCost` (`Players/PlayerAction.cs`); `Card.Manifested` (`State/Card.cs`); `_enteringFaceDown` (`Game.Zones.cs`) makes it face down as it enters; event `Manifested` (`Events/GameEvent.cs`) |
| Manifest dread (rule 701.62a: look at the top two cards, manifest one, put the other into the graveyard) | `ManifestDread` effect (`Abilities/Effects.cs`), run by `ManifestDreadAsync` (`Game.Morph.cs`), which manifests with `ManifestAsync`; event `ManifestedDread` (`Events/GameEvent.cs`); trigger `YouManifestDread` (queued in `Game.Triggers.cs`). `ManifestFromHand` (`Abilities/Effects.cs`) is dispatched in `Game.Effects.Part1.cs` |
| Companion (revealed before the game, put into the hand for {3} once per game) | `CompanionCondition` and `CompanionConditions.FulfilledBy` (`Cards/CompanionCondition.cs`); `CardDefinition.Companion` (`Cards/CardDefinition.cs`); `RevealCompanionsAsync` (`Game.Companion.cs`), called from `RunAsync` in `Game.cs`; `CompanionActions` (`Game.Companion.cs`, listed in `Game.Priority.cs`) and `PutCompanionIntoHand` (`Players/PlayerAction.cs`); `Player.Companion` and `Player.CompanionTaken` (`State/Player.cs`); events `CompanionRevealed` and `CompanionPutIntoHand`; deck check in `DeckValidator` (`Arcanum.Data/Formats/FormatRules.cs`); `DeckList.ResolveCompanions` (`Arcanum.Data/Decks/DeckList.cs`) |
| Cascade count (rules text "Cascade, cascade" is two; a script's `"cascade"` overrides it) | `CardFactory.KeywordInstances` (`Arcanum.Data/CardData/CardFactory.cs`); `Cascade` in `CardScript` (`Arcanum.Data/Scripts/CardScript.cs`) |
| Granted evoke (`spellsFromHandGainEvoke`: a permanent spell cast from hand gets the evoke cost and its sacrifice trigger) | `GrantedEvokeCosts` (`Game.CastingCosts.cs`, `Game.Evoke.cs`); `GrantedEvokeSacrifice` (`Game.Evoke.cs`, `Game.Triggers.cs`); `SpellsFromHandGainEvoke` and `GrantedEvokeCost` (`Cards/CardDefinition.cs`) |
| Spells that have convoke or conspire (`spellsHaveConvoke`, `spellsHaveConspire`) | `SpellsHaveConvoke`, `SpellsHaveConspire` (`Cards/CardDefinition.cs`); checked in `Game.CastingCosts.cs` |
| Casting the cards exiled with a permanent (`castExiledWithThis`, `castExiledWithThisIf`; the extra cost) | `StaticExiledWithPermission` (`Game.Priority.cs`); `CastExiledWithThis`, `CastExiledWithThisYourTurn`, `CastExiledWithThisExtra` (`Cards/CardDefinition.cs`); the extra cost in `NonManaCastingCosts` (`Game.CastingCosts.cs`) |
| Exile with a counter instead of the graveyard as it resolves (`exileWithCounterWhenResolves`) | `ExileWithCounterWhenResolves` (`Abilities/Effects.cs`); `State.ExileWithCounterOnResolution` (`State/GameState.cs`), set in `Game.Effects.Part4.cs`, read in `Game.Priority.cs` |
| Behold with a chosen creature type (`beholdChooseType`, `beholdCount`, `beholdExile`) | `BeholdTypeOptions` and `ChooseBeholdTypeAsync` (`Game.Priority.cs`); `_beholdType` (`Game.Priority.cs`) keeps the choice; the spell's `ChosenType` is set when it's cast |
| Extra triggers when a creature dies (`yourDyingTriggersExtra`) | `ExtraTriggersFromDying` and `YourDyingTriggersExtra` (`Game.Heroes.cs`), added to the trigger count in `Game.Triggers.cs`; a permanent that left the battlefield in the same event counts through `_lookBack` |
| Extra triggers when permanents enter (`replaces` `ExtraTriggersFromPermanentsEntering`) | `Replacements.ExtraTriggersFromPermanentsEntering` (`Cards/CardDefinition.cs`); `ExtraTriggersFromLandsAndBirds` (`Game.Heroes.cs`), used in `Game.Triggers.cs` |
| Tokens that copy the equipped creature (`firstTokensCopyEquipped`) | `FirstTokensCopyEquipped` (`Cards/CardDefinition.cs`); `CreateTokenEventAsync` (`Game.Effects.Helpers.cs`) |
| Activated abilities only opponents may activate (`onlyOpponents`), or any player may (`anyPlayer`) | `OnlyOpponents` and `AnyPlayer` (`Abilities/AbilityDefinition.cs`); the legal actions in `Game.Priority.cs` |
| Mana that can't pay generic costs (`cantPayGeneric`) | `ManaOption.CantPayGeneric` (`Cards/ManaOption.cs`); `ManaUnit.CantPayGeneric` (`Mana/ManaPool.cs`); `ApplyPool`, `Apply` and `ApplyTaps` (`Rules/ManaPayment.cs`); `ManaSourceOption.CantPayGeneric` (`Players/ManaPaymentRequest.cs`) for the payment screen |
| Land mana bonus (`landManaBonus`) | `LandManaBonus` (`Cards/CardDefinition.cs`, `Cards/CompanionCondition.cs`); `AddLandManaBonusAsync` (`Game.Evoke.cs`), called from `Game.Priority.cs` |
| Spells you control can't be countered, damage doubled by a chosen type, wither for all damage, no draws (`yourSpellsCantBeCountered`, `doublesDamageOfChosenType`, `allDamageWither`, `playersCantDraw`) | `YourSpellsCantBeCountered` checked in `Game.Effects.Helpers.cs`; `DoublesDamageOfChosenType` and `AllDamageWither` in `Game.Damage.cs`; `PlayersCantDraw` in `Game.Zones.cs` |
| Counter all of an opponent's spells and abilities, sacrifice by a shared creature type, reveal and exile from hand or top (`counterAllOfOpponents`, `sacrificeNotSharingTypeWithChosen`, `revealFromHandYouChooseExile`, `revealNonlandExileOnePerColor`) | Effects in `Abilities/Effects.cs`, run in `Game.Effects.Part3.cs` |
| Commander mana rider (`commanderEntersWithCastCountCounters`, `commanderIdentity`) | `ManaRider` (`Abilities/Costs.cs`); counters counted in `Game.Priority.cs` from `Player.CommanderCasts` |
| Persist and undying (return once with a -1/-1 or +1/+1 counter) | `QueuePersistAndUndying` (`Game.Persist.cs`), called from `Game.Triggers.cs` |
| Keywords `CantBecomeUntapped`, `CantHaveCounters`, `HexproofFromItsColors` | `Game.Permanents.cs` (untap), `Game.Blight.cs` and `Game.Counters.cs` (counters), `Game.Targeting.cs` (hexproof from the colors) |
| Counter kinds `dream`, `tower` and `pressure` | `CounterKind` (`Abilities/Effects.cs`) |
| Champion (`champion`, the `championed` trigger, `returnLinkedExiled`) | `Champion` and `ReturnLinkedExiled` (`Abilities/Effects.cs`); the `Champion` case in `Game.Effects.Part3.cs` links the exiled card (`ExiledWith`) and queues `TriggerEvent.Championed` (`Abilities/AbilityDefinition.cs`); `Champion` is in `ScriptedKeywords` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Prevent combat damage to your attackers (`preventCombatDamageToYourAttackers`) | `PreventsAll` (`Game.Damage.cs`), with `PreventCombatDamageToYourAttackers` (`Cards/CardDefinition.cs`) |
| Your creatures' abilities as though they had haste (`yourCreaturesActivateAsThoughHaste`) | `ActivatesAsThoughHaste` (`Game.Priority.cs`) for {T} abilities; `AvailableSources` (`Rules/ManaPayment.cs`) for mana abilities |
| Every card has the chosen color (`everythingIsChosenColor`, `chooseOnEnter: "color"`) | `Card.AddedColors` (`State/Card.cs`), set in `RecomputeContinuousEffects` (`Game.Layers.cs`) for every card in every zone |
| Bot answers no to a repeated "you may" (rule 732.2a: a loop only the player can stop) | `EndsALoop` and `LoopLimit` (`Arcanum.Bots/BotController.cs`), used by `ChooseYesNoAsync`: after 40 yes answers to the same prompt from the same source in one turn, it says no |
| Board announcements for sacrificed permanents, countered spells and abilities, cards revealed, choices made and control changes | `Announce` (`scripts/UI/Board/GameBoard.cs`) |
| Combat keywords (decayed, flanking, battle cry, mentor, myriad; the attack and block triggers) | `Game.CombatKeywords.cs` (triggers queued on attack and block); `Keyword.Decayed`, `Flanking`, `BattleCry`, `Mentor`, `Myriad` (`Cards/Keyword.cs`); the block check in `Game.Combat.cs` reads `Keyword.Decayed` |
| Riot (a +1/+1 counter or haste, asked as the creature enters) | `Keyword.Riot` (`Cards/Keyword.cs`); the question in `Game.Priority.cs`, per instance of the keyword |
| Hexproof from monocolored | `Keyword.HexproofFromMonocolored` (`Cards/Keyword.cs`); the targeting check in `Game.Targeting.cs` |
| Extort (rule 702.101a: each spell its controller casts may drain each opponent for {W/B}) | `Keyword.Extort` (`Cards/Keyword.cs`); `ExtortTrigger` (`Game.Triggers.cs`), queued for each spell cast beside prowess |
| Committing a crime (rule 700.13: targeting an opponent, their permanent or spell, or a card in their graveyard; trigger `youCommitCrime`) | `QueueCrime` (`Game.Targeting.cs`), called from `PushStack` for every spell and ability put on the stack; `TriggerEvent.YouCommitCrime` (`Abilities/AbilityDefinition.cs`) |
| Demonstrate and suspend from the hand (the cast-copy trigger; the exile with time counters) | `DemonstrateAsync` and `SuspendFromHand` (`Game.CombatKeywords.cs`); `SuspendFromHand` and `Demonstrate` effects (`Abilities/Effects.cs`) are dispatched in `Game.Effects.Part1.cs`; `SuspendLine` and the demonstrate line in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Fabricate (counters or Servo tokens as it enters) | `FabricateAsync` (`Game.Endure.cs`); `Fabricate` effect (`Abilities/Effects.cs`); `FabricateLine` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Eternalize and embalm (a token copy from the graveyard, no mana cost, Zombie) | `ZombieCopyAbility` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); the copy's colors and size set by its `SetColors`, `SetPower` and `SetToughness` |
| Dredge (mill N instead of drawing, return the card from the graveyard) | `CardDefinition.Dredge` (`Cards/CardDefinition.cs`); the draw path in `Game.Zones.cs`, which asks before the draw and mills with `Abilities.Mill` |
| Omen (an adventure's spell half shuffled into the library as it resolves) | the Omen check on resolution in `Game.Priority.cs`; `Omen` subtype on the adventure (`State/Card.cs` treats it as a spell type) |
| Void counters (opponents' cards exiled with a void counter instead of the graveyard) | `ZoneReplacementKind.ExiledWithVoidCounter` (`Game.Zones.cs`), collected for opponents' cards and applied as the card moves to exile; `CounterKind.Void` (`Abilities/Effects.cs`); `CardDefinition.OpponentsCardsExiledWithVoidCounter` (`Cards/CardDefinition.cs`) |
| Special effects of single cards (free casts from the graveyard, exiling a creature and the graveyard, territorial attacks, counters per color, choosing from the graveyard, and the rest) | `Game.SpecialEffects.cs`, one method per effect, dispatched from `ApplyAsync` (`Game.Effects.cs`, into the parts); the effect records are in `Abilities/Effects.cs` and parsed in `CardScript.cs` (`Arcanum.Data/Scripts/CardScript.cs`); the Angel token for `creatureTokensBecomeAngels` is `AngelToken` in `Game.SpecialEffects.cs` |
| Attack legality for a creature with defender that can attack players who attacked you (`canAttackPlayersWhoAttackedYou`) | `AttackForbidden` with `OnlyPlaneswalkers` (`Players/AttackRequest.cs`), added in `Game.Combat.cs`; the defender check reads `Player.PlayersAttackedDuringLastTurn` |
| Attacks this combat if able, and attacks a chosen player this combat if able | `AttacksThisCombat` effect (`Abilities/Effects.cs`), dispatched in `Game.Effects.Part1.cs`; `State.AttacksThisCombat` and `State.AttackPlayerThisCombat` (`State/GameState.cs`), checked in `Game.Combat.cs`; both record the `CombatSerial` they belong to |
| Combat bookkeeping for "last turn" and "this turn" (`CombatSerial`, the latest and previous combat, the player attacked last) | `State.CombatSerial` (`State/GameState.cs`) numbers each combat; `Player.LatestCombat` and `Player.PreviousCombat` (`State/Player.cs`), set in `Game.Turn.cs`; `Card.LastPlayerAttacked` (`State/Card.cs`), set in `Game.Combat.cs`; `Player.PlayersAttackedDuringLastTurn` (`State/Player.cs`), filled from the previous turn in `Game.Turn.cs`; `Player.AttackedWithCommanderThisTurn` (`State/Player.cs`), set in `Game.Combat.cs` and reset in `Game.Turn.cs` |
| Any-color mana for a cost (`anyColorMana`: spend mana as any color; {C} still needs colorless) | `AnyColorOnly` and `AnyManaType` on the exile-play permission (`Abilities/Effects.cs`, `Game.Effects.Part1.cs`); the check that the card can be paid for in `Game.CastingCosts.cs` |
| Granted mana abilities (a permanent gives the mana and activated abilities of the cards exiled with it, `exiledCardAbilities`) | `Card.PrintedManaOptions()` and `Card.GrantedManaOptions` (`State/Card.cs`), filled in `RecomputeContinuousEffects` (`Game.Layers.cs`) from `StaticAbility.GrantsExiledCardAbilities` (`Abilities/StaticAbility.cs`) |
| Experience counters (kept for the game, shown on the player's side of the board) | `Player.Experience` (`State/Player.cs`); `GetExperienceCounter` in `Game.Effects.Part1.cs`; `GameView.Experience` (`Views/GameView.cs`), set in `ViewBuilder` (`Views/ViewBuilder.cs`) and shown as a tag by `PlayerArea` (`scripts/UI/Board/PlayerArea.cs`) |
| Modes "you may choose two" (exactly that many or none) | `MayChooseModes` and `ModeCount` (`Abilities/AbilityDefinition.cs`), with the mode list in `Abilities/Modes.cs` |
| Targets that are different from each other from a slot on (`distinctFrom`), targets of a repeated group (`countOf`, `upTo`), a target's owner (`ownedByTarget`) | `TargetRule.AllDifferent` with `ModeRule` and `ModeStart` (`Abilities/Targets.cs`), checked by `RuleHolds` (`Game.Targeting.cs`) |
| Predefined Gold token (sacrifice for one mana of any color) | `PredefinedTokens.Gold` and `PredefinedTokens.ByName` (`Cards/PredefinedTokens.cs`) |
| Rooms (rule 709.5: a split card whose two halves are both permanents; each door unlocks as a special action at sorcery speed) | `CardDefinition.IsRoom` and `AsRoom(unlocked)` (`Cards/CardDefinition.Rooms.cs`) combine the unlocked doors' name, mana cost and rules text, with the card's own types and printing; `Card.UnlockedDoors` and `Card.IsUnlocked` (`State/Card.cs`) pick that definition on the battlefield in `Card.Definition`. The special action `UnlockDoor` (`Players/PlayerAction.cs`) is listed by `UnlockActions` (`Game.Rooms.cs`, from `Game.Priority.cs`) and paid without the stack in `UnlockDoorAsync` (`Game.Rooms.cs`). `Unlock`, `Lock`, `UnlockCost` (the door's cost less `UnlockCostsLess`) and the effect `UnlockADoor` (`Game.Rooms.cs`, dispatched in `Game.Effects.Part1.cs`). Mana for doors: `ManaOption.ForUnlock` (`Cards/ManaOption.cs`), `ForUnlock` on `AddMana` (`Abilities/Effects.cs`) and on `ManaUnit` (`Mana/ManaPool.cs`). Events `DoorUnlocked` and `DoorLocked` (`Events/GameEvent.cs`) come from `Unlock` and `Lock`, and from `MoveCardAsync` (`Game.Zones.cs`) for the door a Room spell was cast as. Triggers `YouUnlockThisDoor` and `YouFullyUnlockRoom` are queued on `DoorUnlocked` (`Game.Triggers.cs`). `MiracleCost` (`Game.Rooms.cs`) and `UnlockedDoorsOf` (the quantities) are also here. Board: the announcement and the unlock button in `scripts/UI/Board/GameBoard.cs`, the log line in `scripts/UI/Board/EventLogFormatter.cs`, the door states in `scripts/UI/Board/CardStatusPanel.cs`; `ViewBuilder` (`Views/ViewBuilder.cs`) sets `DoorsUnlocked` on `CardView` and names a fully locked Room "Room (both doors locked)" |
| Card-wide rule flags (`"rules": [...]`, rules text that is a flag on the card, such as "You don't lose the game for having 0 or less life") | `ExtraRules` flags enum (`Cards/CardDefinition.ExtraRules.cs`; each flag quotes its rules text in a doc comment); `CardDefinition.ExtraRules`, read from the root `"rules"` in `CardScript.cs` and merged in its `Merge`. `Game.HasRule(player, rule)` (`Game.Rooms.cs`) is true when a permanent the player controls, with text abilities, has the flag. Most flags are read where their rule applies: `grep -rn "ExtraRules\." src/Arcanum.Engine` lists them |
| A card that is also a creature off the battlefield (`creatureOffBattlefield`) | `CreatureOffBattlefield` record and `CardDefinition.CreatureOffBattlefield` (`Cards/CardDefinition.OffBattlefield.cs`); `AsOffBattlefield()` adds the creature type, its power and toughness; `Card.Definition` uses it in every zone but the battlefield (`State/Card.cs`); `FormatRules` (`Arcanum.Data/Formats/FormatRules.cs`) counts it as a legendary creature for commander eligibility (rule 903.3) |
| Lands that add mana while a creature with `LandsTappedAddOneAndDamageWhileTapped` is tapped | `Card.ExtraWhileWatchersTapped` (`State/Card.cs`) is set for each land in `RecomputeManaTypes` (`Game.Layers.cs`) and added to the land's mana options in `Card.ManaOptions`; `Tap` and `Untap` (`Game.Permanents.cs`) recompute the continuous effects when a watcher changes; the trigger after a land is tapped for mana is in `TapForManaAsync` (`Game.Priority.cs`), which deals the damage |
| Casting ways from card-wide rules, library-top costs and free casts (`CastingWayKind.RuleAlternative`, `FreeFromExile`, `LifeOncePerTurn`) | `Game.CastingCosts.cs`: `RuleAlternative` for `YourSpellsForFiveColors`; `FreeFromExile` for `FreeFromExileOncePerTurn` (`FreeFromExileSource`) and `FreeFromHandOrTopOncePerTurn` (`FreeFromHandOrTopSource`); `LifeOncePerTurn` for `LifeCastFromHandOncePerTurn` and `LifeCastEnchantmentOncePerTurn` (`LifeOncePerTurnSource`); `GrantedEscape` for `GraveyardEnchantmentsHaveEscape`; `LibraryTopCostHolder` for a card's `LibraryTopExtraCost` (`playFromLibraryTopCost`). The choice of such a way and its payment is in `Game.Priority.cs` (`_freeFromExileUsed` marks the once-per-turn uses). `MiracleCost` (`Game.Rooms.cs`) gives `HandEnchantmentsHaveMiracle` |
| Exile permissions in groups (one card with several permissions from one effect) | `PlayableFromExile.Group` and `GroupLabel` (`State/GameState.cs`); `NoteExilePlayAsync` (`Game.Priority.cs`) asks the caster which group's permission the card uses (`ChooseOptionAsync`) when the groups have labels. The groups by card type are made in `Game.Effects.Part1.cs` |
| New trigger events (`TriggerEvent`) | `Abilities/AbilityDefinition.cs` defines them. Queued in `Game.Triggers.cs`: `BecomesTargetOfOpponentSpell` (queued from `Game.Targeting.cs` when a spell targets the permanent), `CreatureDiesOrExiled` (`QueueObservers`, from the death and exile paths), `ThisPutIntoGraveyard` (for the card in the graveyard), `EnchantedPlayerDealtDamage`, `TurnedFaceUp` and `YourPermanentTurnedFaceUp`, `YouManifestDread`, `YouUnlockThisDoor` and `YouFullyUnlockRoom`, `YourCreaturesAttackPlayer`, `AnyPlayerDraws`, `YourSecondMainPhase` (at the second main phase of each of your turns) |
| Academy set effects and quantities (`atYourNextMainPhase`, exile-and-cast effects, piles, delayed combat-damage abilities, coin flips) | `ParseAcademyEffect` and `ParseAcademyQuantity` (`Arcanum.Data/Scripts/CardScript.Academy.cs`); records `AcademyEffect` (`Abilities/Effects.Academy.cs`); `EvalAcademy` and `ApplyAcademyAsync` (`Game.Academy.cs`); the casts without paying in `CastAnyNumberFreeAsync` (`Game.Academy.cs`) |
| Academy turn effects (free casts while resolving, can't lose, damage can't reduce life below 1, spells can't be countered, hexproof from colors, next-cast trigger, draw and pay) | the cases in `ApplyAcademyAsync` (`Game.Academy.cs`); `CantLoseGameTurn`, `DamageCantReduceLifeBelowOneTurn`, `SpellsCantBeCounteredTurn`, `HexproofFromColorsThisTurn` and `DrawnThisTurn` (`State/Player.cs`); the life floor in `LifeLostToDamage` (`Game.Combat.cs`); `CanBeCountered` (`Game.Effects.Helpers.cs`); the hexproof check in `LegalTargets` (`Game.Targeting.cs`); `WhenNextCast` (`State/GameState.cs`) |
| Opponent cast a matching spell this turn (`opponentCastSpellThisTurn`) | `OpponentCastSpellThisTurn` (`Abilities/Conditions.cs`), checked in `Game.Conditions.cs` |
| Hit stays exiled until end of turn (`exileUntilLesserCastFree` with `"untilEndOfTurn"`) | `ExileUntilLesserCastFree` (`Abilities/Effects.Heroes.cs`), run by `ExileUntilLesserCastFreeAsync` (`Game.Heroes.cs`) |
| Delayed abilities for the next main phase and for combat damage (`AtYourNextMainPhase`, `WhenDealsCombatDamageThisCombat`, `WhenYourCreatureDealsCombatDamageThisTurn`) | Lists in `State/GameState.cs`; queued in `ApplyAcademyAsync` and `TriggerWhenDealsCombatDamage` (`Game.Academy.cs`, called from `Game.Triggers.cs` on combat damage); `ReleaseAtYourNextMainPhase` (`Game.Academy.cs`) runs at the precombat and postcombat main phases in `Game.Triggers.cs` |
| Bestow (cast as an Aura for the bestow cost; a creature again when it isn't attached legally) | `CardDefinition.Bestow` and `AsBestowed()` (`Cards/CardDefinition.Bestow.cs`); `BestowLine` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `CastingWayKind.Bestow` (`Game.CastingCosts.cs`); `Card.Bestowed` (`State/Card.cs`), set in `Game.Priority.cs` and kept in `Game.Zones.cs`; the unattach in `Game.StateBasedActions.cs` |
| Casualty and gravestorm (the sacrifice question, copies on cast, `spellsHaveCasualty` from permanents) | `CasualtyInstances` (`Game.CastingCosts.cs`); the question and the paid instances in `Game.Priority.cs`; `GravestormTrigger` and `CasualtyTrigger` (`Game.Priority.cs`), queued on cast; `Casualty` and `Gravestorm` (`Cards/CardDefinition.cs`), read by `CardFactory`; the count `State.PermanentsPutIntoGraveyardThisTurn` (`State/GameState.cs`, reset in `Game.Turn.cs`) |
| Paradigm (exiled on resolution; the delayed copy at each precombat main phase) | `ParadigmTrigger` and `State.Paradigms`, `State.ResolvedSpellNames` (`State/GameState.cs`); added on resolution in `Game.Priority.cs`; `ParadigmAbility` (`Game.Triggers.cs`) and `CastParadigmCopy` (`Game.Effects.Part3.cs`); `Paradigm` (`Cards/CardDefinition.cs`) |
| Echo and increment (the upkeep question; the cast trigger) | `EchoDue` and `IncrementHolds` (`Abilities/Conditions.cs`, checked in `Game.Conditions.cs`); `Card.ControlStamp` (`State/Card.cs`, set in `Game.cs`) and `Player.UpkeepStamp` / `PreviousUpkeepStamp` (`State/Player.cs`); `EchoLine` and `IncrementTrigger` in `CardFactory` |
| Devoid (colorless in every zone) | `CardDefinition.Devoid` and `ColorList` (`Cards/CardDefinition.cs`) |
| Academy card-wide rules (graveyard alternative cost, attack tax per counter, +1/+1 counter redirection, mana only for {X}, colored strive, protection from types and from the chosen color, no attacks on you) | `GraveyardAlternativeCost` (`Game.CastingCosts.cs`); `AttackTaxPerCounter` (`Game.Combat.cs`); `PlusOneCountersPlusOneOn` (`Game.Counters.cs`); `XCostsOnly` on `ManaOption` (`Cards/ManaOption.cs`, used in `Rules/ManaPayment.cs`); `ExtraTargetCostMana` (`Game.CastingCosts.cs`); `ProtectionFromTypes`, `ProtectionFromChosenColor`, `CantAttackYouOrPlaneswalkers` (`Abilities/StaticAbility.cs`, applied in `Game.Layers.cs`) |
| Card-wide rules `EffectDiscardsMayGoOnTop` and `YouCantCastPermanentSpells` | `EffectDiscardsMayGoOnTop` in `DiscardCardAsync` (`Game.Effects.Helpers.cs`, `byEffect`); `YouCantCastPermanentSpells` in `NoncreatureForbidden` (`Game.CastingCosts.cs`), also checked in `Game.Priority.cs` |
| Attack taxes for each attacker (`AttackerTaxes`, `AttackTaxBudget`) | `AttackerTaxView` and `AttackerTaxesFor` (`Game.Combat.cs`); set on the view in `Game.cs`; `GameView` (`Views/GameView.cs`); the bot's payment in `BotController` (`Arcanum.Bots/BotController.cs`); the board's costs line in `scripts/UI/Board/GameBoard.cs` |
| Auras that return the creature they enchant from a graveyard (`animateEnchantedCard`, `sacrificeAnimated`) | `Card.Animated` (`State/Card.cs`), set by `AnimateEnchantedCard` (`Game.Academy.cs`); the Aura's legality, including an Aura on a graveyard card (`enchant` of kind `GraveyardCard`), in `Game.StateBasedActions.cs` |
| Auras attached as a creature died (`yourAurasOnTriggeredAtDeath`) | `State.AurasAtDeath` (`State/GameState.cs`), filled as the creature dies in `Game.Triggers.cs` |
| Aura color protection that doesn't remove the Aura (`protectionFromChosenColor`) | `Card.AuraColorProtections` (`State/Card.cs`); checked in `Game.Targeting.cs` |
| Token copies of exiled cards (`returnCopiedCardToGraveyard`) | `Card.CopiedFromCard` (`State/Card.cs`), set in `Game.Effects.Part1.cs`; `ReturnCopiedCardToGraveyard` (`Game.Academy.cs`) |
| Spells that go to the bottom of the library instead of the graveyard | `ZoneReplacementKind.BottomInsteadOfGraveyard` (`Game.Zones.cs`); `State.BottomInsteadOfGraveyard` (`State/GameState.cs`), set in `Game.Priority.cs`; `PlayableFromExile.BottomInsteadOfGraveyard` (`State/GameState.cs`), set in `Game.Academy.cs` |
| Activated abilities of emblems (offered at priority from the command zone, e.g. an until-end-of-turn "pay 1 life: add {C}") | the emblem loop in the legal actions in `Game.Priority.cs` |
| Per-turn bookkeeping for the academy set (`SpellManaValuesThisTurn`, `ModifiedCreatureDiedThisTurn`, `CardsPutIntoExileThisTurn`) | `Player.SpellManaValuesThisTurn` and `Player.ModifiedCreatureDiedThisTurn` (`State/Player.cs`), `State.CardsPutIntoExileThisTurn` (`State/GameState.cs`); reset in `Game.Turn.cs` |
| Warp (rule 702.185a: cast from hand for the warp cost, exiled at the next end step, castable from exile on a later turn) | `CastingWayKind.Warp` and its offer from the hand only (`Game.CastingCosts.cs`); `SpellOnStack.Warped` (`State/StackItem.cs`), set in `Game.Priority.cs` beside blitz, which queues the `WarpExile` end-step effect; `WarpExile` (`Abilities/Effects.Academy.cs`), run in `Game.Academy.cs`, adds a `PlayableFromExile` with `CastOnly` and `LaterTurnOnly` (`State/GameState.cs`); `PlayableExile` (`Game.Priority.cs`) leaves it out on the turn it was made |
| Case (layout `case`, rule 719.3: the "To solve" trigger makes it solved, then its "Solved —" abilities work) | `Card.Solved` (`State/Card.cs`), cleared by `ResetStatus` (`Game.Zones.cs` calls it on a zone change); `SolveCase` (`Abilities/Effects.Academy.cs`), run in `Game.Academy.cs` and announced with `ChoiceMade`; condition `SourceSolved` (`Abilities/Conditions.cs`, the `"solved"` key in `CardScript.cs`); `"Solved"` in `ScriptedKeywords` and `"case"` in `SingleFaceLayouts` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Exile a card from a hand until the source leaves (`exileFromHandUntilLeaves`; the card returns to its owner's hand, rule 610.3c) | `ExileFromHandUntilLeaves` (`Abilities/Effects.Academy.cs`), run in `Game.Academy.cs`; the link is a `LinkedExile` with `ToHand` (`State/GameState.cs`); `_linkedReturnToHand` (`Game.Zones.cs`) sends the card to its owner's hand when the source leaves |
| Subject `eachTarget` with a filter (the targets that match it) | `SubjectKind.EachTarget` with a filter (`Subject` in `Abilities/Targets.cs`; parsed in `CardScript.cs`), resolved in `Game.Effects.cs` with the other subjects |
| X of an enters-the-battlefield trigger (`countOf` and `upTo` targets) | `Card.CastX` (`State/Card.cs`), set in `Game.Zones.cs`; the trigger's targets take it through `WithRepeatedTargets`, called in `Game.Triggers.cs` where triggered abilities choose targets |
| Cards in your graveyard gain "cast from your graveyard this turn" (`castFromGraveyardThisTurnEach`, the cards there at resolution) | `PlayableFromGraveyardThisTurn.EachInYourGraveyard` (`Abilities/Effects.cs`), granted in `Game.Effects.Part2.cs` beside the `GrantFlashback` case |
| Controlling another player (rule 722: `controlPlayerNextTurn`) | `State.PendingPlayerControl` (added in `Game.Effects.Part4.cs`) and `State.ActivePlayerControlledBy` (`State/GameState.cs`). `RunTurnAsync` (`Game.Turn.cs`) takes the latest pending control when the controlled player's turn starts, so a skipped turn doesn't use it (the turn loop in `Game.cs` never runs it). |
| Decisions of a controlled player (rule 722.4) | `Game.DeciderFor` (`Game.cs`) gives `ControllerOf` the controller; the controller is asked with `ViewFor(player)`, the controlled player's view. Bots answer through `BotController.For(view)` (`Arcanum.Bots/BotController.cs`). Online, `GameClient` (`Arcanum.Net/Client/GameClient.cs`) answers a `Question` from `GameView.ControlledView` when its `For` is the controlled player, and `NetSession.ViewFor` (`scripts/Client/NetSession.cs`) gives the client that view. |
| Hidden information of a controlled player (rule 722.3) | `ViewBuilder.SeenBy` (`Views/ViewBuilder.cs`) lets the viewer see what the player they control sees. `GameView.ActivePlayerControlledBy` and `GameView.ControlledView` (`Views/GameView.cs`) carry the control to the client. |
| Control announcements | `PlayerControlled` event (`Events/GameEvent.cs`), announced in `GameBoard` (`scripts/UI/Board/GameBoard.cs`), logged in `EventLogFormatter` (`scripts/UI/Board/EventLogFormatter.cs`), and shown as the "Controlled by" tag in `PlayerArea` (`scripts/UI/Board/PlayerArea.cs`) |
| Station (rule 702.184a: tap creatures for charge counters) | the station ability is added by `WithStation` (`Arcanum.Data/CardData/CardFactory.cs`); `StationValue` (`Game.Quantities.cs`) counts the tapped creature's power, or its toughness with `stationsWithToughness` |
| Modular N (rule 702.43a) | `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`) adds the dies trigger and sets `Modular`; `Game.Zones.cs` puts the counters on it as it enters |
| One noncreature spell per turn (`EachPlayerOneNoncreatureSpellPerTurn`) | `Player.NoncreatureSpellsCastThisTurn` (`State/Player.cs`), counted in `Game.Priority.cs` and reset in `Game.Turn.cs`; checked in `NoncreatureForbidden` (`Game.CastingCosts.cs`) |
| Casting from the top of the library (the permanent's `LibraryTopCastFilter`) | `LibraryTopPermissions` (`Game.Priority.cs`): any nonland card at the top that the filter allows, creature or not; used for the ways to cast in `Game.CastingCosts.cs` |
| Fuse (rule 702.102: cast from hand with both halves as one spell) | `CardDefinition.Fuse.cs` (`Fuse`, `AsFused`, `CanFuse`, `FusedHalves`); the fused cast in `Game.Priority.cs` (`CastSpell` with `Half` = `FusedHalves`, kept in `Card.CastHalf`); the bot casts through `AsFused` (`Arcanum.Bots/BotController.cs`) |
| Mana from artifact sources (`ManaUnit.FromArtifact`) | `AddPoolMana` (`Game.Priority.cs`) puts it in the pool; `Card.ArtifactManaSpent` (`State/Card.cs`) records the count for the spell when it is paid and is kept when the spell becomes a permanent (`Game.Zones.cs`); `countersPerArtifactManaSpent` is counted in `Game.Counters.cs` |
| Nonland cards with no printed mana cost (`CardDefinition.NoManaCost`) | no mana-cost way to cast (`CastingWays` in `Game.CastingCosts.cs`) and no granted escape (rule 118.6) |
| Escape for every nonland card in the graveyard (`GraveyardNonlandCardsHaveEscape`) | `GrantedEscape` (`Game.CastingCosts.cs`) reads the flag from `ExtraRules` (`Cards/CardDefinition.ExtraRules.cs`) |
| Skipping turns (skip your next turn; an opponent's `opponentsSkipExtraTurns`) | `SkipsTurnAsync` (`Game.Turn.cs`), called from the `NextTurn` local function in `Game.cs`; when both apply, the player chooses (rule 616.1) |
| Counters put on players (poison, experience) | `PlayerCountersPut` event (`Events/GameEvent.cs`), emitted wherever those counters are given (`Game.Damage.cs`, `Game.Effects.Part1.cs`, `Game.Effects.Part2.cs`, `Game.Proliferate.cs`); the `countersPlaced` triggers with `orPlayer` are queued from it in `Game.Triggers.cs` |
| Riot granted by permanents (`nontokenCreaturesYouControlHaveRiot`) | `RiotInstances` (`Game.Priority.cs`), used as a creature enters |
| Damage to your other creatures as counters (`preventsDamageToYourOtherCreaturesForCounters`), damage doubled for creatures with counters (`doublesDamageOfYourCreaturesWithCounters`) | the `preventCounters` damage modifier (`preventCountersStatic`) and the doubling modifier, both in `Game.Damage.cs` |
| Draw triggers that trigger again (`drawTriggersTwice`) | `ExtraTriggersFromDrawing` (`Game.Triggers.cs`) |
| Extra Mutagen tokens with token creation (`extraMutagenWithTokens`) | `CreateTokenEventAsync` (`Game.Effects.Helpers.cs`) counts them with the other token replacements |
| Myriad options (`mandatory`, `playerOnly`, `notLegendary`, `sacrificeAtEndOfCombat`) | `Myriad` effect (`Abilities/Effects.cs`), run by `Game.Myriad.cs` |
| Pay life rounds (`payLifeRounds`) | `PayLifeRounds` effect (`Abilities/Effects.cs`), run in `Game.Effects.Part4.cs` |
| Graft (rule 702.58) and moving counters of one kind (`moveCounter`) | `MoveCounters` effect (`Abilities/Effects.cs`); `Graft` is in the scripted keyword list in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`) |
| Filters on counters (`noCounterKind`, `maxManaValueYourCounters`), conditions and quantities (`aPlayerLifeAtMost` with `"halfStarting"`, `triggeredAttacksPlayer`, `opponentsAttackedThisTurn`, `exiledForCostManaValue`) | `Matches` (`Game.Layers.cs`) for the filters; `Game.Conditions.cs` and `Game.Quantities.cs` for the rest |
| Protection from colors outside the commander's color identity (`protectionFromColorsOutsideCommanderIdentity`) | `ProtectionFromColorsOutsideCommanderIdentity` on `StaticAbility` (`Abilities/StaticAbility.cs`), applied in `Game.Layers.cs` |
| Plot (a special action from the hand at sorcery speed; cast from exile on a later turn without paying, at sorcery speed only) | `PlotAbility` and `PlotLine` in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `AbilityCost.Plot` (`Abilities/AbilityDefinition.cs`); `PlotAsync` and `Plotted` (`Game.HeroesCommander.cs`), dispatched in `Game.Priority.cs`; `CastingWayKind.WithoutPaying` (`Game.CastingCosts.cs`), with the sorcery check in `TimingAllows`; `Card.PlottedTurn` (`State/Card.cs`) |
| Mayhem (cast from the graveyard for the mayhem cost on the turn it was discarded) | `CardDefinition.Mayhem` (`Cards/CardDefinition.cs`), read by `MayhemLine` in `CardFactory`; `MayhemCastable` (`Game.HeroesCommander.cs`), `CastingWayKind.ManaCost` (`Game.CastingCosts.cs`) and the graveyard list in `Game.Priority.cs`; `Card.DiscardedTurn` (`State/Card.cs`), set in the discard in `Game.Effects.Helpers.cs` |
| Bargain and teamwork (optional additional costs: sacrifice an artifact, enchantment or token; tap creatures with total power N, not crewing) | `CardFactory` sets `CardDefinition.OptionalAdditionalCost` to `BargainCost` (`ExtraCost.Bargain`) or to an `ExtraCost` with `CrewPower` and `Teamwork` (`Abilities/Costs.cs`); the payment is in `PayExtraAsync` (`Game.Priority.cs`), where a teamwork tap isn't added to `State.CrewedThisTurn`; `DescribeCost` (`Game.Effects.Helpers.cs`) |
| Collect evidence N (exile cards from your graveyard with total mana value N or more, as a cost) | `ExtraCost.CollectEvidence` (`Abilities/Costs.cs`), read from `collectEvidence` in the extra-cost object (`Arcanum.Data/Scripts/CardScript.cs`); the payability check and the choice in `Game.Priority.cs`, with `CardChoiceRequest.MinTotalManaValue` (`Players/CardChoiceRequest.cs`) |
| Crew and teamwork totals (`crewsAsThoughPowerGreater`: `CardDefinition.CrewPowerBonus`, not counted once the creature has lost its abilities) | `CrewingPower` and `TappingPower` (`Game.Priority.cs`), used by the crew and teamwork checks and choices; `CardChoiceRequest.MinTotalPower` (`Players/CardChoiceRequest.cs`); the token key is read in `CardScript.cs` |
| Power-up (`powerUp`: activate only once; cheaper by the permanent's mana cost if it entered this turn) | `ActivatedAbility.PowerUp` (`Abilities/AbilityDefinition.cs`), set with `OnlyOnce` in `CardScript.cs`; the reduction is in the ability cost computation in `Game.Priority.cs`, using `SourceEnteredThisTurn` |
| Earthbend N (a land becomes a 0/0 haste creature with N +1/+1 counters, returned tapped when it dies or is exiled) | `Earthbend` record (`Abilities/Effects.HeroesCommander.cs`), applied in `Game.HeroesCommander.cs` as `Become`, `AddCounters` and a lasting `WhenDiesThisTurn` (`Lasting`, `OrExiled`); the dies-or-exiled watch is checked in `Game.Triggers.cs` |
| Descend (a permanent card put into your graveyard from anywhere this turn; `descended`) | `Player.DescendedThisTurn` (`State/Player.cs`), set in `Game.cs` when a permanent card that isn't a token moves to a graveyard, reset in `Game.Turn.cs`; condition `TurnEvent.YouDescended` (`Abilities/Effects.HeroesCommander.cs`), checked in `Game.HeroesCommander.cs` |
| Copies that keep a name, have set power and toughness, or exile the copied card (`entersAsCopyKeepName`, `entersAsCopySetPT`, `entersAsCopyExileOriginal`) | `CardDefinition` properties (`Cards/CardDefinition.cs`); the copy's values in `Game.Zones.cs`; the reflexive `CopyExileOriginalTrigger` and `_pendingCopyOrigins` (`Game.Zones.cs`), queued when the copy enters |
| Enter with a basic land type and pay life or enter tapped (`enterLife` with `chooseOnEnter: "basicLandType"`) | `Game.Priority.cs`, after the land type choice: `ChooseYesNoAsync` for the life payment, else `EnterTapped` |
| Damage that heals other damage (`damageHealsOtherDamage`) | `CardDefinition.DamageHealsOtherDamage` (`Cards/CardDefinition.cs`), applied in `Game.Damage.cs` before the lethal-damage check |
| Creatures entering don't trigger (`enteringCreaturesDontTrigger`); everything goes to exile instead of a graveyard (`allExiledInsteadOfGraveyard`) | `EnteringCreaturesDontTrigger` in the entering-trigger check in `Game.Triggers.cs`; `AllExiledInsteadOfGraveyard` as the zone replacement `ExileAllInsteadOfGraveyard` (`Game.Zones.cs`) |
| Searches of an opponent's library (`opponentSearchesLibrary`) | `TriggerEvent.OpponentSearchesLibrary` (`Abilities/AbilityDefinition.cs`); `SearchedOwnLibrary` (`Game.Effects.Helpers.cs`), called at every search site (`Game.Effects.Helpers.cs`, `Game.Effects.Part2.cs` to `Part4.cs`, `Game.Heroes.cs`, `Game.Origins.cs`, `Game.SpecialEffects.cs`) |
| Exile a card from an opponent's graveyard, then its namesakes (`exileFromOpponentsGraveyardThenNamesakes`); `upTo` on `nameAndExile` | `NameThenExileFromAllZones.FromOpponentsGraveyardCard` and `MaxCount` (`Abilities/Effects.Origins.cs`), run in `Game.Origins.cs`, which draws for the cards exiled from hand |
| Delayed ability at the next combat this turn (`atNextCombatThisTurn`) | `AtNextCombatThisTurn` (`Abilities/Effects.Origins.cs`), run in `Game.Origins.cs`, which adds a `DelayedTrigger` with its turn to `State.AtNextCombatThisTurn` (`State/GameState.cs`); released at the beginning of combat in `Game.Triggers.cs` if that turn is still the current one |
| Creatures can't block your creatures (`cantBlockYourCreatures`) | `CantBlockYourCreatures` on `StaticAbility` (`Abilities/StaticAbility.cs`); `Card.CantBlockCreaturesOf` (`State/Card.cs`), set in `Game.Layers.cs`; checked in `CanBlock` (`Game.Combat.cs`) |
| Airbend (`exile` with `"airbend": true`: cast the card for {2}) | `ExileIt.Airbend` (`Abilities/Effects.cs`), applied in `Game.Effects.Part2.cs` as a `PlayableFromExile` with `AltCost` (`State/GameState.cs`) set to `AirbendCost`; the cast ways are built in `CastingWays` (`Game.CastingCosts.cs`), and the permissions are read by `ExilePermissions` (`Game.Priority.cs`) |
| Opponents cast only from their hands (`opponentsCastOnlyFromHandUntilYourNextTurn`) | `GameState.CastOnlyFromHand` (`State/GameState.cs`), added by `OpponentsCastOnlyFromHandUntilYourNextTurn` in `Game.Effects.Part3.cs`; checked in `NoncreatureForbidden` (`Game.CastingCosts.cs`); removed in `Game.Turn.cs` when the player whose next turn it is begins |
| Abilities granted to a card in exile (`exileUntil` with `"gains"`; activated abilities with `"fromExile"`) | `ExileUntilGains` (`Abilities/Effects.cs`), run in `Game.Effects.Part3.cs`, which adds to `Card.ExileGrantedAbilities` (`State/Card.cs`); `Card.Abilities` returns them while the card is that version in exile. `ActivatedAbility.FromExile` (`Abilities/AbilityDefinition.cs`) is matched in the legal actions in `Game.Priority.cs`, for the owner's exile |
| Cast triggers that mean "those creatures" (`"batch"` with `"spellTargets"`) | `TriggerInfo.Batch` (`State/StackItem.cs`), filled in the cast triggers in `Game.Triggers.cs` with the spell's targets that match `spellTargets`, as they are when the trigger happens |

**Where a new effect goes.** `ApplyAsync` (`Game.Effects.cs`) calls `ApplyGroup1Async`, `ApplyGroup2Async`, `ApplyGroup3Async`
and `ApplyGroup4Async` in that order (`Game.Effects.Part1.cs` to `Part4.cs`); each returns false for an effect it does not handle,
so the first part with a matching `case` runs it. The order of the cases inside each part is kept on purpose and matters (guards and
overlapping patterns resolve by position), so add a new effect record as a `case` in the part and position next to the closest related
cases, not at the end of a file. Helpers that cases call go in `Game.Effects.Helpers.cs`, counter placement in `Game.Counters.cs`,
and quantities in `Game.Quantities.cs`.

## Board view

The board shows one table at a time (compact) or every table at once (full). `Settings.BoardView` is "auto", "compact" or "full";
"auto" is compact on a screen under 9 inches (diagonal, from its pixel size and density). In the compact view a side's cards scale
with its height, and the career table pictures are not drawn (`GameBoard.AtTable` is false).

| Piece | Where |
|-------|-------|
| Setting, `CompactBoard`, `SmallScreen`, `CheckScreen`; small screens draw the interface `SmallScreenScale` (1.5) bigger | `scripts/Client/Settings.cs`; the "Board view" row in `scripts/UI/Menu/SettingsScreen.cs`; `App._Process` (`scripts/Client/App.cs`) |
| Layout of the shown table and the strip of this screen's cards (`PlaceAreas`, run first in `Refresh`; `StripDivider` 0.66, `StripWithHandDivider` 0.5; `ScaleFor`, `UsualSideHeight`); the stack (`_stackView`) sits near the top, over the table shown large | `scripts/UI/Board/GameBoard.Compact.cs` |
| Hand button, and the hand opening by itself for a decision about cards in it | `GameBoard.Compact.cs` (`_handButton`, `_handOpen`, `handWanted`) |
| The list of players: life buttons pick attack and target choices; name buttons show a table, a second press follows the turn again | `GameBoard.Compact.cs` (`RefreshPlayerList`, `_viewed`); a new turn resets the view (`_compactTurn`) |
| Re-checking the view every 0.5 s (a console taken out of its dock) | `GameBoard.Compact.cs` (`CheckScreen`), called from `GameBoard._Process` |
| Per-side hand, strip and pile settings (`RightInset`, `HideHand`, `CardScale`; piles never below `PileScale` 0.8) | `scripts/UI/Board/PlayerArea.cs` |
| Banner of the announcer centred at 0.27 of the board's height | `scripts/UI/Board/Announcer.cs` |

## Touch input

A touch is detected from `InputEventScreenTouch`; a real mouse motion switches back (`TrackPointer`, from `GameBoard._Input`).
The first touch on a card picks it and shows the preview. A touch on the picked card again does what a click does, and so does every
touch after that while it stays picked. A hand card can be slid out to play it after its first touch (`DragState.PlaysOnRelease`
false). A touch elsewhere puts the preview away. Hover ends do not hide the preview on touch.

| Piece | Where |
|-------|-------|
| `_touch`, `TouchActs`, `PutPreviewAwayIfMissed`, `HoverEnded` | `scripts/UI/Board/GameBoard.Touch.cs`; `OnCardClicked` and `_Input` in `scripts/UI/Board/GameBoard.cs` |
| Slide-out play (`DragState`, `PlaysOnRelease`) | `scripts/UI/Board/GameBoard.cs` |
| Finger slide that scrolls (14 px deadzone, glide after release, the press under it cancelled), and the right stick scrolling (focus, else the control under the pointer, else the front-most list) | `scripts/UI/ScrollGestures.cs`; added as a child of `App` in `App._Ready` (`scripts/Client/App.cs`) |
| Controller pointer: the left stick moves a gold dot that sends mouse events (`ui_accept` left click, `ui_cancel` right click while it shows; fades after 4 s unused; a touch or real mouse fades it at once) | `scripts/UI/VirtualPointer.cs`; added as a child of `App` in `App._Ready` (`scripts/Client/App.cs`) |
| Controller focus on a panel that must be answered (`PadFocus.OfferModal`): the screen behind loses the focus and the answer takes it at once, so one press of confirm answers it | `scripts/UI/PadFocus.cs`; used by `MenuKit`'s modal (`scripts/UI/Menu/MenuKit.cs`) and the update dialog (`scripts/UI/Menu/MainMenu.cs`) |

## Stops panel

The stops panel sets the stops per step, for opponents' turns and for this player's own turn, and full control. It opens when the
phase bar is touched in the compact view, or with the controller's top face button (`JoyButton.Y`). It is bound to the session's
`AutoPassPolicy` and works with the directional pad; it is drawn smaller when the board is too short for it.

| Piece | Where |
|-------|-------|
| Panel: toggles, `Bind`, `Open`, `Close`, focus | `scripts/UI/Board/StopsPanel.cs` |
| Phase bar: `OpensPanel` (touch opens the panel instead of the small markers), `PanelRequested`; the shared `Steps`, `StopHint`, `OwnStop`, `OpponentStop` | `scripts/UI/Board/PhaseBar.cs` |
| Controller button | `scripts/UI/Board/GameBoard.cs` (`_UnhandledInput`) |

## Commander suggestions

The deck builder and the career deck desk can show the community statistics of the chosen commander. These are not rules and the engine never reads them.

| Piece | Where |
|-------|-------|
| Parsing the commander page and average deck, `Slug` (page name) | `Arcanum.Data/Decks/CommanderInsights.cs` |
| Source URLs (`commanderInsights` in the module's `sources.json`), `CommanderInsightsUrl` | `Arcanum.Data/Modules/ContentModule.cs` |
| Download, cache in `user://commander_insights`, offline fallback | `scripts/Client/CommanderInsightsSource.cs` |
| Search / Suggestions switch, sections, sort, variants, average deck | `scripts/UI/Menu/DeckBuilder.cs` |
| Career desk: Suggestions button, owned / not yours, average deck (owned copies only), collection grouped by canonical name | `scripts/UI/Career/DeckDesk.cs` |
| Tests | `tests/Arcanum.Data.Tests/CommanderInsightsTests.cs` |

## Deck suggestions for other formats

The deck builder and the career deck desk can also show the cards of community decks like the player's, for a format the
module's deck search covers (`deckSearch` in `sources.json`). Like the commander statistics, this is not rules and the
engine never reads it.

| Piece | Where |
|-------|-------|
| Parsing search and deck pages, key cards, color fit, suggestions in the `CommanderInsights` shape, average deck | `Arcanum.Data/Decks/SimilarDecks.cs` |
| Source URLs and format ids (`deckSearch`, `DeckSearchSource`), `ModuleSources.DeckSearch` | `Arcanum.Data/Modules/ContentModule.cs` |
| Finding similar decks, download, cache in `user://deck_search`, spaced requests, `SuggestAsync`, `Average`, `LastFound` | `scripts/Client/SimilarDecksSource.cs` |
| Search / Suggestions switch for those formats | `scripts/UI/Menu/DeckBuilder.cs` |
| Career desk: casual suggestions, share of owned cards per deck, average deck (owned copies only) | `scripts/UI/Career/DeckDesk.cs` |
| Tests | `tests/Arcanum.Data.Tests/SimilarDecksTests.cs` |

## Computer players

The bot (`BotController`) answers every decision of a seat from the player's view, so it sees only what a player sees.
It plays by a plan when it knows its own deck list (`KnowDeck`). Without one, or with `UsePlan` off, it plays by its
plain heuristics. Search by simulation is on top of that when `Search` is set. The game sets it from the
**Computer thinks ahead** setting. Online computer seats (`Seat.cs`) know their deck and search when the host sets
`HostOptions.ComputerSearch`. At the priority, attackers and blockers decisions the search copies the game instead of replaying it (`Game.Copy`, `Game.Copy.cs`); see [game-copy.md](game-copy.md).

| Piece | Where |
|-------|-------|
| Deck plan from the deck list (`DeckPlan.Build`): themes with their payoff and enabler counts, `Aggression` (−1 control to +1 aggro), `AverageManaValue`, `Lands`, `Colors`; `DeckFit` for a card | `src/Arcanum.Bots/DeckPlan.cs` |
| Synergy: `ThemeKind`, `Theme` (a creature type, artifacts or enchantments, instants and sorceries, counters and the other kinds), `Synergy.Payoffs` (what a card rewards) and `Synergy.Enables` (what it brings to a theme), both read from the card's abilities; `IsSacrificeOutlet`, `AllAbilities` | `src/Arcanum.Bots/Synergy.cs` |
| Plan in use: `KnowDeck`, `UsePlan`, `Parts` (`PlanParts` flags), the synergy value of a permanent on a board | `src/Arcanum.Bots/BotController.Plan.cs` |
| Which decks are known: the bot's own list, from the client's seats (`GameSession.cs`) and from the host's seats (`Seat.cs`, online computer seats) | `scripts/Client/GameSession.cs`, `src/Arcanum.Net/Host/Seat.cs` |
| Computer search on the host (`HostOptions.ComputerSearch`, the `ComputerSearch` record: budget, rollouts, horizon, candidates): when set, `GameHost` records every seat's answers from the start (`GameRecording.Record`) and sets `GameRecording.Live` to the game. Each `Seat`'s computer (the `Computer` property, for computer seats and for seats the computer plays after a player drops) gets a `Simulator` for that recording. The client sets it from `GameSession.SearchSettings` (offline and for the games this device hosts; in events only with six or more processors, `OnlineService.Events.cs`). Games the career service hosts don't set it | `src/Arcanum.Net/Host/GameHost.cs`, `src/Arcanum.Net/Host/Seat.cs`, `scripts/Client/GameSession.cs`, `scripts/Client/OnlineService.cs`, `scripts/Client/OnlineService.Events.cs` |
| Spell choice by what the mana allows (`PlanParts.Curve`: the best set of spells, `BestUseOfMana`, not the single best one), the synergy and fit of each spell (`PlanParts.Casting`) | `src/Arcanum.Bots/BotController.Plan.cs` |
| Land to play (`ChooseLand`, `PlanParts.Lands`): the land that gives the colors the hand's spells still lack, untapped when something can be cast with it this turn | `src/Arcanum.Bots/BotController.Plan.cs` |
| Opening hand (`HandHasColors`, `PlanParts.Mulligan`): kept only if its lands give the colors of one of its spells | `src/Arcanum.Bots/BotController.Plan.cs` |
| Cards to search for, keep, discard or bottom by deck fit (`PlanParts.Cards`); removal targets by the opponents' synergy (`PlanParts.Targets`); creatures and attacks by the style (`PlanParts.Style`, the aggression) | `src/Arcanum.Bots/BotController.Plan.cs`, `src/Arcanum.Bots/BotController.cs` |
| Taking a creature (`StealValue`, `PlanParts.Steal`): for good, worth twice the creature; until end of turn, only in the first main phase when the attack with it is lethal. `GainControl` with no new controller counts as harmful (`IsHarmful`, `TargetIsHarmed`) | `src/Arcanum.Bots/BotController.Plan.cs`, `src/Arcanum.Bots/Evaluation.cs` |
| Search of the first action in an own main phase (empty stack), of the attack declaration and of the blocks: `Search` (a `Simulator`), `SearchMargin` (0.03: an option must beat the rules-of-thumb choice by this much), `TimedScoreAsync` counts the searched decisions and their time (`SearchedDecisions`, `SearchTicks`, `LongestSearchTicks`) for measuring. Options for the action: the planned choice, pass, one land and one spell per name, abilities, no attack, all attack, each planned attacker held back. Options for the blocks (`SearchBlocksAsync`, `FirstAnswer.Blocks`; not with a block tax): the planned blocks, no blocks, and the plan with each planned blocker held back, each made legal. At most six options | `src/Arcanum.Bots/BotController.Search.cs` |
| Search of instants and abilities outside the bot's own main phase (`SearchActionAsync`): only with a spell or a non-mana ability to use (`Acts`, `Game.IsManaAbilityOf`, public), and only when something is on the stack, in the declare blockers step, or at an opponent's end step | `src/Arcanum.Bots/BotController.Search.cs`, `src/Arcanum.Engine/Game.Effects.Helpers.cs` |
| Recording a game: `Recorder` passes each answer of a seat to its real controller and records it; `GameRecording.Record` wraps every seat | `src/Arcanum.Bots/Simulation/Recorder.cs`, `src/Arcanum.Bots/Simulation/Simulator.cs` |
| Replaying: `Replayer` gives the recorded answers back in order, then hands over to `Then`; `ReachedEnd` runs at the position the recording ended in. While `IReplayingController.Replaying` holds, the game skips the legal action list at priority (`Game.Priority.cs`) and gives the seat the one view it built first (`ViewFor`, `Game.cs`) | `src/Arcanum.Bots/Simulation/Recorder.cs`, `src/Arcanum.Engine/Game.Priority.cs`, `src/Arcanum.Engine/Game.cs`, `src/Arcanum.Engine/Players/IPlayerController.cs` |
| Game copy: `Game.Copy(controllers)` copies the game at a copy point (`CanCopy`: priority, the attackers declaration, or the blockers declaration when there is no block tax; no game mode) and the copy resumes there from `_resume` (`ResumePoint.None`, `Priority`, `Attackers`, `Blockers`; the point is set in `_waiting` around the ask in `Game.Priority.cs`, `Game.Combat.cs` and `Game.BlockTax.cs` (`DeclarePaidBlocksAsync`)); `Game.Turn.cs` (`RunStepAsync`) asks for attackers, or for blockers again (from the defender at `_blockingDefender`, `DeclareBlockersAsync(resume)` in `Game.Combat.cs`), then the mode step, then priority. Copied with `DeepCopier` (`src/Arcanum.Engine/Core/DeepCopier.cs`), which uses the generated copiers in `src/Arcanum.Engine/Game.Copiers.g.cs` (do not edit by hand) and falls back to reflection (`DeepCopier.CopiedByReflection`). The copy rules (wiring cleared, `Config` kept) are `Game.CopyRule`. Rebuild the generated file with `ARCANUM_WRITE_COPIERS=1 dotnet test tests/Arcanum.Engine.Tests --filter GeneratedCopiers`; `GeneratedCopiersTests.GeneratedCopiersAreCurrent` fails when it is out of date. The generator is `tests/Arcanum.Engine.Tests/CopierGenerator.cs`. | `src/Arcanum.Engine/Game.Copy.cs`, `src/Arcanum.Engine/Core/DeepCopier.cs`, `src/Arcanum.Engine/Game.Copiers.g.cs`, `tests/Arcanum.Engine.Tests/CopierGenerator.cs`, `tests/Arcanum.Engine.Tests/GeneratedCopiersTests.cs`, `tests/Arcanum.Data.Tests/GameCopyTests.cs` |
| Simulator: `Simulator.ScoreAsync` rebuilds the game from the recording (the engine is deterministic), or, at a copy point with `UseCopies` (on by default) and `GameRecording.Live`, copies the live game once (`TimedCopy`) and each rollout copies that copy (`PlayOnCopyAsync`); deals the cards the seat can't see anew (`Deal`), gives the option with `FirstAnswer`, plays `Horizon` turns (default 4) with plain bots, `Rollouts` per option (default 4), `RolloutTimeout` (10 s), and scores the position for the seat (`Score`: 1 won, 0 lost). Games played on share the `Workers` semaphore (processors − 1) | `src/Arcanum.Bots/Simulation/Simulator.cs` |
| Imagined hidden cards, dealt at the end of a replay or in a copy (`Simulator.Deal`): the seat's own library in a new order; each opponent's hidden hand and library replaced by imagined cards (`Game.ReplaceHiddenCard`, for hand and library cards only, in a rebuilt game or a copy); the game's random source reseeded (`DeterministicRng.Reseed`) | `src/Arcanum.Bots/Simulation/Simulator.cs`, `src/Arcanum.Engine/Game.cs`, `src/Arcanum.Engine/Core/DeterministicRng.cs` |
| Imagining (`OpponentModel.Weigh`, `ImagineFrom`): spells weighted by the colors seen (the commander's identity when there is one, strictly), by the seen cards' themes (`Synergy.Payoffs`, `Synergy.Enables`), and a singleton deck's commander once only; lands that make those colors. The hidden lands come first, by the deck's land share (commander 37%, 45 cards or fewer 17 of 40, else 24 of 60) less the lands seen, then the spells. With no candidates, the seen cards fill in. The weighing is cached in `Simulator` (`WeighingFor`) for each set of seen cards | `src/Arcanum.Bots/Simulation/OpponentModel.cs`, `src/Arcanum.Bots/Simulation/Simulator.cs` |
| Candidates (`CardCandidates`, `CardGuess`): the cards an opponent could have, given by the host through `GameRecording.Candidates` for the seen cards | `src/Arcanum.Bots/Simulation/OpponentModel.cs`, `src/Arcanum.Bots/Simulation/Simulator.cs` |
| The host's candidates (`CardPoolGuess.For`). In a limited game: the event's set, each card weighted by how often a booster has its rarity. Otherwise: the cards of the sets the seen cards were printed in (basic lands say nothing). Only fully supported cards are offered; basic lands always are (snow ones at 0.1). Each card's weight doubles for each spread (the set's standard deviation) its in-hand win rate is above the set's mean, scaled by confidence (`ratings/<set>.json`). The set comes from `MatchSetup.LimitedSet` (`LimitedService.NextGame` fills it for `set:` events) through `GameSession.LimitedSet`; `GameSession.Candidates()` builds the provider | `src/Arcanum.Data/CardData/CardPoolGuess.cs`, `scripts/Client/GameSession.cs`, `scripts/Client/LimitedService.cs` |
| The **Computer thinks ahead** setting (`BotThinksAhead`, on by default on every device; applies from the next game). The budget per decision (`SearchBudget`: 5 s under six processors, else 2 s) and the shape (rollouts × horizon: 2 × 2 under six processors, else 4 × 4; `ARCANUM_BOT_SEARCH_SHAPE`) depend on the processor count; `ARCANUM_BOT_SEARCH` forces it off, on, or on with a budget in milliseconds. `GameSession.SearchSettings` builds the `ComputerSearch` for both the games played here and the ones this device hosts | `scripts/Client/Settings.cs`, `scripts/UI/Menu/SettingsScreen.cs`, wired in `scripts/Client/GameSession.cs` |
| Crew, teamwork and collect evidence choices (`CardChoiceRequest.MinTotalPower`, `MinTotalManaValue`): the least valuable creatures until their total power is enough, and the fewest cards with the biggest mana values that reach the total (`ChooseCardsAsync`, `Arcanum.Bots/BotController.cs`) |
| Tests: unit tests of the plan (`BotPlanTests`) and of the imagined cards (`OpponentModelTests`); the recorded game replays to the same end (`ForkCostTests`, optional); the mirrored matchups (`BotMatchupTests`, optional) | `tests/Arcanum.Engine.Tests/BotPlanTests.cs`, `tests/Arcanum.Engine.Tests/OpponentModelTests.cs`, `tests/Arcanum.Data.Tests/ForkCostTests.cs`, `tests/Arcanum.Data.Tests/BotMatchupTests.cs` |

The search never reads an opponent's real hidden cards. A replay sees only the seen cards, the commander and the number of
the opponent's cards; everything hidden is imagined as above. The candidates come from the card database in the client
(never from the opponent's deck), so a search can only guess, and it guesses from what the seat has seen.
In the rollouts only the searching seat's bot knows its deck list (`KnowDeck`, `Simulator.cs`). The other seats' bots play
without a plan, since their lists are hidden from the searching seat.

The optional tests are off unless their environment variables are set (see [building.md](building.md), *Debug options*).
Their matchups play each pairing in both seat orders with the same seeds, so two equal bots score exactly 50%. Measured
against the previous bot on the module's decks: the plan parts alone made no difference that could be measured, the land
choice about +1.7 points, and search (4 rollouts, horizon 4) won about 71% of 244 games while it still dealt from the
opponent's real cards; imagining them, it won 72.5% of 40 games with the set as candidates and 67.5% with only the seen
cards. A searched decision took about 0.36 s on average in a single game on a 16-processor machine.

## Draft statistics

The module's draft statistics (`ratings/<set>.json`) give the computer drafters a community view of each card, and career
drafts show it to the player as grades. The engine never reads them as rules. A set without the file drafts on the bots'
own ratings only.

| Piece | Where |
|-------|-------|
| Parsing the file, `Stats`, `Deviation`, `Score`, `Grade`, `MinGames` (500) | `Arcanum.Data/Limited/DraftRatings.cs` |
| `LoadDraftRatings(set)` | `Arcanum.Data/Modules/ContentModule.cs` |
| Opponent `Skill` per seat (0–1, saved as `skill`, `DefaultSkill` 0.6) | `Arcanum.Data/Limited/LimitedEvent.cs` |
| Community value blended into the bot's own rating by skill × confidence (`DraftOption.Community`, `Confidence`) | `Arcanum.Bots/Limited/CardRating.cs`, `Arcanum.Bots/Limited/DraftPicker.cs` |
| Misjudgment noise by skill, colour choice, deck build by skill | `Arcanum.Bots/Limited/DraftPicker.cs`, `Arcanum.Bots/Limited/LimitedDeckBuilder.cs` |
| `RatingsFor`, `Ratings`, `Option`, `RatingsForPlayer` (player's "Build for me" uses them only then) | `scripts/Client/LimitedService.cs` |
| Career opponent skill (`DraftSkill`, `VisitorSkill`), `DraftRatingsForPlayers` (the switch for players) | `scripts/Client/CareerService.cs` |
| Grade strip, tooltip and credit line on cards | `scripts/UI/Career/DraftGrades.cs`, shown in `scripts/UI/Career/EventScreen.cs` and `scripts/UI/Career/DeckDesk.cs` |
| Career table mats (`TableMat` overlays, picture playmats via `Playmat.Picture`, `ApplyPlaymats`, mat rects from the table's JSON; one shared tournament mat via `MatchSetup.TournamentMat`; its cloth, band and crest drawn in one `_Draw`) | `scripts/UI/Board/Playmat.cs`, `scripts/UI/Board/TournamentMat.cs`, `scripts/UI/Board/GameBoard.cs` |
| Turn border and glow left open along the line between the players on a tournament mat (`PlayerArea.OpenEdge`, `TurnGlow.Open`, set by `GameBoard` when the mat is shared) | `scripts/UI/Board/PlayerArea.TurnGlow.cs`, `scripts/UI/Board/TurnGlow.cs`, `scripts/UI/Board/GameBoard.cs` |
| Broadcast overlay on the board (clock tab, player plates, round name; `MatchSetup.Broadcast`, `RoundBroadcast`, `BroadcastPlate`; calls time on the game at the deadline) | `scripts/UI/Board/BroadcastOverlay.cs`, `scripts/UI/Board/GameBoard.cs`, data in `scripts/Client/App.cs` |
| Tests | `tests/Arcanum.Data.Tests/DraftRatingsTests.cs`, `tests/Arcanum.Engine.Tests/LimitedBotTests.cs` |

## Card search

The search language (keywords, `or`, parentheses, `-`, sorting) is read once into a `CardFilter`, which every card box uses.
A screen can add its own number terms: a `SearchTerms` maps a keyword to a value for the card, its printing and the item the
screen searches.

| Piece | Where |
|-------|-------|
| Parsing, keywords, `CardFilter.Matches`, `Sort`, warnings | `Arcanum.Data/CardData/CardSearch.cs` |
| Price terms (`usd`, `eur`), `order:usd` / `order:eur` (`CardFilter.CheapestPrice`, cheapest printing, unpriced last) | `Arcanum.Data/CardData/CardSearch.cs` |
| Curated `is:` values (`is:shockland`, `is:triome`, `is:bear`, `is:atypical`, ...) and their aliases, from the module's `search/tags.json.gz` (`SearchTags`: cards by oracle id, printings by printing id; a value without tags is a "no data" warning) | `Arcanum.Data/CardData/SearchTags.cs`, `Arcanum.Data/CardData/CardSearch.cs` (`IsTerm`) |
| Loading the tags (`ContentModule.LoadSearchTags`), set on `CardDatabase.SearchTags`; `SearchVocabulary` (the tags and the promo kinds the printings have, built by `CardDatabase.Filter`) | `Arcanum.Data/Modules/ContentModule.cs`, `Arcanum.Data/CardData/CardDatabase.cs`, `scripts/Client/ContentLoader.cs` |
| Other `is:` values the card data says: layouts, frames (`is:old`, `is:new`), finishes, frame effects (`is:showcase`, `is:extendedart`), printing names, promo kinds (`is:serialized`); underscores ignored | `Arcanum.Data/CardData/CardSearch.cs` (`IsTerm`, `BuiltIn`) |
| Printing prices and printing attributes read from the card data | `Arcanum.Data/CardData/OracleJsonl.cs`, `Arcanum.Data/CardData/CardRecord.cs` (`PrintingPrices`) |
| `CardDatabase.Filter(text, extra)` | `Arcanum.Data/CardData/CardDatabase.cs` |
| Search box: reads the filter, warnings in the tooltip and the amber text, help line from `SearchTerms.Help` | `scripts/UI/Menu/CardSearchBox.cs` |
| Career desk terms (`usd`, `price`, `p`; `qty`, `copies`, `owned`: copies not in decks while the toggle is on) | `scripts/UI/Career/DeckDesk.cs` (`CareerTerms`) |
| Deck builder prices: tile tag, deck list total, price sorts of suggestions (`PriceOf`) | `scripts/UI/Menu/DeckBuilder.cs` |
| Other face beside a large menu preview (`ShowsOtherFace`); menu card views with their other face (`CardViewOf`) | `scripts/UI/Board/CardNode.cs`, `scripts/UI/Menu/MenuKit.cs` |
| Tests | `tests/Arcanum.Data.Tests/CardSearchTests.cs` |

## Set scenes

A set file may list its scenes: cards whose art joins edge to edge into one picture. `SetDefinition.Parse` reads the
`scenes` array into `SetDefinition.Scenes` (`SceneSpec`: name, columns, and the collector numbers of each place, row by row
from the top left). A cell may hold several numbers when one art is printed under more than one number. The career
collection shows one scene at a time; cards not owned are drawn grey (`CardNode.Greyed`, the `grey` uniform of
`shaders/rounded_card.gdshader`).

| Piece | Where |
|-------|-------|
| `scenes` in the set file, `SceneSpec`, `ParseScene` | `Arcanum.Data/Limited/SetDefinition.cs` |
| Scene picker, grid, grey cards (`Scenes (N)` toggle) | `scripts/UI/Career/DeckDesk.cs` |
| Greyed card | `scripts/UI/Board/CardNode.cs`, `shaders/rounded_card.gdshader` |
| Tests | `tests/Arcanum.Data.Tests/SetPackTests.cs` |

## Career service and online career events

The career rules run in the game and in the career service (`CareerRules.Replay`, through `CareerChecker`). The
service seals a career's checked actions, and online career events run through it. Product rules are in
[career-mode.md](career-mode.md).

| Piece | Where |
|-------|-------|
| Sync: replay of new actions, seal up to the first that fails, import of older careers (`CareerChecker`) | `src/Arcanum.Data/Career/CareerChecker.cs` |
| Seal: hash chain, ECDSA P-256 signature, public keys, sync request and response (`CareerSeal`, `CareerSeals`) | `src/Arcanum.Data/Career/CareerSeal.cs` |
| Online event rules: check-in times, seating, hosts, confirmed results, ranks and points (`OnlineEvents`, `OnlineTable`, `TableReport`) | `src/Arcanum.Data/Career/OnlineEvents.cs` |
| Online entry and result (`EnterEvent.Online`; `FinishEvent` with `Table`, `Players`, `Pvp`); online prizes | `src/Arcanum.Data/Career/CareerAction.cs`, `src/Arcanum.Data/Career/CareerRules.cs` (`Enter`, `Finish`) |
| Constructed sideboarding between games (rule 100.4a: `EventSeat.Registered`, `ValidateSideboarding`, `WithSideboardFrom`) | `src/Arcanum.Data/Formats/FormatRules.cs`, `src/Arcanum.Data/Limited/LimitedEvent.cs`; the desk in `scripts/UI/Career/DeckDesk.cs` (`Sideboarding`) |
| Online event host: rounds, round clock (`Game.CallTime`, `EndOnTime`), day end and cut (`DayOver`, `DayEnded`, `LimitedEvent.ApplyCut`), playoff, `EventOptions.DayEndsAfter` | `src/Arcanum.Net/Events/EventHost.cs`, `src/Arcanum.Net/Protocol/EventMessages.cs` (`EventInfo`, `MatchInfo`); the career table's setup in `scripts/Client/OnlineService.CareerTable.cs` |
| Tardiness in hosted events (seat call, game loss, no-show, both lost; `Away`, the waiting block in `Poll`; `LimitedEvent.SeatCall`, `NoShowAfter`, `GiveGameLoss`, `NoShow`; match fields `Waiting`, `Due`, `Late`, `BothLost`, saved as `waiting`, `due`, `late`, `bothLost`) | `src/Arcanum.Data/Limited/LimitedEvent.cs`, `src/Arcanum.Net/Events/EventHost.cs`; the event screen in `scripts/UI/Menu/LimitedScreen.cs`; the match's fields reach players in `MatchInfo` (`src/Arcanum.Net/Protocol/EventMessages.cs`) |
| Service endpoints | `server/Arcanum.CareerService/Program.cs` |
| Service: online tables, host reports, results, leaderboards (SQLite) | `server/Arcanum.CareerService/EventDesk.cs` |
| Service: checked careers (SQLite) | `server/Arcanum.CareerService/CareerStore.cs` |
| Service: EOS Connect ID token check | `server/Arcanum.CareerService/EosIdentity.cs` |
| Service: device sign-in links (`/auth/link`, `/link`, `/link/callback`) | `server/Arcanum.CareerService/DiscordLinks.cs` |
| Service: cloud files (`/cloud/{name}`, `CloudFile`, `SaveCloudFile`) | `server/Arcanum.CareerService/CareerStore.cs` |
| Service: career world per content version, built at start | `server/Arcanum.CareerService/CareerWorlds.cs` |
| Service: systemd unit, Caddy, deploy scripts | `server/Arcanum.CareerService/deploy/` |
| Client: sync and checked status | `scripts/Client/CareerService.Check.cs` |
| Client: check-in, table, result, leaderboards | `scripts/Client/CareerService.Online.cs` |
| Client: the table's unlisted lobby, started when its people are in | `scripts/Client/OnlineService.CareerTable.cs` |
| Client: EOS ID token (`IdTokenAsync`, `IIdentityTokens`) | `scripts/Client/OnlineService.Directory.cs`, `scripts/Client/Eos/EosOnlineServices.cs`; without the services' library, `scripts/Client/Eos/EosWebIdentity.cs` |
| Client: Discord sign-in, browser or device code (`DiscordSignIn.LinkAsync`, `Account.HasBrowser`, `AccountPrompt.ShowCode`) | `src/Arcanum.Net/Services/DiscordSignIn.cs`, `scripts/Client/Account.cs`, `scripts/UI/Menu/AccountPrompt.cs` |
| Client: cloud files on the career service (`CloudAsync`) | `scripts/Client/ServiceCloud.cs`, `scripts/Client/OnlineService.Directory.cs` |
| Client: file times where the file system can't set them (`file-times.json`) | `scripts/Client/FileTimes.cs` |
| Client: online event screen and standings | `scripts/UI/Career/EventScreen.Online.cs`, `scripts/UI/Career/CareerScreen.Standings.cs` |
| Tests | `tests/Arcanum.Data.Tests/CareerSealTests.cs`, `tests/Arcanum.Data.Tests/OnlineEventsTests.cs` |

## Adding a mechanic

1. Read the rule in the comprehensive rules and decide the exact behaviour, choices and UI it implies (no approximations).
2. Add the engine record (effect, quantity kind, condition, trigger event or definition property) with a doc comment quoting the
   rules text it implements.
3. Parse it in `CardScript` (or the group's partial file).
4. Execute it in the matching place from the table above. Choices go through the player controller (`ChooseYesNoAsync`,
   `ChooseCardsAsync`, ...) so bots and the UI both handle them. A new layer effect (a static ability or a continuous effect in
   a layer) must declare the `Aspect`s it reads and writes (`Game.LayerDependencies.cs`), or the fast path may skip a dependency on it.
5. If the mechanic is a keyword, make `CardFactory` derive it or list it in `ScriptedKeywords`.
6. Write a test in `tests/Arcanum.Engine.Tests` with invented cards (see `HeroesTests.Make` for building one from a script).
7. Document the vocabulary in `card-scripts.md`.
8. Run `dotnet build Arcanum.csproj`, the test projects, and a soak of the set that needs it (`SetGamesTests`, environment variables
   `ARCANUM_MODULE_PATH` (absolute), `ARCANUM_SMOKE_SET`, `ARCANUM_SMOKE_GAMES`).
   The real card data comes from `ARCANUM_CARD_FILE` when it is set (optional). Otherwise the checks use the card data the game
   downloaded and converted for the module's current version, `user://card_data/<module id>/cards.v*-<module version>.jsonl.gz`
   (on Linux, `~/.local/share/godot/app_userdata/Arcanum/card_data/<module id>/`). Start the game once with that module version
   to create it. A device that installed a module zip with `card-data.bin` keeps no such file, so the checks find none there.
   Without card data, the set and commander soaks fail with a message saying so; the booster and script coverage
   checks skip.
