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

1. `OracleJsonl.Import` reads card records. A printing whose `promo_types` has `fracturefoil` is marked `Fracture` (`CardRecord.Printings`), and the compact card file stores it as `fracture_foil`. The same record carries each printing's market prices (`PrintingPrices`: Cardmarket euros and TCGplayer dollars, foil and nonfoil) and its attributes (`Etched`, `Border`, `Frame`, `FrameEffects`, `PromoTypes`, ...); the compact card file keeps them as well, under `cards.v12-<module version>`.
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
| Quantities by target loyalty, domain, distinct powers, graveyard mana values, cards milled or drawn this way | `EvalFracture` (`Game.Fracture.cs`); parsed by `ParseFractureQuantity` (`Arcanum.Data/Scripts/CardScript.Fracture.cs`) |
| Filters | `Matches` (`Game.Abilities.cs`), targeting in `Game.FiltersAndTargeting.cs` |
| Triggered abilities | `Observers` and `QueueObservers` (`Game.Abilities.cs`); trigger bookkeeping in `Game.TriggerTracking.cs` |
| Static abilities, layers | `RecomputeContinuousEffects` (`Game.Abilities.cs`) |
| Casting, costs, modes, X | `Game.Priority.cs` (casting flow), `Game.CastingCosts.cs` (ways to cast, `CanPayToCast`) |
| Mana payment | `Rules/ManaPayment.cs`, `Mana/ManaCost.cs`, `Mana/ManaPool.cs` |
| Zone changes and their replacements | `MoveCardAsync` (`Game.Zones.cs`) |
| Cards outside the game (a sideboard; `revealTwoFromOutsideOpponentChooses`) | `Zone.Outside` (`State/Zone.cs`; hidden like the library and hand, `IsPublic`), `Player.Outside` (`State/Player.cs`). Game setup (`Game.cs`) puts each `PlayerSetup.Sideboard` (`GameConfig.cs`) there. A deck's sideboard comes from `DeckList.ResolveSideboard` (`Arcanum.Data/Decks/DeckList.cs`); in limited play, `EventSeat.SideboardFromPool` (`Arcanum.Data/Limited/LimitedEvent.cs`) gives the pool's cards not in the deck |
| Preparation cards and Empower Jace (the copy in exile, its cast, unpreparing) | `Game.Prepared.cs`; the cast list is `PreparedSpells` (`Game.Priority.cs`), leaving the battlefield calls `Unprepare` (`Game.Zones.cs`) |
| Damage, prevention, redirection | `DealDamageEventAsync` (`Game.Damage.cs`), `Game.ReplacementsAndLife.cs` |
| Impending (cast for its impending cost, enters with time counters, not a creature while it has one, a counter removed at its controller's end step) | `CastingWayKind.Impending` (`Game.CastingCosts.cs`); `SpellOnStack.Impended` and the enter counters (`Game.Priority.cs`, `_enteringImpended` and `MoveCardAsync`); `Card.Impended` (`State/Card.cs`, set in `Game.Zones.cs`); the type layer and `ImpendingEndStep` (`Game.Abilities.cs`) |
| Morph and face-down spells and permanents (cast face down for {3} as a 2/2 with no name, text or mana cost; turned face up for the morph cost as a special action; revealed when they leave) | `CardDefinition.Morph` set from the rules text by `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `CastingWayKind.FaceDown` and `AsFaceDown` (`Game.CastingCosts.cs`); the face-down cast flow and the turn-up actions in the priority list (`Game.Priority.cs`); `TurnFaceUp` (`Players/PlayerAction.cs`), `TurnFaceUpActions` and `TurnFaceUpAsync` (`Game.Morph.cs`); `Card.TurnedFaceDown` and `Card.FaceDownCreature` (`State/Card.cs`); the reveal on a zone change in `MoveCardAsync` (`Game.Zones.cs`); `FaceDown` and `FaceDownCard` on `CardView` (`Views/GameView.cs`, built in `Views/ViewBuilder.cs`) |
| Choose a card type as it enters, and protection from it | `EnterChoice.CardType` (`Game.Priority.cs`); `ChoosableCardTypes`, `PlayerProtectionFromTypes`, `PlayerProtectedFromSource`, the granted protection in the layers (`Game.Abilities.cs`); damage from such sources in `Game.Damage.cs` |
| Toxic (combat damage to a player gives poison) and `hasToxic` | `Game.Damage.cs` (`Toxic` on `CardDefinition`, set from a token spec's `toxic`); the filter in `Matches` (`Game.Abilities.cs`) |
| Incubate, tokens that enter with counters, predefined Incubator and Map tokens | `Incubate` in `ApplyAsync` (`Game.Abilities.cs`); `CreateTokenEventAsync` and `CreateTokenAsync` (`enterCounters`); `PredefinedTokens.Incubator` and `PredefinedTokens.Map` (`Cards/PredefinedTokens.cs`) |
| "Whenever you create one or more tokens" | `TokensCreated` event (`Events/GameEvent.cs`), emitted once per batch by `CreateTokenEventAsync`; the trigger is queued in `Game.Abilities.cs` |
| Delayed "at the beginning of the next end step" | `AtNextUpkeep` with `EndStep` (`Abilities/Effects.cs`); `State.AtNextEndStepTriggers` (`State/GameState.cs`), released at the end step in `Game.Abilities.cs` |
| Cards exiled with this, put into a graveyard | `PutExiledWithThisIntoGraveyard` in `ApplyAsync` (`Game.Abilities.cs`); `TriggeredInGraveyard` (`Abilities/Conditions.cs`, checked in `Game.Abilities.cs`) |
| Tapping / untapping | `Tap`, `Untap`, `EnterTapped` (`Game.Permanents.cs`) — always go through these |
| State-based actions | `CheckStateBasedActionsAsync` (`Game.StateBasedActions.cs`) |
| Turn structure, extra steps | `Game.Turn.cs` |
| End-of-match procedure when a round's time is up (`CallTime`, the additional turns, a draw) | `Game.cs` (game loop; `TimeCalledAt`, `AdditionalTurns`) |
| Combat | `Game.Combat.cs` |
| Goads (`goad`, `forRestOfGame`) | `Goad` in `Game.Abilities.cs` adds to `State.Goads`, or to `State.GoadsForever` for the rest of the game (`State/GameState.cs`). `Game.Combat.cs` reads both; `State.Goads` entries of a goader are removed at that goader's next turn (`Game.Turn.cs`) |
| Planeswalker attack bans (`cantAttackYourPlaneswalkers`) | `State.PlaneswalkerAttackBans` (`State/GameState.cs`), added by `CantAttackYourPlaneswalkersThisTurn` in `Game.Abilities.cs`; attack targets are checked against it in `Game.Combat.cs` |
| Life that can't change (`protectionAndLifeLock`) | `Player.LifeCantChange` (`State/Player.cs`), set by `ProtectionAndLifeLockUntilTheirTurn` in `Game.Abilities.cs`. `ChangeLife` (`Game.Combat.cs`) and `GainLifeAsync` (`Game.ReplacementsAndLife.cs`) skip changes while it is set; `Game.Turn.cs` clears it at that player's turn |
| Commander rules | `Game.Commander.cs` |
| Per-card and per-game state | `State/Card.cs`, `State/Player.cs`, `State/GameState.cs`, `State/StackItem.cs` |
| Events the UI and log see | `Events/GameEvent.cs` |
| Tournament rounds, Swiss standings and tiebreakers, playoffs, drops, round clock (`RoundMinutes`, `Deadline`, `TimeUp`, `EndOnTime`) (career and limited events) | `Arcanum.Data/Limited/LimitedEvent.cs`; time called on the game by `LimitedService.EndIfTimeUp` (`scripts/Client/LimitedService.cs`) |
| Career calendar: `when`, `months`, multi-day runs, attendance, `invitational` and invitations | `Arcanum.Data/Career/CareerRules.cs` (calendar section) |
| Offspring (the optional cost at cast; the 1/1 token copy when it enters, from last known information if it has left) | `OffspringTrigger` added by `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); `CastingChoices.OffspringPaid` (`Game.CastingCosts.cs`) and `SpellOnStack.OffspringPaid` (`State/StackItem.cs`); `_enteringOffspring` in `MoveCardAsync` (`Game.Zones.cs`) sets `Card.OffspringPaid` (`State/Card.cs`); `LastKnown.OffspringPaid`; quantity `offspringPaid` (`Game.Abilities.cs`); `ResolvingSourceGone` (`Game.SourceObject.cs`) |
| Forage (rule 701.61a: exile three cards or sacrifice a Food, the player's choice) | `ExtraCost.Forage` and the `forage` cost token (`Abilities/Costs.cs`, `Arcanum.Data/Scripts/CardScript.cs`); `ForageWays`, `ChooseForageWayAsync`, `Foraging` (`Game.Priority.cs`); event `Foraged` (`Events/GameEvent.cs`); trigger `YouForage` (`Abilities/AbilityDefinition.cs`); the board's "X forages" line in `scripts/UI/Board/EventLogFormatter.cs` and `scripts/UI/Board/GameBoard.cs` |
| Class levels (level sections, level-up ability, `becomesLevel`, `cardRulesLevel`) | `Card.ClassLevel` and `Card.Abilities` (`State/Card.cs`); `AbilityDefinition.ClassLevel` and `InClassLevelOrder` (`Abilities/AbilityDefinition.cs`); `BecomeClassLevel` (`Abilities/Effects.cs`) and `ClassLevelIs` (`Abilities/Conditions.cs`), applied in `Game.Abilities.cs`; event `ClassLevelChanged` (`Events/GameEvent.cs`); `Card.HasTextAbilities` for `cardRulesLevel`; the status panel in `scripts/UI/Board/CardStatusPanel.cs` |
| Expend (mana spent on spells this turn, counted from what was actually paid) | `Player.ManaSpentOnSpellsThisTurn` (`State/Player.cs`), added to in `Game.Priority.cs` from the paid mana, reset in `Game.Turn.cs`; event `ManaSpentOnSpell` (`Events/GameEvent.cs`); `TriggerEvent.YouExpend` checked in `Game.Abilities.cs` |
| Valiant counting (first time each turn a spell or ability you control targets the permanent) | `GameState.TargetedThisTurn` (`State/GameState.cs`), recorded in `Game.Abilities.cs`; `TriggerEvent.BecomesTargetOfYours` and its `nth` count in `Game.Abilities.cs`; the trigger filter in `Game.TriggerTracking.cs` |
| Pawprint modes (a spell's budget, each mode's cost in pawprints, repeated modes) | `AbilityDefinition.PawprintBudget` and `Mode.Pawprints` (`Abilities/AbilityDefinition.cs`); parsed by `WithModes` (`Arcanum.Data/Scripts/CardScript.cs`); chosen with `PawprintBudget` in `Game.Abilities.cs` |
| Colored cost reductions (`spellCost` with `"color"`) | `ManaCost.MinusColored` (`Mana/ManaCost.cs`); applied in `Game.CastingCosts.cs` and `Game.Priority.cs` |
| Graveyard cast rights (`castFromGraveyard`, `mayCastFromGraveyard`, extra cost, finality) | `GraveyardCastRight` record and `State.GraveyardCastRights` (`State/GameState.cs`); added in `Game.Priority.cs` and `Game.Abilities.cs`; `GraveyardCastRightsFor` and `GraveyardRightUsed` (`Game.Priority.cs`); `HasGraveyardCastRight` (`Game.Origins.cs`) |
| Free casts with a non-mana cost (the cast is free apart from the extra cost) | `_castFreeExtra` (`Game.CastingCosts.cs`, used in `Game.Priority.cs` and `Game.Abilities.cs`) |
| Counters you put are doubled (`doublesCountersYouPut`) | `CountersYouPutDoublings` (`Game.Abilities.cs`) |
| Sacrifice any number as an additional cost (`additionalSacrificeAny`) | `SacrificeAnyCounts` (`Game.CastingCosts.cs`, used in `Game.Priority.cs`); the filter is `CardDefinition.CastSacrificeAny` (`Cards/CardDefinition.cs`) |
| Escape (cast from the graveyard for the escape cost, exiling other cards; enters with counters) | `CastingWayKind.Escape` (`Game.CastingCosts.cs`); `CardDefinition.Escape`, `EscapeExtra` and `EscapesWithCounters` read in `CardFactory` (`Arcanum.Data/CardData/CardFactory.cs`); the counters are placed in `Game.Priority.cs` |
| Counters a permanent spell enters with (impending, X counters, ravenous, escape, "enters with additional counters"; placed as it enters, rule 122.6) | `EnteringCounters` (`Game.Priority.cs`); `_enteringCastX` (`Game.Priority.cs`, `Game.Zones.cs`) sets `Card.CastX` (0 if not cast, `State/Card.cs`) |
| Token creation replacements (twice that many, extra Food, Clue, Food and Treasure, Squirrels, Fish into Shark) | `CreateTokenEventAsync` (`Game.Abilities.cs`) |
| Auras on players | `Card.AttachedPlayer` (`State/Card.cs`); `_enteringAttachedPlayer` (`Game.Priority.cs`, `Game.Zones.cs`); the state-based check in `Game.StateBasedActions.cs`; the aura is shown on the enchanted player's side by `PlayerArea.SideOf` (`scripts/UI/Board/PlayerArea.cs`), with "Enchanting <player>" in `scripts/UI/Board/CardStatusPanel.cs` |
| Activated abilities from the command zone | legal actions in `Game.Priority.cs` (`FromCommandZone`); `FromCommandZone` on `AbilityDefinition` (`Abilities/AbilityDefinition.cs`) |
| Gained offspring (a creature spell gets offspring from a permanent's `creatureSpellsGainOffspring`) | `CastingChoices.GrantedOffspringPaid` (`Game.CastingCosts.cs`) and `GrantedOffspringPaid` (`State/StackItem.cs`); `GrantedOffspringTrigger` (`Game.Priority.cs`) |
| Every creature type (layer 4) | `RecomputeEveryCreatureType` (`Game.Abilities.cs`); `Card.EveryCreatureType` (`State/Card.cs`) |
| Next-combat attack rules (attacks if able, can't attack a player) | `GameState.NextCombatRules` (`State/GameState.cs`), checked in `Game.Combat.cs` |
| Delayed "at end of combat" triggers | `GameState.AtEndOfCombat` (`State/GameState.cs`), used in `Game.Combat.cs` |
| Damage log (each entry keeps `SourceController`) | `DamageLog` (`State/GameState.cs`), written in `Game.TriggerTracking.cs` |
| Hideaway (the rest go to the bottom in a random order, rule 702.75a; choice among several hidden cards) | `Hideaway` in `ApplyAsync` (`Game.Abilities.cs`); `Hideaway` (`Abilities/Effects.cs`) |
| Draw limit ("can't draw more than one card each turn", `opponentsDrawAtMostOne`) | `DrawOneAsync` (`Game.Zones.cs`) |

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
| Price terms (`usd`, `eur`), `order:usd` / `order:eur` (`CardFilter.CheapestPrice`, cheapest printing, unpriced last) | `Arcanum.Data/CardData/CardSearch.cs` |
| Land cycle nicknames (`is:shockland`, `is:triome`, ...), judged from the rules text (`LandCycles`) | `Arcanum.Data/CardData/CardSearch.cs` |
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
