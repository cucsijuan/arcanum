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
`Game.Heroes.cs` (`ApplyHeroesAsync`, `EvalHeroes`). The commander decks of the heroes set use the same split: `CardScript.HeroesCommander.cs` (`ParseHeroesCommanderEffect`,
`ParseHeroesCommanderQuantity`), `Effects.HeroesCommander.cs` and `Game.HeroesCommander.cs` (`ApplyHeroesCommanderAsync`,
`EvalHeroesCommander`). Follow that pattern for a new group.

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
| End-of-match procedure when a round's time is up (`CallTime`, the additional turns, a draw) | `Game.cs` (game loop; `TimeCalledAt`, `AdditionalTurns`) |
| Combat | `Game.Combat.cs` |
| Commander rules | `Game.Commander.cs` |
| Per-card and per-game state | `State/Card.cs`, `State/Player.cs`, `State/GameState.cs`, `State/StackItem.cs` |
| Events the UI and log see | `Events/GameEvent.cs` |
| Tournament rounds, Swiss standings and tiebreakers, playoffs, drops, round clock (`RoundMinutes`, `Deadline`, `TimeUp`, `EndOnTime`) (career and limited events) | `Arcanum.Data/Limited/LimitedEvent.cs`; time called on the game by `LimitedService.EndIfTimeUp` (`scripts/Client/LimitedService.cs`) |
| Career calendar: `when`, `months`, multi-day runs, attendance, `invitational` and invitations | `Arcanum.Data/Career/CareerRules.cs` (calendar section) |

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
| Career table mats (`TableMat` overlays, picture playmats via `Playmat.Picture`, `ApplyPlaymats`, mat rects from the table's JSON; one shared tournament mat via `MatchSetup.TournamentMat`) | `scripts/UI/Board/Playmat.cs`, `scripts/UI/Board/TournamentMat.cs`, `scripts/UI/Board/GameBoard.cs` |
| Broadcast overlay on the board (clock tab, player plates, round name; `MatchSetup.Broadcast`, `RoundBroadcast`, `BroadcastPlate`; calls time on the game at the deadline) | `scripts/UI/Board/BroadcastOverlay.cs`, `scripts/UI/Board/GameBoard.cs`, data in `scripts/Client/App.cs` |
| Tests | `tests/Arcanum.Data.Tests/DraftRatingsTests.cs`, `tests/Arcanum.Engine.Tests/LimitedBotTests.cs` |

## Card search

The search language (keywords, `or`, parentheses, `-`, sorting) is read once into a `CardFilter`, which every card box uses.
A screen can add its own number terms: a `SearchTerms` maps a keyword to a value for the card, its printing and the item the
screen searches.

| Piece | Where |
|-------|-------|
| Parsing, keywords, `CardFilter.Matches`, `Sort`, warnings | `Arcanum.Data/CardData/CardSearch.cs` |
| `CardDatabase.Filter(text, extra)` | `Arcanum.Data/CardData/CardDatabase.cs` |
| Search box: reads the filter, warnings in the tooltip and the amber text, help line from `SearchTerms.Help` | `scripts/UI/Menu/CardSearchBox.cs` |
| Career desk terms (`usd`, `price`, `p`; `qty`, `copies`, `owned`: copies not in decks while the toggle is on) | `scripts/UI/Career/DeckDesk.cs` (`CareerTerms`) |
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
