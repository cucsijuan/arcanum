# Code review backlog

Review of the engine, data, bots, network and client after M11. Nothing here is fixed yet: each item says where the problem
is, why it matters, how to fix it and how to prove the fix. Line numbers are from the review and will drift.

Priorities: **P1** a rule or behavior that can be wrong in a real game; **P2** a duplicated path or a design that lets the same
kind of bug come back; **P3** hygiene. "To confirm" items were found by reading the code: write the failing test first.

## P1 — rules

### R1. Permanents that "enter tapped" are tapped after they entered — **done**
`MoveCard` has no way to say "tapped", so about eight effects move the card and set `Tapped` afterwards
(`Game.Abilities.cs` ~721, 1064, 1390, 1869, 2028, 3123, 3254, `Game.Voting.cs`). Rule 614.1c: the permanent enters
already tapped. Between the move and the tap, triggers of the entering are collected and static effects recomputed with the
permanent untapped.
**Fix:** a `tapped` parameter on `MoveCard` (next to the definition's own enters-tapped replacements), used by every caller.
**Test:** an enters trigger with an intervening "if it's untapped/tapped" condition on a permanent put onto the battlefield
tapped by an effect.

### R2. Counters a permanent enters with are put on after its enters triggers were collected (to confirm) — **done**
In `MoveCard` the `CardMoved` event is emitted, which collects triggers and checks intervening "if" conditions
(`AddPending`, rule 603.4), and only then are the enter counters placed. Rule 614.1c / 122.6: it enters with them. An
enters trigger whose condition looks at counters or at power or toughness raised by them doesn't trigger.
**Fix:** put the counters on as part of the move, before `CardMoved`, still emitting "counters put" events afterwards.
**Test:** a creature that enters with +1/+1 counters and an enters trigger "if its power is N or greater".

### R3. A commander going to a hand or library is offered the command zone after the move — **done**
`Game.Commander.cs` `OfferCommanderReturnsAsync` says so itself: rule 903.9b is a replacement effect, but the card first
goes to the hand or library (shuffles included) and is then offered.
**Fix:** treat the hand and library case as a replacement in `MoveCard` (the owner chooses as it would move), keep the
graveyard and exile case as the state-based choice (903.9a).
**Test:** a commander bounced or tucked: the choice comes before it reaches the zone, no shuffle with the commander inside.

### R4. Only one zone-change replacement is applied, in a fixed order — **done**
`MoveCard` is an `if / else if` chain: death replacements, then "exile instead of the graveyard", then the card's own
"shuffle into its library instead", then exiling instants and sorceries. Rule 616.1: when several apply, the affected
object's controller (or owner) chooses one, applies it, and the rest are checked again.
**Fix:** collect the applicable zone-change replacements, let the right player order them (the same helper already used for
counter and token replacements), reapply until none are left.
**Test:** a card with its own "shuffle instead" while "exile instead of the graveyard" also applies: the player chooses.

### R5. "Put the rest on the bottom in any order" is always random — **done**
`LookAtTopTakeAsync` (`Game.Abilities.cs` ~3260) shuffles the rest unless they go to the graveyard, and `RestOnTop` leaves
them as they were. Effects that say "in any order" (bottom) or "put the rest back in any order" (top) must let the player
order them; only "in a random order" is random.
**Fix:** an order option on `LookAtTopTake` (random / chosen, top / bottom) using `OrderAsync`; audit the module's scripts
that use the effect and set it from the card's text.
**Test:** look at three, take one, the player chooses the order of the other two at the bottom.

### R6. A free-cast permission outlives the effect that granted it — **done**
Effects that let a player cast a card during their resolution (`Game.Abilities.cs` ~801, 1034, 1831, 1943, 2560, 2664) add a
`PlayableFromExile` entry "without paying" valid for the whole turn and then call `CastSpellAsync`. When the player backs
out (or the cast fails), the entry stays: the card can be cast for free later that turn, at any time the player could cast
it. Suspend also sets `HasteOnEnter` before the cast and doesn't undo it.
**Fix:** a "cast now" helper that grants the permission only for that cast and always removes it (try/finally), with the
haste rider applied only when the spell is actually cast.
**Test:** decline the free cast; the card is not castable afterwards.

### R7. Casting steps out of order: alternative costs after targets
`CastAsItIsAsync` (`Game.Priority.cs` ~535-620) asks for dash and for "without paying its mana cost" (with X) after targets.
Rule 601.2b: the alternative cost is announced with modes, kicker and splice, before targets (601.2c). It matters when an
alternative cost changes what can be targeted or when targets depend on the total cost.
**Fix:** move every alternative and additional cost choice into the announcement step, before modes and targets.
**Test:** sequence check through the controller calls (announcement before target request).

### R8. Mulligans — **done**
`Game.cs` `ResolveMulliganAsync`: a further mulligan is only offered while at least one card would be kept (rule 103.5
allows going to zero), and each player finishes all their mulligans before the next player starts (rule 103.5: each player
in turn order declares, then everyone who chose to mulligans at the same time, repeated).
**Fix:** allow mulligan to zero; run mulligans in declaration rounds.

### R9. Prevention is always applied before damage doubling
`ModifyDamage` prevents first, then doubles and triples. Today every prevention effect prevents all damage, so the order
doesn't change the result; as soon as a partial prevention ("prevent the next N") exists it will. Rule 616.1: the affected
player or controller chooses the order of replacement and prevention effects.
**Fix:** when adding partial prevention, build the damage pipeline from ordered replacement effects instead of fixed steps.

### R10. Layers: dependency is handled by a fixed two-pass approach
`RecomputeContinuousEffects` works the ability layer out with printed characteristics and again after types and colors
(comment near `Game.Abilities.cs` ~3750). Rule 613.8 dependencies in general (an effect that changes what another applies
to, or whether it exists, inside the same layer) are not detected.
**Plan:** a dependency pass inside each layer (613.8b: dependent effects apply after what they depend on, timestamps
otherwise, loops fall back to timestamps), covered by the classic dependency cases as tests.

## P1 — program

### N1. Getting back into an internet game after restarting doesn't work — **done**
`OnlineService.StartGameClient` saves `LastHostAddress` as `address:port`. For a lobby joined through the internet service the
"address" is the host's online id and the port is 0, so "Get back into the game" (`Rejoin`) tries a TCP connection to it.
**Fix:** save how the lobby was reached (provider and listing) with the seat token; rejoin through the same provider.
**Test:** service test with the in-memory provider: join by code, drop, rejoin from saved settings.

### N2. Hosted internet lobbies stay listed when the game closes — **done**
`EosOnlineServices.Dispose` destroys the lobbies, but nothing disposes the services when the game quits, and `Leave` removes
the listing with an `async void` call that the quitting process doesn't wait for. The listing stays until the service times
the owner out.
**Fix:** dispose the online services on quit (wait a short time for the removals).

## P2 — duplicated paths

### D1. "Can this be cast?" and "what does casting cost?" are two implementations
`CanCast` (`Game.Priority.cs` ~261) rebuilds the cost (delve, dash, extra flash cost, graveyard costs, paying life) separately
from `CastAsItIsAsync`. When they disagree, an action is offered and then can't be paid: that is how the dash bug of the
Commander soak happened, and the bots now carry a guard (`BotController`, backed-out actions) that hides such cases.
**Fix:** one function that builds every possible way to pay for a spell (mana cost, alternative costs, additional costs,
reductions, increases) used both to list legal actions and to cast; the legal-action check becomes "some way is payable".
Remove the bot guard and make the soak tests fail on any backed-out action.

### D2. Tapping and untapping are done in about twenty places — **done**
`Tapped = true/false` appears across `Game.Priority.cs`, `Game.Abilities.cs`, `Game.Combat.cs`, `Game.Turn.cs`,
`Game.Voting.cs`, `Game.Monarch.cs`; most emit `PermanentTapped`/`PermanentUntapped`, some (entering, untap step) on purpose
don't. A new site that forgets the event breaks "becomes tapped" triggers and conditions silently.
**Fix:** `Tap(card)` / `Untap(card)` helpers (event + no-op when already in that state) and the R1 enter-tapped parameter;
nothing else writes the field.

### D3. Card-specific behavior hard-coded in the engine
The `Replacements` bit flags (40 of 64 bits used), many `ObjectFilter` booleans and several `ManaRider` values each stand for
one card's text, with subtypes and numbers written in code (for example the extra counters for three specific creature types
in `ExtraCounterInstances`, a "damaged by your creatures of type X" filter, extra enter counters for one creature type in
`MoveCard`, a "has the activated abilities of lands your opponents control" special case in the layer code). Every new card
like these needs engine code, and the bit field will run out.
**Plan:** generic, parameterized forms: counter replacements with a filter and an amount, enter-with-counters with a
filter and a quantity, "gains the abilities of" as a layer 6 effect with a filter, filters with subtype parameters instead
of one boolean per card. Migrate card by card (their tests must stay green); stop adding new `Replacements` bits.

### D4. Effect resolution is one 4,700-line file
`Game.Abilities.cs` holds the effect switch, conditions, quantities, filters, the layer system and trigger collection.
**Plan:** split by concern (`Game.Effects.*.cs` per family of effects, `Game.Layers.cs`, `Game.Filters.cs`, `Game.Triggers.cs`)
without changing behavior, so reviews and merges stay possible.

### D5. Limited events are run by two orchestrators
Local events (`scripts/Client/LimitedService.cs`) and online events (`src/Arcanum.Net/Events/EventHost.cs`) share
`LimitedEvent` but each drives drafting, deck building and rounds itself (`NextRound` in both).
**Plan:** run local events through `EventHost` with in-memory connections, like local games could run through `GameHost`;
one place for timers, pairing and match results.

## P3 — hygiene

### H1. Real card names in the generic repository — **done**
Against the project rule, the main repository names real cards in identifiers and comments: a local variable in `MoveCard`
(`Game.Zones.cs` ~142), a field and comment in the layer code (`Game.Abilities.cs` ~3756), comments in
`AbilityDefinition.cs` (~472), `Effects.cs` (~477), `Conditions.cs` (~94) and `Game.Priority.cs` (~587); the demo sandbox in
`GameBoard.cs` (~233-247) builds its board from real cards; two data tests use names from a real set's world.
**Fix:** rename to describe the rule, write the comments generically, make the sandbox use generic test cards
(`Arcanum.Cards`), rename the test cards. Add a test that greps the main repository for the module's card names.

### H2. A test hook in the bots hides engine errors
See D1: the backed-out-action guard in `BotController` should go once D1 is done; until then, soak tests should report how
often it fires.

### H3. The online services thread polls every few milliseconds
`EosOnlineServices.Run` loops with `Thread.Sleep(8)` for the whole session even when idle. Fine for now; a wait handle that
wakes on queued work (and a slower tick when nothing is going on) would save battery on mobile (M12).

## Left over from the fixes

### L1. Entering counters with both "plus one" and "twice that many" replacements — **done**
Counters a permanent enters with are on it before it is announced (R2), also when both kinds of counter replacement apply:
`MoveCardAsync` has the controller order them (rule 616.1) right after the move is made and before anything is announced
(`OrderEnterCountersAsync`, sharing `OrderedCounterCountAsync` with the deferred path), and tokens do the same
(`CreateTokenAsync`). Left: a permanent that returns from an "until an opponent becomes the monarch" exile is moved by a
synchronous caller (`ReturnFromMonarchExile`, reached from `LoseAll`), so in that one case its counters are still ordered
right after the current effect.

### L2. A commander drawn and put into the command zone instead — **done**
Decision: it is still a draw and still announced (`CardDrawn`). Rule 903.9b only modifies where the drawn card goes, the
modified event happens instead of the original (614.6), and the player still drew the top card (121.1); no different event
replaces the draw, so it counts for "cards drawn this turn" and "whenever you draw" triggers. The rules text is not
explicit about this case; the choice matches how other engines treat it. Pinned by a test, comment in `DrawOneAsync`.

### L3. The "onto the battlefield instead of being discarded" replacement — **done**
It is now one of the zone-change replacements (`OntoBattlefieldInsteadOfDiscard`, only for a discard from the hand caused
by an opponent's spell or ability), so it is ordered with the others when several apply. A card put onto the battlefield
instead is not discarded (614.6: no `CardDiscarded`); one that goes elsewhere (exile, library) still is.

## Suggested order

1. **Tests first** for R1, R2, R5, R6, N1 (cheap, real bugs).
2. **D2 + R1** (tap helpers and enter-tapped in `MoveCard`), then **R2** in the same place.
3. **R6** (scoped free-cast permission) and **N1/N2**.
4. **D1** (single cost builder), then remove the bot guard (H2) and rerun every soak.
5. **H1** (generic names, sandbox, guard test).
6. **R3, R4, R7, R8** (rules ordering work, each with its own tests).
7. **D4** (split the file, no behavior change), then **D3** migration and **R9/R10** as the engine needs them.
8. **D5** when limited events are next touched.
