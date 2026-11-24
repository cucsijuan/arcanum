# Copying the game for search

Status: all three stages implemented (copy and resume at priority, at the attackers declaration and at the blockers
declaration; the bot copies instead of replaying, and searches its blocks and its instants).

The searching bot (`src/Arcanum.Bots/Simulation/Simulator.cs`) tries each option by playing the game on from that
point. It rebuilds the game for every rollout by replaying every recorded answer from the start (`Recorder` and
`Replayer`). At the copy points this design copies the game at the decision instead, so a rollout starts from a copy and
skips the replay. The replay stays for the other decisions.

## Why

- A rollout costs about 60 ms on a desktop (Release build). Measured later, the replay is a small part of it (copying
  instead saves about 16% on a desktop): most of the time goes into playing the rollout on.
- On the handheld console a rollout costs about 20 times more. One round of search does not finish in the bot's 2 s
  budget: measured, 0 rounds finished. With a 6 s budget, 1.4 rounds of a 2-rollout, 2-turn search finished, about 5 s
  per decision.
- Averaged over the decks of four sets, search with 2 rollouts and a 2-turn horizon wins about 64% of games against the
  plain bot, and a search with 4 rollouts and a 4-turn horizon about 67.5%. Without search it wins about 51.4%.
- So cheaper rollouts are worth the work. Copying removes the replay cost.

## What the game state is

The state that a copy must cover:

- The mutable part of `GameState` is a graph of 9 types: `GameState`, `Player`, `Card`, `ManaPool`, `CombatState`,
  `AttackList`, `AttackInfo`, `RedirectShield` and `AttackPlaneswalkerRequirement`. Together they have about 380 mutable
  members.
- None of them holds a delegate or a reference to a service.
- Card definitions, abilities, effects, mana costs and ids are immutable (init-only). Only their internal caches change.
  A copy can share them.
- `Game` has about 80 instance fields. Most are transient, used within one action.
- Some of the game's position exists only in local variables of the async turn loop:
  - `Game.RunAsync`: the regular turn-order player and the first-turn flag.
  - `Game.RunTurnAsync`: the step list (extra combat phases are inserted into it) and the index into it.
  - `Game.RunPriorityAsync`: the count of consecutive passes.
- A game mode's object (`IGameMode`) keeps its own state outside the game. Games with a game mode are not copied.

## Design

1. **A generic deep copier.** It copies objects field by field, by runtime type, and keeps shared and cyclic references.
   - It shares immutable types: the `Cards`, `Abilities` and `Mana` namespaces (except `ManaPool`), ids, strings and enums.
   - It copies collections exactly, including their internal layout, so later enumeration order matches the original.
   - It refuses, and the caller falls back to the replay, when it meets something it cannot copy safely: a delegate, or
     a hash collection keyed by a mutable object.
   - It is not written field by hand. New fields are copied without any change to the copier.
   - The copiers for the game's own types are generated: `src/Arcanum.Engine/Game.Copiers.g.cs` is written by
     `tests/Arcanum.Engine.Tests/CopierGenerator.cs` from the game's fields, and must not be edited by hand. The test
     `GeneratedCopiersTests.GeneratedCopiersAreCurrent` fails when the file is out of date. Types without a generated
     copier are copied by reflection and counted in `DeepCopier.CopiedByReflection`, which the copy test asserts is zero.
     See [engine-map.md](engine-map.md), *Computer players*, for the commands.

2. **Copy points.** The engine waits at the top of its loop at these points, and the copy is taken there:
   - priority, with an empty stack or not (`Game._waiting` is `ResumePoint.Priority`);
   - before attackers are declared (`ResumePoint.Attackers`);
   - before blockers are declared (`ResumePoint.Blockers`), when blocking is free. With a block tax there is no copy
     point: the attempts to pay would have to be resumed too.

   No copy is taken in the middle of resolving a spell or ability, or in the combat damage or cleanup steps. Those
   decisions use the bot's rules of thumb or the replay.

3. **A turn cursor.** The loop's position moves from local variables into fields:
   - the regular player and the first-turn flag;
   - the step list and the index into it;
   - whether the draw step is skipped;
   - the count of consecutive passes;
   - what the engine is waiting for.

   An entry point resumes a copied game from the cursor. It does not repeat what already happened. It raises no
   step-began or priority-given events, because those would trigger abilities again. A copy taken at priority resumes
   at priority. A copy taken at the attackers declaration asks for attackers again, then the mode step, then priority.
   A copy taken at the blockers declaration asks the defenders again in turn order, from the one it was copied at (the
   defenders before it have declared already), then the mode step, then priority.

4. **The bot.** At a decision the bot copies the real game once. Each rollout copies that copy, imagines the opponent's
   hidden cards (`OpponentModel`) and plays on. A game with a game mode, or a decision that is not at a copy point, falls
   back to the replay.

## Checks

- **Copy and continue.** Bot games are recorded. At each copy point the game is copied, and the copy is continued with
  the same remaining answers. It must end with exactly the same events and state as the original. This runs over every
  deck in the content module. The copy points are counted by kind: at actions, at attackers and at blockers.
- **Independence.** Playing the copy leaves the original unchanged.
- **Engine tests.** The existing engine tests keep passing.

## Stages

1. The copier, the turn cursor, and resuming at priority, with the tests above. Done.
2. Attackers as a copy point. The bot copies instead of replaying. Done.
3. Blockers as a copy point (when blocking is free), and with cheap copies the bot searches its blocks and its instants
   and abilities outside its own main phase. Done.

## Cost

Measured on the module's decks:

| Device | Replay per rollout | Copy (reflection only) | Copy (generated copiers) |
|--------|--------------------|------------------------|--------------------------|
| Desktop | about 60 ms | 5 ms | 3 ms |
| Handheld console | about 20 times the desktop figure | 1.5 s | 66 ms |

Reflection is very slow under ahead-of-time compilation, which is why the generated copiers matter on the console.

## Validation

- Copy and continue (`GameCopyTests`, with `ARCANUM_COPY_CHECK`): 300 games over all module decks, 11383 copies
  (346 of them at the attackers declaration and 144 at the blockers declaration), 2.72 ms per copy. 0 differences in
  events or state, and no aliasing between the copy and the original.
- Search with copies gives the same results as search with replay: with 2 rollouts and a 2-turn horizon on one set's decks,
  the searching bot wins 67.5% of games in both cases.
- With blocks and instants also searched (2 rollouts, 2-turn horizon, guessed opponent cards), the searching bot wins
  73.3%, 81.1%, 77.4% and 67.8% of games on the decks of four sets, 0.9 to 1.4 s per decision on a desktop. On the
  handheld (3 processors, 5 s budget) one game took 45 decisions at 3.6 s each, 5.1 s at most, with 59 ms per copy.
