# Card scripts

Content modules describe what cards do with small JSON files in `scripts/`, one per card, named after the
card's oracle id (`scripts/<oracle-id>.json`). A card needs a script only when its rules go beyond what the
engine derives from card data: type line, power/toughness, supported keywords and mana abilities written as
`{T}: Add {G}` / `{T}: Add {R} or {G}` / `{T}: Add one mana of any color`.

Comments (`//`) and trailing commas are allowed.

Also derived from rules text without a script: `Equip {N}`, `Enchant creature` (and land, artifact,
enchantment, permanent, "... you control"), "This land/creature enters tapped", cycling and "Partner with [name]"
(its enters trigger). Partner, partner with, "Partner—[kind]", friends forever, "Choose a Background" and "Doctor's
companion" only decide which two cards can be commanders together.

## Structure

```json
{
  "spell":     { "targets": [...], "effects": [...], "text": "..." },
  "aura": "creature",
  "entersTapped": false,
  "entersWithCounters": 0,
  "abilities": [
    { "static": { ... }, "text": "..." },
    { "trigger": "enters", "targets": [...], "effects": [...], "text": "..." },
    { "cost": "{1}{R}, {T}", "targets": [...], "effects": [...], "sorcery": false, "text": "..." }
  ]
}
```

- `spell`: what an instant or sorcery does when it resolves.
- `aura`: for an Aura, what it enchants (same values as targets). Usually read from the "Enchant ..." line.
- `entersTapped` / `entersWithCounters`: replacement effects applied as the permanent enters.
- Card-wide rules (all optional): `"uncounterable": true`, `"kicker": "{2}"`, `"flashback": "{1}{R}"`,
  `"ward": "{2}"`, `"wardLife": 2`, `"givesHexproof": true` ("You have hexproof"), `"hexproofFrom": ["B"]`,
  `"playersCantGainLife": true`, `"attacksEachCombat": true`, `"doesntUntap": true`,
  `"additionalCost": { "discard": 1, "sacrifice": filter, "life": 2 }`,
  `"costReduction": { "amount": 3, "if": condition }` (or `"perPermanent": filter` / `"perGraveyardCard": filter`),
  `"entersWithCounters": "X"`. Kicker, flashback, ward and "can't be countered" are also read from the rules text.
- `abilities`: a permanent's abilities. An entry with `static` is a static ability; one with `trigger` is a
  triggered ability; one with `cost` is an activated ability (`"sorcery": true` limits it to sorcery timing).

## Static abilities

```json
{ "static": { "affects": "creatures:you", "other": true, "subtype": "Goblin", "pump": [1, 1], "keywords": ["Haste"] } }
```

| `affects` | Applies to |
|-----------|------------|
| `self` | The permanent itself |
| `creatures:you` | Creatures you control |
| `creatures:opponents` | Creatures your opponents control |
| `creatures` | All creatures |
| `enchanted` | The permanent this Aura is attached to |
| `equipped` | The creature this Equipment is attached to |

`other` excludes the source itself; `subtype` limits it to one creature type. `pump` adds power/toughness and
`keywords` grants keyword abilities while the source is on the battlefield.
- `text`: rules text shown to players for this ability (on the stack, in ability choosers and the log).

## Modal spells and abilities

Instead of `targets`/`effects`, a spell or ability can list `modes`:

```json
{ "spell": { "chooseCount": 1, "upTo": false, "modes": [
    { "text": "Deals 4 damage to target player.", "targets": ["player"], "effects": [{ "damage": 4, "to": "target" }] },
    { "text": "Draw a card.", "effects": [{ "draw": 1 }] } ] } }
```

Each mode's effects refer to its own targets (`"target"` is the mode's first target). Modes without legal
targets can't be chosen.

## Cost reductions

`{ "spellCost": { "spells": { "types": ["instant", "sorcery"] }, "amount": 1 } }` in `abilities`: spells you cast
matching the filter cost that much less (generic mana only).

## Targets

Each entry adds one target, chosen when the spell is cast or the ability is put on the stack. Effects refer
to them as `"target"` (first), `"target2"`, `"target3"`, ...

| Value | Meaning |
|-------|---------|
| `any` | Creature, player or planeswalker |
| `creature`, `permanent`, `artifact`, `enchantment`, `land` | A permanent of that kind |
| `player` | Any player |
| `opponent` | An opponent |
| `spell` | A spell on the stack |
| `<kind>:you` / `<kind>:opponent` | Restricted by controller, e.g. `creature:you` |
| `graveyardCard` / `graveyardCard:you` | A card in a graveyard / in your graveyard |

A target can also be an object: `{ "kind": "permanent", "controller": "opponent", "filter": { "types": ["artifact", "enchantment"] }, "optional": true }`.
`filter` adds requirements (see Filters); `optional` makes it "up to one" (the player may choose none).

## Subjects

Effects say who or what they affect with these values:

| Value | Meaning |
|-------|---------|
| `target`, `target2`, ... | A chosen target (ignored if it became illegal) |
| `you` | The controller of the spell or ability (default for player effects) |
| `opponents` | Each opponent |
| `everyone` | Each player |
| `self` | The source permanent |
| `targetController` | The controller of the first target |
| `targetOwner` | The owner of the first target |
| `triggered` / `triggeredPlayer` | The object / player the trigger event was about ("that creature", "that player") |
| `{ "each": filter }` | Every permanent matching the filter (controller defaults to any) |

## Quantities

Numbers in effects can be `3`, `"X"`, `"-X"`, `"lifeGained"`, `"life"`, `"handSize"`, `"triggerAmount"`
("that much"), `"triggeredPower"`, or `{ "count": filter, "times": 2 }` (permanents), `{ "graveyard": filter }`,
`{ "attacking": filter }`, `{ "power": "self" | "target" }`, `{ "toughness": "target" }`, `{ "manaValue": "target" }`.

## Effects

| Effect | Example |
|--------|---------|
| Damage | `{ "damage": 3, "to": "target" }` |
| Draw cards | `{ "draw": 2 }` / `{ "draw": 1, "who": "target" }` |
| Gain / lose life | `{ "gainLife": 3 }` / `{ "loseLife": 2, "who": "opponents" }` |
| Destroy | `{ "destroy": "target" }` |
| Exile | `{ "exile": "target" }` |
| Return to hand | `{ "bounce": "target" }` |
| Tap / untap | `{ "tap": "target" }` / `{ "untap": "self" }` |
| Mill | `{ "mill": 3, "who": "target" }` |
| Counter a spell | `{ "counter": "target" }` |
| Until end of turn | `{ "pump": [3, 3], "what": "target", "keywords": ["Trample"] }` |
| Counters | `{ "counters": 1, "what": "self" }` / `{ "counters": 1, "what": "target", "kind": "-1/-1" }` |
| Attach this Aura/Equipment | `{ "attach": "target" }` |
| Tokens | `{ "tokens": 2, "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1, "keywords": [], "colors": ["W"] } }` |
| Predefined tokens | `{ "tokens": 1, "token": "Treasure" }` (also `"Food"`, `"Clue"`) |
| Scry / surveil | `{ "scry": 2 }` / `{ "surveil": 1 }` |
| Fight | `{ "fight": "target", "with": "target2" }` (or `"fight": "self"`) |
| Discard (the player chooses) | `{ "discard": 1, "who": "opponents" }` |
| Only if a condition holds | `{ "if": "raid", "then": [ ... ], "else": [ ... ] }` |
| Optional | `{ "may": "Draw a card?", "effects": [ ... ] }` |
| Put onto the battlefield | `{ "reanimate": "target", "tapped": true }` (under your control; `"ownerControl": true` for its owner's) |
| Search your library | `{ "search": filter, "count": 1, "to": "hand" \| "battlefield" \| "graveyard" \| "top", "tapped": true }` |
| Sacrifice | `{ "sacrifice": 1, "filter": { "types": ["creature"] }, "who": "opponents" }` |
| Top or bottom of library | `{ "toLibrary": "target" }` (its owner chooses) / `"bottom": true` |
| Gain control | `{ "gainControl": "target", "untilEndOfTurn": true }` |

## Triggers

| Value | When |
|-------|------|
| `enters` | This permanent enters the battlefield |
| `dies` | This creature goes from the battlefield to a graveyard |
| `attacks` | This creature attacks |
| `upkeep` | At the beginning of your upkeep |
| `endStep` | At the beginning of your end step |
| `combatDamageToPlayer` | This creature deals combat damage to a player |
| `blocks` / `attacksOrBlocks` | This creature blocks / attacks or blocks |
| `creatureEnters` | A creature matching `filter` enters (default: a creature you control) |
| `creatureDies` | A creature matching `filter` dies |
| `landfall` | A land you control enters |
| `gainLife` | You gain life |
| `castSpell` | You cast a spell matching `filter` |
| `beginCombat` | At the beginning of combat on your turn |
| `youAttack` | You attack with one or more creatures |
| `opponentCastsSpell` | An opponent casts a spell matching `filter` |
| `creatureAttacks` | A creature matching `filter` attacks |
| `creatureCombatDamageToPlayer` | A creature matching `filter` deals combat damage to a player |
| `opponentLosesLife` | An opponent loses life |
| `draw` | You draw a card |
| `countersPlaced` | +1/+1 counters are put on a creature matching `filter` (`"onSelf": true`: on this one) |
| `becomesTapped` | This permanent becomes tapped |
| `eachUpkeep` / `eachBeginCombat` / `eachEndStep` | At the beginning of each upkeep / combat / end step |

`"nth": 2` limits a trigger to the Nth such event of the turn ("your second card each turn").

A trigger can have an intervening condition, `"if": ...`: it triggers only if the condition holds, and does
nothing on resolution unless it still holds.

### Filters

```json
{ "types": ["instant", "sorcery"], "not": ["creature"], "subtype": "Elf", "controller": "you", "other": true, "minPower": 4, "token": false }
```

Also: `maxPower`, `minToughness`, `minManaValue`, `maxManaValue`, `colors` (any of), `keyword` / `without`,
`tapped`, `inCombat` (attacking or blocking), `attacking`, `blocking`, `multicolored`, `colorless`, `enchanted` (an Aura is attached), `equipped`, `commander`, `supertype` (`"basic"`), `notSubtype`.

Every field is optional. `types` matches any of the listed card types, `not` excludes types, `controller` is
`you` (default), `opponent` or `any`, `other` excludes the source itself and `token` limits to (or excludes)
tokens.

## Conditions

| Value | True when |
|-------|-----------|
| `"raid"` | You attacked this turn |
| `"morbid"` | A creature died this turn |
| `"gainedLife"` / `{ "gainedLife": 3 }` | You gained (at least that much) life this turn |
| `"threshold"` / `{ "graveyard": 7 }` | That many cards are in your graveyard |
| `"ferocious"` | You control a creature with power 4 or greater |
| `{ "control": filter, "count": 2 }` | You control at least that many permanents matching the filter |
| `{ "life": 10 }` | You have at least that much life |
| `{ "not": condition }` | The condition is false |
| `"kicked"` | The spell (or the permanent, as it entered) was kicked |
| `"opponentLostLife"` | An opponent lost life this turn |
| `"yourTurn"` | It's your turn |
| `{ "counters": 3 }` | The source has at least that many +1/+1 counters |

## Costs

Comma-separated: mana symbols (`{2}{R}`, `{X}`), `{T}` (tap this permanent), `sacrifice` (sacrifice this
permanent), `sacrifice:creature` (sacrifice another creature), `discard`, `discard:2`, `life:2`,
`removeCounters:3` (+1/+1 counters from this permanent) and `exileFromGraveyard` (the ability is activated
from your graveyard, exiling this card). An activated ability can add `"oncePerTurn": true` and
`"activateIf": condition`.

## More vocabulary

### Card-wide rules
`hexproofFromTypes`, `additionalCostOptions` (`[{ "cost": extra }, { "mana": "{3}{B}" }]`, pay one), `alternativeCost`
(`{ "cost": "{B}", "if": condition }`), `flashExtraCost`, `startsOnBattlefield` (from the opening hand),
`manaRider` (`HasteForDragonCreatureSpells`, `CopyRedInstantOrSorcery`), `manaOnlyFor` (filter: spells its mana can pay
for), `extraMana` (`[{ "types": "any", "amount": 1, "onlyFor": filter, "abilitiesToo": true }]`), `tapForMana`,
`manaAmount`, `manaAmountFrom`, `manaFromChosenColor`, `chooseOnEnter` (`color`, `creatureType`, `cardName`),
`countersPerChosenType`, `entersWithCounterKind`, `entersWithCountersIf`, `graveyardCastCost`, `powerFrom` /
`toughnessFrom` (quantities), `cantBeBlockedBy` (filter), `ontoBattlefieldIfDiscarded`, and `replaces`: a list of
`DoubleDamageToOpponents`, `DoubleCreatureDamage`, `DoubleTokens`, `DoubleCounters`, `PreventCombatDamageToAndBySelf`,
`PreventNoncombatDamageToYourOtherCreatures`, `ExtraLifeGain`, `YouCantLose`, `ExileInstantsAndSorceries`,
`OpponentsCreaturesEnterTapped`, `ShuffleIntoLibraryInsteadOfGraveyard`, `YourSpellsHaveFlash`,
`YourInstantsAndSorceriesCantBeCountered`, `NoMaximumHandSize`, `CastFromHandFree`, `CreaturesFromLibraryTop`,
`PlayStashedCards`, `PermanentsFromGraveyard`, `StopsChosenNameAbilities`, `AngelsEnterWithCounters`,
`AdditionalLandPlay`.

### Static abilities
Besides `affects`/`pump`/`keywords`: `filter`, `while` (condition), quantities in `pump`, `addSubtypes`, `addChosenType`,
`addTypes`, `grants` (abilities), `losesAbilities`, `setPower`, `setToughness`, `setTypes`, `setSubtypes`, `setColors`,
`setName`, `grantsMana` / `grantsManaAmount`, `givesControl`. Scope `permanents:you` affects all your permanents.

### Targets and spells
Target objects also take `anyNumber` (last requirement only), `attachedToTarget: "target"`, `singleTarget` (stack objects)
and `controlledByTriggeredPlayer`. Kinds: `planeswalker`, `creatureOrPlaneswalker`, `playerOrPlaneswalker`,
`spellOrAbility`. A spell or ability can set `targetRule` (`allDifferent`, `differentControllers`, `sameGraveyard`),
`whenKicked` (a replacement spell definition used when kicked), `modesOnce`, and a spell `exileAfter`.
Subjects: `eachTarget`, `granter` (in granted abilities: who granted it), `granterPermanent`.

### Costs and triggers
Costs: loyalty `+1` / `-3`, `addCounters:1:page`, `removeCounters:3:incubation`, `returnToHand`, `exile`, `tapGranter`,
`tapCreatures:10:Elf`, `crew:3`. Abilities: `onlyOnce`. Triggers also: `selfSacrificed`, `noncombatDamageToOpponent`,
`creatureCombatDamage`, `dealsCombatDamage`, `opponentDiscards`, `becomesUntapped`, `opponentDraws`, `eachDrawStep`,
`anyPlayerCastsSpell`, with `targetsSource`, `fromGraveyard`, `counterKind`, `oncePerTurn`.

### Effects
`become`, `exileUntilLeaves`, `flicker`, `copy`, `sacrificeIt`, `exileIfDies`, `preventCombatDamage`, `lookAtTop`,
`discardChosen`, `exileGraveyard`, `doubleCounters`, `removeCounters`, `shuffleGraveyard`, `addMana`,
`addManaAnyColor`, `addManaUntilEndOfTurn`, `bite`, `reanimateAll`, `bounceAll`, `mayPay`, `mayPayX`,
`returnFromGraveyard`, `discardHand`, `millUntil`, `revealUntil`, `emblem`, `exileTopPlayable`, `divideDamage`,
`distributeCounters`, `keepOneOfEachType`, `destroySameName`, `unless`, `opponentMaySacrifice`, `piles`, `winGame`,
`loseGame`, `untapUpTo`, `poison`, `endTurn`, `additionalCombat`, `copySpell`, `copyNextInstantOrSorcery`,
`castFromLibraryTopsFree`, `returnExiledWithThis`, `searchExileWithThis`, `grantFlashback`,
`castFromGraveyardThisTurn`, `noMaxHandSize`, `returnNextEndStepOneFewer`, `destroyManaValueXDamaged`, `changeTarget`.
Quantities also: `sacrificedToughness`, `lifeLostThisWay`, `destroyedThisWay`, `excessDamage`, `triggeredColors`,
`opponentsGraveyards`, `greatestOtherPower`, `{ "milled": f }`, `{ "exiled": f }`, `{ "distinctManaValues": f }`,
`{ "spellsCast": f, "offset": -1 }`, `{ "counters": "soul" }`. Conditions also: `castFromHand`, `youSacrificed`,
`triggeredWasAttacking`, `{ "targetLifeExactly": 10 }`, `{ "xAtLeast": 10 }`, `{ "sourceWas": "Demon" }`,
`{ "sourceHadCounters": "revival" }`, `{ "differentNames": f, "count": 10 }`, `{ "resolvedThisTurn": 2 }`,
`{ "triggeredCounters": 3 }`, `{ "targetAttachedTo": "target2", "to": "target" }`, `{ "sourceIs": f }`,
`{ "atLeast": quantity, "value": 1 }`, `{ "lifeAboveStarting": 10 }`, `{ "totalPower": 8 }`, `{ "attackers": 3 }`.

### Exact timing
- A trigger's `"when"` is part of the trigger event ("whenever you attack with three or more creatures", "attacks
  while you control …"): checked only when the event happens. `"if"` is an intervening "if", checked again on
  resolution.
- `{ "whenYouDo": { "if": "createdThisWay", "targets": […], "effects": […], "text": "…" } }` creates a reflexive
  triggered ability once the effect before it happened; it chooses its own targets when it goes on the stack.
- `{ "doubleCounters": "target", "kind": "+1/+1" }` doubles one kind of counter (without `kind`: every kind).
- `{ "spellsCastBefore": f }` counts the spells you cast this turn before the spell that triggered the ability.
- `lookAtTop` takes `revealAll` ("reveal the top X cards") and `restAnyOrder` ("in any order": the player orders the cards
  left over, on the bottom or, with `"rest": "top"`, back on top; without it they go to the bottom in a random order).
- Copies of a token (`copy`) with `sacrificeAtEndStep` get the ability "At the beginning of the end step, sacrifice
  this token" as part of the copy.

## Adventures, Sagas and other card frames

- **Adventurer cards** (layout `adventure`): the script describes the card itself as usual and its Adventure under
  `"adventure": { "spell": { … }, "additionalCost": … }` (a script of its own). The card can be cast as its
  Adventure; once that spell resolves the card is exiled and its owner may cast the card itself from exile. A
  countered Adventure goes to the graveyard.
- **Sagas** (layout `saga`): chapter abilities are triggered abilities with `"trigger": "chapter", "chapters": [3, 4]`.
  A Saga enters with a lore counter, gets one as its controller's precombat main phase begins, and is sacrificed
  once it has as many lore counters as its last chapter and no chapter ability is waiting or on the stack.
- **Cycling and typecycling** (`Cycling {2}`, `Mountaincycling {2}`, …) are read from the rules text: activated from
  the hand by discarding the card.

## More effects

| Effect | Example |
|--------|---------|
| Amass | `{ "amass": 2, "type": "Goblin", "who": "targetController" }` (count can be a quantity) |
| Recruit | `{ "recruit": true }` (draw, discard, a 1/1 white Human Soldier if a nonland card was discarded) |
| Attach something | `{ "attach": "created", "to": "target" }`, `{ "attach": { "choose": filter }, "to": "target" }` (chosen, optional) |
| Cards milled this way to hand | `{ "takeMilled": filter }` (all) / `{ "takeMilled": filter, "count": 2 }` (up to) |
| Remove every counter | `{ "removeAllCounters": "attached" }` |
| Exile and return at once | `{ "blink": "eachTarget" }` |
| Shuffle into its owner's library | `{ "shuffleIntoLibrary": "self" }` |
| Play an additional land | `{ "additionalLand": true }` |
| Players can't cast spells this turn | `{ "noSpellsThisTurn": true }` |
| Exchange control | `{ "exchangeControl": "target", "with": "target2" }` (with `"targetRule": "shareCardType"`) |
| At the beginning of the next upkeep | `{ "atNextUpkeep": { "effects": [ … ] }, "amount": "returned" }` (`"triggerAmount"` there) |
| Choose a creature type | `{ "chooseType": true }` (then filters with `"chosenType": true`) |
| Random card from the top | `{ "revealTopRandom": 13, "filter": f, "to": "battlefield" }` |
| Search hand and library | `{ "searchHandOrLibrary": f, "to": "battlefield" }` |
| Mana in any combination of colors | `{ "addManaCombination": 4, "onlyFor": filter }` |
| Behold | `{ "behold": filter, "effects": [ … ] }` (choose one you control or reveal one from your hand; if you do, …) |
| Cast from your graveyard now | `{ "castFromGraveyard": filter }` (an instant or sorcery cast this way is exiled afterwards) |
| Prevent all damage it would deal | `{ "preventDamageBy": "target" }` (while the source stays on the battlefield) |

Existing effects take more options: `counter` (`"unlessPays": "{4}"`, `"exilePermanentPlayable": true`), `search`
(`"count"` as a quantity, `"split": true` for one onto the battlefield tapped and the rest into the hand), `reanimate`
and `reanimateAll` (`"attachTo"`, `"setTypes"`, `"setSubtypes"`, `"abilities"`), `lookAtTop` (`"tapped"`,
`"rest": "shuffle"`), `exileTopPlayable` (a quantity, `"of": "target"`, `"payLife"`, `"forever"`, `"faceDown"`,
`"while"`), `revealUntil` (`"battlefieldIf": filter`), `copy` (`"notLegendary"`), `become` (`"powerFrom"`,
`"toughnessFrom"`, `"continuous"`, `"whileSource"`), `pump` (`"whileSource"`), `returnExiledWithThis` and
`searchExileWithThis` (`"count"`), `emblem` (`"untilEndOfTurn"`), and `whenYouDo` (`"about"`: what "that creature" is,
`"amount"`: worked out as the reflexive ability is created).

Subjects also: `created`, `found` (cards found by a search), `amassed`, `{ "discarded": filter }`,
`{ "each": filter, "controlledBy": "target" }`, `{ "each": filter, "exceptTargets": true }`.
Quantities also: `{ "discarded": f }`, `{ "graveyardsWith": 7 }`, `"manaSpent"`, `"returned"`, `{ "power": "attached" }`,
`{ "toughness": "self" }`, `{ "count": f, "controlledBy": "target" }`.
Conditions also: `"enduringStory"`, `"castFromGraveyard"`, `"giftPromised"`, `{ "targetControlledByYou": "target" }`,
`{ "drawn": 2 }`, `{ "attackingPower": 12 }`, `{ "resolvedThisTurn": 2, "exactly": true }`.
Filters also: `inHand`, `fromBattlefieldThisTurn`, `paidWithTreasure`, `chosenParity`, `sharesNameWithYourLegendary`.

## More triggers, abilities and card-wide rules

Triggers: `permanentEnters` (any permanent matching `filter`), `leavesGraveyard`, `precombatMain`, `putIntoGraveyard`
(any permanent), `becomesTarget` (of an opponent's spell or ability), `activateAbility` (you activate an ability of a
`filter` source), `playerLosesLife`, `youSacrifice`, `creatureExiledInstead` (with the replacement below), `chapter`.
Trigger options: `"batched": true` ("whenever one or more …"), `"counterKind": "any"`, and `"nth"` also counts
another player's draws (`opponentDraws`) and spells (`opponentCastsSpell`).

Activated abilities: `"equip": true` marks equip abilities written in a script (`Equip—{2}, Pay 2 life`, `Equip Wizard
{1}`), `"costReductionPer": filter`, `"discardFilter": filter` (the discarded card must match). Costs:
`sacrifice:artifact|creature`, `sacrifice:Goblin` (another permanent with that subtype).

Static abilities: `"grantsWard": "{1}"`, `"extraTriggers": true` (triggered abilities of affected permanents trigger an
additional time), `"graveyardAbilities": filter` (has the activated abilities of matching cards in your graveyard).
`spellCost` also takes a quantity as `amount`, `"firstOfTurn"` and `"grantsFlash"`.

Card-wide rules: `"gift": "Treasure"` (or a token), `"flashIf": condition`, `"attackTax": "{1}"` with `"attackTaxIf"`,
`"additionalLandPlayIf"`, `"entersTappedUnless"`, `"equipDiscount": 2`, `"freeFirstEquipIf"`, `"manaOnlyForAbilitiesToo"`,
`"chooseOnEnter": "oddOrEven"`, `costReduction` with `"powerFilter"`, and `replaces` also `DrawTwoExceptFirstInDrawStep`
and `OpponentsCreaturesExiledInsteadOfDying`. Storied is a keyword: with three or more artifacts, legendaries and/or
Sagas, its controller has an enduring story for the rest of the game.

Counters with rules of their own: hone counters on an Equipment give the equipped creature +1/+0; trample counters give
trample.

## The Ring, phasing, cascade and evasion

- `{ "ringTempts": true }`: the Ring tempts you; you choose a Ring-bearer. With 1 or more temptations it can't be
  blocked by creatures with greater power; 2: whenever it attacks, draw then discard; 3: creatures blocking it are
  sacrificed at end of combat; 4: when it deals combat damage to a player, each opponent loses 3 life. Trigger
  `ringTempts` ("whenever the Ring tempts you"), quantity `"ringLevel"`.
- `{ "phaseOut": subject }`: phases out until its controller's next untap step (with what's attached to it).
- `"cascade": 2` (card-wide): cascade instances. `"minimumBlockers": 3`: can't be blocked except by that many creatures.
- Keywords `Islandwalk` (and the other landwalks), `Shadow`, `Ascend` (`"citysBlessing"` condition). Indestructible,
  lifelink and shadow counters give their keyword (a shadow counter also makes it a Wraith).
- `{ "playerProtection": true }`: you have protection from everything until your next turn.

More effects: `{ "castFromHandFree": filter, "maxManaValue": quantity }`, `{ "putFromHand": filter }`,
`{ "destroyAllBut": filter, "keep": 2 }`, `{ "handToBottom": 1 }`, `{ "tapAnyNumber": filter }` (then `"tapped"`),
`{ "opponentChooses": "prompt", "yes": [ … ], "no": [ … ] }`, `{ "castCopy": "target" }` (exile the card, copy it and
cast the copy free); `revealUntil` with `"from": "triggeredPlayer", "castFree": true`; `exileGraveyard` with
`"filter"` and `"playable"` (castable with mana of any type); `addMana` with `"times"`; `blink` with `"tapped"`; `pump`
with `"loseKeywords"`; `tokens` with `"attacking"`.
Triggers: `dealtNoncombatDamage`, `becomesBlocked`, `youScry`, `combatDamageToYou`, `finalChapterResolved`; option
`"exceptFirstInDrawStep"`. Abilities: `"modesOncePerTurn"`, `"extraModeIf"`, activated `"costReductionIf"` with
`"costReductionAmount"`. Card-wide: `"wardCost"` (a non-mana ward cost), `"othersEnterWithCounters"`,
`"chooseOnEnter": "payLifeOrTapped"` with `"enterLife"`, `costReduction` with `"amountFrom"`, `replaces`
`FoodAlsoTreasure` and `CastCreaturesFromLibraryTop`, `manaRider` `LegendaryUncounterable`. Costs:
`sacrifice:legendary artifact`. Quantities: `"greatestPower"`, `"greatestToughness"`, `{ "greatestAmongOpponents": f }`,
`{ "countersAmong": f, "kind": "lore" }`, `"otherSpellsManaValue"`, `"milledManaValue"`, `"tapped"`,
`{ "count": f, "controlledBy": "triggeredPlayer" }`. Conditions: `"opponentHasMostLife"`, `{ "attackersExactly": 1 }`,
`{ "attackedWith": 2 }`. Filters: `maxToughness`, `notChosenType`, `leastPower`, `damagedThisTurn`, `blockingSource`,
`dealtCombatDamageToYou`, `maxManaValueTriggerAmount`.

## Mana abilities with choices and costs, linked abilities

- `extraMana` entries also take `"combination": true` ("add two mana in any combination of …": the player chooses
  each mana; the payment screen offers the combinations), `"lifeCost": 1` ("{T}, Pay 1 life: Add …") and `"rider"`
  (a mana rider for that ability only, e.g. `LegendaryUncounterable`).
- `{ "exile": "target", "linked": true }` with a `"leaves"` trigger doing `{ "returnLinkedExiled": true }`: the
  two linked abilities of "exile … / when this leaves the battlefield, return the exiled card" (rule 607). If the
  permanent left before its exile ability resolved, the card stays exiled.
- `{ "cantBlockThisTurn": filter }`: a rules effect for the turn (creatures that arrive later are affected too).
- `{ "unblockableByMostLifePlayer": "target" }`: choose a player with the most life or tied (you included); the
  creature can't be blocked by creatures that player controls this turn.
- Conditions `{ "triggered": filter }` (the object the trigger was about, checked on resolution); subject
  `{ "damagedThisWay": filter }`; quantity `"attached"` (Auras and Equipment attached by this effect).
- The free first equip (`freeFirstEquipIf`) is offered as a choice. Attack taxes are paid as attackers are declared;
  an unpaid declaration is made again, and players see what attacking costs.

## Ring-bearers, keyword counters, lasting control and more

- **The Ring**: trigger `ringBearerChosen` ("whenever you choose a creature as your Ring-bearer"); a `ringTempts` trigger is
  about the creature chosen that time, so `"if": { "triggered": { "other": true } }` reads "if you chose a creature other
  than this". Conditions `"ringBearer"` (this is your Ring-bearer) and `"hasRingBearer"`; quantity `"ringBearerPower"`.
  `ringTempts` and `youScry` triggers also work from the graveyard (`"fromGraveyard": true`).
- **Counters**: keyword counters `first strike`, `double strike`, `deathtouch`, `flying`, `haste`, `hexproof`, `menace`,
  `reach`, `vigilance` (and `verse`). `{ "counterChoice": ["first strike", "vigilance"], "what": "self" }` puts the chosen
  one; `{ "countersOfTriggeredKinds": "target" }` puts one of each kind a batched `countersPlaced` trigger saw.
- **Durations**: `pump` and `gainControl` take `"whileYouControl": true` ("for as long as you control this Saga/creature");
  `pump` takes `"untilYourNextTurn": true`.
- **Choices on resolution**: `{ "chooseEffect": [{ "text": "Lifelink", "effects": [ … ] }, …] }`; `mayPay` takes
  `"options"` (pay one of several costs) and `"else"`; `may` takes `"oncePerTurn"` ("do this only once each turn").
- **Library**: `{ "toLibrary": "target", "position": 2 }` (second from the top), `{ "revealTop": filter, "optional": true,
  "effects": [ … ] }` (the revealed card is `"found"`), `revealUntil` with `"count"`, `"tapped"` and `"rest": "graveyard"`,
  `{ "piles": 4, "opponentSeparates": true }`, `{ "guessTop": 2, "right": [ … ], "wrong": [ … ] }`,
  `{ "revealTopPutAny": quantity, "filter": f }`, `lookAtTop` with a quantity.
- **Damage**: `damage` with `"excessToController": true`, `{ "damageCantBePrevented": true }`; triggers
  `excessNoncombatDamage` (amount: the excess) and `dealsDamageTo` (this creature dealt damage to a `filter` creature).
- **Combat**: `{ "goad": "target" }`, `{ "removeFromCombat": "self" }`, keywords `Must be blocked`, `Can't be blocked by more
  than one`, `Nonbasic landwalk`, `Assigns damage by toughness`, `Untaps by removing counter`; condition
  `{ "equippedInCombatWith": filter }`; trigger `equippedBlocksOrBlocked` with `{ "loseAllAbilities": "triggered" }`.
- **Protection**: `{ "protectionFromChosenType": "target" }`, `{ "protectionFromColorsOf": "target", "what": subject }`; card-wide
  `"protectionFromSubtypes": ["Demon", "Dragon"]` (protection from creature types: damage, enchanting/equipping, blocking and
  targeting by sources with any of them, as they last existed).
- **Triggers**: `tokenCreated` (each token), `youAttackPlayer` (once per player attacked; amount: how many attackers matched
  `filter`), `becomesTargetOfSpell`, `permanentBecomesTarget` (of an opponent's spell or ability), `phasesIn`; cast triggers
  take `"spellTargets": filter`.
- **Targets**: `"upTo": quantity` repeats a requirement ("up to X target creatures"), `targetRule: "sameController"`, kind
  `spellOrPermanent`, filters `historic`, `lesserPower`, `greaterPower`, `powerIsX` (X is announced first),
  `maxPowerTriggered`, `blockedOrBlockedByLegendary`, `sharesColorWithYourLegendary`, `noSharedCreatureType`,
  `damagedThisTurnByYourSpider`; subject `{ "each": f, "attachedTo": "target" }`.
- **Conditions**: `{ "yourCreaturesDied": 1 }`, `{ "sacrificedThisTurn": filter }`, `{ "sacrificed": filter }` (the
  sacrificed creature), `"yourPermanentLeft"`, `"attackedThisTurn"`, `"greatestPower"`, `"triggeredPlayerAttackedYou"`.
- **Quantities**: `{ "sum": [ … ] }`, `"permanentsSacrificedThisTurn"`, `"sacrificedThisWay"`, `"amassedPower"`,
  `{ "graveyard": f, "of": "target" }` / `"of": "affected"` (the player being affected).
- **Delayed and remembered**: `{ "atNextEndStepOf": "opponents", "effects": [ … ] }` (chooses an opponent; triggers at their
  next end step), `copy` with `setPower`, `setToughness`, `setColors`, `setTypes`, `setSubtypes`, `addKeywords`,
  `abilities`, `tapped`, `attacking`, `atNextEndStep` and `atNextEndStepUnless`; `exileTopPlayable` with `"whenPlayed"`;
  `{ "noteCreatureType": true }`; `{ "moveCounterOfEachMissingKind": … }`, `{ "moveCounters": …, "then": [ … ] }`;
  `{ "sacrificeAnyNumber": filter }`; `{ "exileHandDownTo": 4, "who": "targetController" }`.
- **Costs**: `sacrifice:Food*3` (three Foods), `exileGraveyardCards:3`. Abilities: `{ "abilityCost": { "sources": filter,
  "amount": 1, "equipOnly": true } }` ("activated abilities of Foods you control cost {1} less", "equip abilities you
  activate cost {1} less").
- **Card-wide**: `"opponentsCantGainLife"`, `"cantBeCopied"`, `extraMana` with `"colorsAmongGraveyardLegends"`, and
  `replaces` `ExtraFoodWithTokens`, `DrawTwoWithEmptyHand`, `DoubleLifeGainAtFiveOrLess`, `KeepGreenMana`,
  `ExtraCounterOnArmiesGoblinsOrcs`, `PreventDamageToSelfDuringYourTurn`, `EquipAtInstantSpeedOnYourTurn`,
  `ExtraTriggersFromLegendariesAndArtifactsMoving`, `AnyManaForItsAbilities` (also: it has the activated abilities of
  opponents' lands, which can't be activated unless they're mana abilities).
- A legendary instant or sorcery can be cast only with a legendary creature or planeswalker. "A deck can have up to nine
  cards named …" raises the copy limit; enters abilities that refer to X use the X the permanent was cast with.
- `{ "simultaneously": [ … ] }`: several effects that are one event ("put a +1/+1 counter and a lifelink counter on it",
  "each deal damage"), so "one or more" triggers see it once.
- Replacement effects on tokens and counters ("twice that many", "plus an additional Food", "a Food and a Treasure",
  "that many plus one") each apply once per permanent that has them; when more than one kind applies, the affected
  player chooses the order (rule 616.1). Damage doublers stack the same way. Quantities about a target that changed zones
  use the object as it last existed (a spell returned to hand keeps the mana value it had with its X).

## Combat requirements and restrictions

- Keywords (give them with a static `"keywords"`): `Can't attack alone`, `Can't block alone` (rule 506.5: only together with
  another attacking / blocking creature), `Can block any number of creatures` (it divides its combat damage among the attackers
  it blocks as its controller chooses, rule 510.1d), `All creatures able to block it do so` (also `Lure`: each creature able to
  block it has a requirement to block it). Blocks and attacks must obey as many requirements as possible without breaking a
  restriction (rules 508.1d, 509.1c), worked out exactly together with menace, "can't be blocked by more than one", "must be
  blocked", "attacks each combat", goad and the effects below.
- Card-wide `"cantAttackUnlessDefenderControls": filter` ("can't attack unless defending player controls an Island"): it can't
  attack a player who controls none, nor that player's planeswalkers.
- `{ "attacksYouThisTurn": "target" }`: the creature attacks the controller of this effect this turn if able (only attacking
  that player obeys it; never paid for, never against a restriction).
- `{ "skipNextUntap": subject }`: it doesn't untap during its controller's next untap step; with `"player": "target"` during
  that player's next untap step instead (Sleep). Either way the effect ends with that untap step.
- Triggers `blocksOrBlockedBy` ("whenever this creature blocks or becomes blocked by a creature") and `blocksCreature`
  ("whenever this creature blocks a creature"): once per creature on the other side, which is `"triggered"`. `blocks` triggers
  once however many creatures it blocks.
- `{ "tapAllToDamage": filter, "to": "target" }` (Master of the Wild Hunt): taps all your untapped permanents matching the
  filter; each one tapped this way deals damage equal to its power to the target creature, which deals damage equal to its
  power divided as its controller chooses among them, all at once.
- Filter `"toughnessLessThanPower": true`: toughness less than the source's power (last known if it left the battlefield).
- An Aura's `aura` target with a filter (`{ "kind": "creature", "filter": { "tapped": true } }`, "enchant tapped creature") is
  checked as it is cast and all the time it is attached: once its object no longer matches, the Aura is put into its owner's
  graveyard (rules 303.4d, 704.5m).

## Multiplayer and commander rules

- **The monarch** (rule 724): `{ "becomeMonarch": "you" | "target" | "triggered" }` ("its controller becomes the monarch");
  `{ "exileUntilOpponentMonarch": "target" }`; conditions `"monarch"`, `"noMonarch"`; trigger `monarchEndStep` ("at the beginning
  of the monarch's end step", the monarch is `"triggeredPlayer"`); static `"givesControlToMonarch": true`. The monarch's own
  triggered abilities (draw at the end step, losing it to combat damage) are built in, and a player tag shows who has it.
- **Attack restrictions**: static `"cantAttackYou": true`, effects `{ "cantAttackYouThisCombat": "triggeredPlayer" }`,
  `{ "cantAttackPlayerThisTurn": "target" }`, `{ "playerMayPay": quantity, "who": subject, "ifNot": [ … ] }` ("may pay {X}. If they
  don't, …"); card-wide `"cantAttackIfPowerAboveHandSize"`. The keyword `Attacks each combat` can be granted.
- **Voting** (rule 701.38): `{ "vote": ["option a", "option b"], "secret": true }`, or `"vote": "player"` / `"vote": "creature"` (with
  `"filter"`). After it: condition `{ "moreVotes": 0 }` (strictly more votes than every other option), quantity `{ "votes": 1 }`,
  `"votesReceived"` (for the player being affected), `"opponentsVotedOtherwise"`, subjects `opponentsWhoVotedWithYou` and
  `youAndOpponentsWhoVotedWithYou`, effects `{ "stunVoted": true }` and `{ "votersGiveCreatures": 0 }`. Trigger
  `playersFinishVoting` (it knows every vote).
- `{ "eachPlayer": [ … ], "who": subject, "onlyIf": condition }`: each player does the effects as "you"; with a single `may`, every
  player decides first. `{ "repeat": quantity, "effects": [ … ] }` ("for each …, do this"). `{ "chooseOpponent": true }` then subjects
  `chosenPlayer` / `youAndChosenPlayer`; `{ "choose": filter, "chooser": subject }` then subject `chosen`.
- **Mana**: `extraMana` entries take `"commanderIdentity"`, `"opponentsLands"` (any color a land an opponent controls could produce),
  `"yourLands"` (any type a land you control could produce), `"damage": 1`, `"gainLife": 1`, `"while": condition`, and the riders
  `Uncounterable`, `InstantOrSorceryUncounterable`, `ScryIfSharesTypeWithCommander`. `{S}` in activation costs is paid with mana from
  a snow source. A land given a basic land type has its mana ability.
- **Entering**: `"chooseOnEnter": "revealOrTapped"` with `"enterReveal": filter`; `"entersTappedUnless": { "opponents": 2 }`;
  `"startsOnBattlefield"` with `"startsIfNotStartingPlayer"`, `"startsWithCounter"`, `"startsExilingFromHand"`;
  `"graveyardEnterBonus": filter` (while this card is in your graveyard, those enter with an additional +1/+1 counter).

## Casting

Card-wide: `"multikicker"`, `"replicate"`, `"squad"`, `"dash"`, `"splice"`, `"miracle"` (costs), `"storm"`, `"undaunted"`, `"delve"`,
`"conspire"`, `"payXLife"`, `"entersWithXCountersTimes": 2`, `"flashbackExtra"` (non-mana flashback costs), `"flashbackExilesX"`,
`"flashbackReduction"` (quantity), `"devour": 3` with `"devourFilter"`, `"exert": true` with `"exertIf"`, `"cantBeSacrificed"`.
Split cards describe their halves under `"split": [ { … }, { … } ]`; an aftermath half is cast only from the graveyard.
Quantities `"timesKicked"`, `"squadPaid"`. Trigger `castThis` ("when you cast this spell"), `cycled` ("when you cycle this card",
amount: X), `exerted`. Cycling costs may include `{X}`. `"modesRepeat": true` ("you may choose the same mode more than once").

## More effects

`hideaway` and `playHiddenFree`, `regenerate` (and `destroy` with `"noRegeneration"`), `populate`, `becomeRenowned`,
`preventDamage` (`"toYou"`, or with `"combatOnly"`, `"dealtBy"`, `"sources"`), `tripleDamage`, `cantLoseThisTurn`,
`ownersGainControl`, `protectionFromOpponents`, `drawUpTo`, `exileTopFaceDown` and `playOneExiledFree`, `takeCountersOfTriggered`,
`moveAllCounters`, `mayBounceSharingType`, `copyTriggeredAbility`, `swapGraveyardAndBattlefield`, `temptingOfferSearch`,
`copyEachYouControl`, `opponentsExileGreatestPower` (with `"damageIf"`), `copyIfOpponentsTopSharesType`, `suspendWhenResolves`,
`bounceSameManaValue`, `atNextEndStepAbout`, `destroyPowerAbove`, `exileFromGraveyardChosen` (then subject `exiled`),
`returnFromGraveyardAll`, `thisSpellToLibraryBottom`, `exileThisSpell`. Options: `castFromGraveyard` takes `"of"`, `"free"`,
`"maxManaValue"`, `"card"` and `"fromMilled"` (a spell cast this way is exiled instead of going to a graveyard); `search` takes
`"shareLandType"` and `"withExiled"`; `returnFromGraveyard` takes `"differentManaValues"` and `"anyGraveyard"`; `revealUntil` takes
`"attachTo"`; `reanimate` takes `"counterKinds"`; `may` takes `"else"`; `atNextUpkeep` takes `"yours"` and `"player"`; `mayPayX`
takes `"max"`; `piles` takes `"revealed"`; `exileTopPlayable` takes `"anyMana"` and several libraries; `tokens` takes `"size"` (an X/X
token); copies take `"exileAtEndOfCombat"`; `exileUntilLeaves` takes `"castable"`.

Triggers: `opponentSacrifices`, `opponentActivatesAbility`, `opponentTapsArtifactForMana`, `creatureLeaves`, `creaturesAttackOpponent`
(subject `attackersOfTriggered`), `playerAttacks` (amount: creatures attacking you), `youActivateNonManaAbility`,
`damageToYouPrevented`. Static abilities: scope `permanents`, `"goads"`, `"protectionFromRingBearers"`, `"fromGraveyard"`,
`spellCostIncrease`. Keywords: `Split second`, `Skulk`, `Exalted`. Conditions: `"exertedThisTurn"`, `"renowned"`,
`"triggeredPlayerMostLife"`, `"castDuringMainPhase"`, `"triggeredHadCounters"`, `"hasAnyCounters"`, `{ "cardTypesInGraveyard": 4 }`,
`{ "opponentHasMore": "life" | "creatures" | "lands" | "cards" }`, `{ "enteredThisTurn": filter }`, `{ "opponents": 2 }`. Filters:
`renowned`, `attackingYou`, `fromGraveyard`, `hasX`, `castFromHand`, `exiledWithSource`, `manaValueIsX`,
`sharesCreatureTypeWithTriggered`; target kind `exiledCard`, target option `controlledByDefendingPlayer`. Costs: `returnExiled:creature`.
Quantities: `{ "attackingPower": filter }`, `"cardsInAllHands"`, `{ "damageTakenThisTurn": "target" }`, `{ "greatestPowerOf": "target" }`,
`"greatestCommanderManaValue"`, `"otherAttackersSharingType"`, `"opponentCount"`, `"affectedHandSize"`. Subjects: `playerToYourRight`,
`yourRingBearer`, `attackers`, `lastControlled`, `opponentsDamagedBySameName`. Replacements: `OpponentsCantLoseYouCantWin`,
`DamageCantReduceYourLifeBelowOne`, `StealsOpponentsExtraDraws`, `LookAtLibraryTop`, `PlayLandsFromLibraryTopWhileBehind`.
