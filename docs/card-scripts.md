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
`manaAmount`, `manaAmountFrom`, `manaFromChosenColor`, `chooseOnEnter` (`color`, `creatureType`, `cardName`, `lookAtOpponentsHandThenCardName`, `nonbasicLandCardName`, `basicLandType`),
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
- **Transforming double-faced cards** (layout `transform`): the script describes the front face as usual and the back
  face under `"back": { "abilities": [ … ], … }` (a script of its own, with the same shape). The card is supported only
  when both faces are: a back face whose rules text needs a script stays unsupported until `"back"` has one. The card
  is cast with its front face, and has only the front face's characteristics everywhere but the battlefield; there it
  has the face that's up, and with its back face up its mana value is still the front face's. It enters front face up,
  and leaving the battlefield turns it front face up again.
  - Effect `{ "transform": "self" }` (or any subject: `"target"`, `"triggered"` …) turns a double-faced permanent over.
    It stays the same object: counters, damage, Auras and Equipment, tapped status and effects on it stay, and it
    doesn't enter or leave the battlefield. Anything that isn't a double-faced card or token doesn't transform. An
    ability of the permanent transforms it only if it hasn't transformed since that ability was put on the stack (for
    a delayed ability: since it was created), so two activations waiting on the stack turn it over once.
  - Putting a card onto the battlefield transformed: `"transformed": true` on `reanimate` (any subject, e.g.
    `{ "reanimate": "self", "transformed": true }` for "return it to the battlefield transformed") and `reanimateAll`.
    A card that isn't double-faced stays where it is.
  - Trigger `transforms`: "whenever this permanent transforms"; with `"onSelf": true` and a `"filter"`, "transforms into
    [a permanent matching it]" (judged right after it transformed); with only a `"filter"`, it watches other permanents
    ("whenever a creature you control transforms"). The abilities of the face that's now up are the ones that trigger.
  - Conditions `"transformed"` (the source is a double-faced permanent with its back face up) and `"frontFaceUp"`
    (a double-faced permanent with its front face up), usable in `"if"`, `"activateIf"` and static `"while"`.
    Filter `"transformed": true / false` ("a transformed permanent").
  - Copies copy the face that's up: a copy of a transformed permanent has the back face and mana value 0 and can't
    transform; a token copy of a double-faced permanent is a double-faced token, entering with the same face up.
  - Day/night cards (daybound, nightbound) and cards cast transformed (disturb …) are not run yet.
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

## Damage events, prevention and redirection

- **One damage event**: damage dealt at the same time (combat damage, "each creature and each player", a fight, divided damage, effects
  inside `simultaneously`) is one event. Before it's dealt, each part meets the replacement and prevention effects that apply to it; when
  several do, the affected player (or the affected permanent's controller) chooses their order (rule 616.1), and each applies once.
- **Prevention**: `preventDamage` with no qualifier prevents all (combat, with `"combatOnly": true`) damage this turn (Fog); qualifiers
  combine: `"dealtBy"`, `"sources"`, `"toYou": true`, `"toYourCreatures": true` ("to you and creatures you control": creatures that come
  under your control later in the turn too). Static: `"preventDamage": "all" | "noncombat"` (to the affected permanents, e.g.
  `"affects": "equipped"`). Replacements: `PreventOneDamageFromOpponentsSources` ("if a source an opponent controls would deal damage to
  you, prevent 1 of that damage", per source and event), `PreventDamageRemoveCounters` ("prevent that damage and remove that many +1/+1
  counters from it").
- **Redirection**: `{ "redirectDamage": 2, "to": "target" }`: "the next 2 damage that a source of your choice would deal to you and/or
  permanents you control this turn is dealt to [target] instead". The source is chosen as it resolves (a permanent, a spell, or an
  object referred to by something on the stack); the shield lasts across damage events until used up; when one event has more of it,
  its controller chooses which damage is redirected.
- **Dividing**: `divideDamage` and `distributeCounters` are divided as the spell is cast or the ability is put on the stack (rule 601.2d,
  at least 1 to each target, so no more targets than the amount); on resolution, what was assigned to a target that became illegal isn't
  dealt. `{ "divideEvenly": "X" }` ("X damage divided evenly, rounded down, among any number of targets") divides as it resolves, among
  the targets still legal. Card-wide `"extraTargetCost": 1`: "this spell costs {1} more to cast for each target beyond the first" (no
  more targets than can be paid for).
- **Randomness**: `discard` with `"random": true` ("discards N cards at random", all of them if fewer); `{ "destroyRandom": "eachTarget" }`
  ("destroy one of them at random", among the targets still legal).
- **Triggers**: `dealsDamageToOpponent` ("whenever this creature deals damage to an opponent", combat or not; `triggeredPlayer` is that
  player), `becomesTargetOfAny` ("when this becomes the target of a spell or ability", anyone's), `attachedBecomesTarget` ("when enchanted
  creature becomes the target of a spell or ability"), `counterRemoved` (once for each counter removed; `"counterKind"`, default +1/+1),
  `playerTapsLandForMana` ("whenever a player taps a land for mana": not a mana ability, it goes on the stack, also when lands are tapped
  while paying).
- **Static abilities**: `"loseKeywords": ["Flying"]` ("loses flying", in timestamp order with grants), `"cantActivate": true` ("its
  activated abilities can't be activated", mana abilities included).
- **Costs**: loyalty `-X` (X chosen as it's activated, at most its loyalty; `"X"` in its effects), card-wide `"xManaType": "{B}"` ("spend
  only black mana on X"), condition `{ "activatedThisTurn": 4 }` ("if this ability has been activated four or more times this turn",
  checked as it resolves).
- "Each player" and "each opponent" act in turn order starting with the active player (rule 101.4). "If this is untapped" uses the
  source as it last existed on the battlefield if it has left.

## Copies, libraries, continuous effects and visibility

- **Entering as a copy**: card-wide `"entersAsCopy": filter` ("You may have this creature enter as a copy of any creature on the
  battlefield"). As it enters, its controller chooses one matching permanent (not one entering at the same time) or none; it then
  has the copiable values of that object (rule 707.2: its printed values plus any copy effect on it, so copying a copy copies the
  original; never counters or other effects), including its "enters with counters" and "as this enters" rules and its enters
  triggers. It stays a card (copying a token doesn't make it a token). Choosing nothing leaves it as printed (Clone: a 0/0).
- **Auras entering without being cast** (any effect: `reanimate`, `reanimateAll`, Warp World …): the player it enters for chooses
  what it enchants, among what its enchant ability allows that it isn't protected from and that isn't entering at the same time;
  with nothing legal it stays where it is (rules 303.4f–g).
- **Names**: `"chooseOnEnter": "cardName"` chooses any card name without looking at anything;
  `"lookAtOpponentsHandThenCardName"` first looks at an opponent's hand; `"nonbasicLandCardName"` offers only names of nonbasic land cards. Names come from the game's card database
  (`GameConfig.CardNames`) or, without one, from the cards the chooser knows of, so a hidden card is never given away.
- **Effects**: `{ "copySpell": "triggered", "eachOtherPlayer": true }` (each player other than the spell's controller copies it, in APNAP
  order; a spell that already left the stack is copied as it last existed there), `{ "warpWorld": true }`,
  `{ "rebuildLibraryFromExile": 7 }` (Mirror of Fate), `{ "exileGraveyardAndNamesakes": "target", "filter": f }` (Haunting Echoes; the
  searcher may leave cards, rule 701.19b), `{ "searchThenNameCard": "triggeredPlayer", "filter": f }` (Sphinx Ambassador: the searcher
  must find a card if there is one; the other player names a card without knowing it), `{ "playTopFree": true }` (reveal the top card,
  play it free or exile it; a land only on your turn with a land play left, and it counts as your land play), `{ "shuffle": "you" }`,
  `{ "opponentsCantCastSpells": true }` (this turn; "can't cast" also stops casting during an effect).
- **Existing effects take more options**: `revealUntil` with `"rest": "shuffle"` and `"revealerPuts": true` (the revealing player puts
  the card onto the battlefield under their control), `revealTop` with `"else": [ … ]` (the revealed card is `"found"` there too),
  `reanimate` with `"addColors": ["B"]` ("is black in addition"), `become` with `"setColors"`, `reanimateAll` with `"from": "everyone"` and
  `"ownerControl": true`. `lookAtTop` with `"take": 0` only orders the cards.
- **Quantities**: `"halfLibrary"` (half the library of the player being affected, rounded down), `"otherCreaturesSharingType"` (in a static
  `pump`: worked out for each affected creature, counting other creatures sharing a creature type with it; changelings share every type).
- **Statics and conditions**: `"setChosenLandType": true` (with `"chooseOnEnter": "basicLandType"`: the land's land type becomes the chosen
  one; it loses its old land types and the abilities of its rules text and has the new type's mana ability, rule 305.7);
  condition `{ "topOfLibrary": filter }`.
- **Visibility (card-wide)**: `"opponentsPlayWithHandsRevealed": true` (its controller sees its opponents' hands) and
  `"playWithTopCardRevealed": true` (every player sees the top card of its controller's library).
- **Triggers**: `enchantedControllersUpkeep` ("at the beginning of the upkeep of enchanted creature's controller"; that player is
  `"triggeredPlayer"`). **Filters**: `"notColors": ["B"]` ("nonblack"); with `cantBeBlockedBy` it reads "can't be blocked except by black
  creatures".
- **Tokens** take `"powerFrom"` / `"toughnessFrom"` (a characteristic-defining ability, e.g. "equal to your life total"). Such abilities
  work in every zone (rule 604.3): a card in a hand, library, graveyard or exile, or a spell, has the power and toughness they define.

## Costs, filters, triggers and effects for graveyard, blocking and targeting rules

- **Costs**: `sacrificeAny:creature` / `sacrificeAny:Goblin` is "sacrifice a creature / a Goblin" (the source may sacrifice itself;
  `sacrifice:…` is still "another"), and `exileGraveyardCards:1:creature` exiles a card of that kind from your graveyard (the source,
  if it is in the graveyard, never pays for itself; without a kind, any card).
- **Filters**: `"chosenName": true` (has the name the source chose as it entered: `chooseOnEnter: "cardName"`; the effect only reaches
  what matches, so a name that no land has does nothing), `"manaValueIsTriggerAmount": true` (mana value exactly the amount the
  ability is about, for a reflexive ability made with `"amount": "X"` after `mayPayX`), `"self": true` (only the source).
- **Conditions**: `"attackedOrBlockedThisTurn"` (the source attacked or blocked this turn). **Quantities**: `{ "halfUp": quantity }`
  (half, rounded up, rule 107.1a; with `eachPlayer` the quantity is the player's own: `"life"`, `"handSize"`, a `count`).
- **Triggers**: `youBecomeTargetOfOpponent` (you become the target of a spell or ability an opponent controls; once however often) with
  the effect `{ "counterTriggeringUnlessPays": "{1}" }` (counters that spell or ability unless its controller pays); `discardedByOpponent`
  ("when a spell or ability an opponent controls causes you to discard this card": the card's own ability, it triggers from the
  graveyard it went to, with an intervening `"if"` as usual); `dealtDamage` (this creature is dealt damage, combat or not; once for each
  source; amount: the damage); `attachedBlocks` ("when enchanted creature blocks", once however many it blocks; the creature is
  `"triggered"`); `permanentDies` (any permanent put into a graveyard from the battlefield that matches `filter`, e.g. a creature or
  a planeswalker you control: `{ "anyOf": [{ "types": ["creature"] }, { "types": ["planeswalker"] }] }`). An emblem's "at the beginning
  of your upkeep / beginning of combat / end step" abilities trigger like those of your permanents.
- **Effects**: `pump` takes `"cantBeBlockedBy": filter` (until end of turn, creatures matching the filter can't block it: `{ "maxPower": 2 }`;
  "can't be blocked except by Spirits" is `{ "notSubtype": "Spirit" }`, also card-wide as `"cantBeBlockedBy"`); `damage` takes
  `"cantBePrevented": true` (only that damage, rule 615.12); `{ "ignoreHexproof": true }` (until end of turn your spells and abilities can
  target hexproof opponents and their hexproof creatures); `{ "mayCastFromGraveyard": filter }` (until end of turn you may cast
  matching spells from your graveyard, paying their costs; they go to the graveyard as usual); `exileTopPlayable` takes `"castOnly": true`
  (spells only, no lands); `reanimate` takes `"exileIfLeaves": true` ("if it would leave the battlefield, exile it instead of putting it
  anywhere else") and counter kinds `corpse` and `gold` exist; `lookAtTop` takes `"required": true` ("put one of them into your hand",
  no "may").
- **Card-wide**: `"uncounterableIfXAtLeast": 5` ("if X is 5 or more, this spell can't be countered"), `replaces` `LandsFromGraveyard`
  ("you may play lands from your graveyard", not limited to one per turn beyond the land plays).
- **Keywords**: `Can attack as though it didn't have defender`; `Can block an additional creature each combat` (up to two attackers; the
  search for the most requirements to obey counts such a creature as blocking one, so a second block that would alone satisfy one more
  requirement isn't demanded, though it is always allowed).

## Continuous copies, state triggers, counter bans and more

- The keyword `Can attack as though it didn't have defender` (above) is granted with a static ability, e.g. with
  `"while": { "sourceIs": { "anyOf": [{ "enchanted": true }, { "equipped": true }] } }`.
- **Condition** `"dealtDamage"`: the source has dealt damage since it came to the battlefield ("as long as it hasn't dealt
  damage yet" is `{ "not": "dealtDamage" }`).
- **Quantities** `{ "greatestManaValue": filter }` (greatest mana value among matching permanents you control), `"-lifeGained"`
  (negated life gained this turn, for "-X/-X where X is the life you gained") and `"foundThisWay"` (cards a `lookAtTop` moved,
  usable as `{ "atLeast": "foundThisWay", "value": 1 }`; the subject `"found"` names them, so `whenYouDo` can be `"about": "found"`).
- **Continuous copy** ("As this Aura enters, choose a creature. Enchanted creature is a copy of the chosen creature", rule 613.1a):
  card-wide `"chooseOnEnter": "creature"` remembers a creature, and a static ability with `"copyOfChosen": true` and
  `"affects": "enchanted"` gives the enchanted permanent its copiable values. It follows the chosen creature while it stays on the
  battlefield and keeps the values it last had once it has left.
- **Counter on a permanent as it enters**: `"entersCounterOn": { "filter": { "types": ["artifact"] }, "kind": "phylactery" }` (one
  permanent you control matching the filter, chosen as this enters). Filter `"hasCounterKind": "phylactery"` matches permanents
  with a counter of that kind.
- **State triggers** (rule 603.8): `{ "trigger": "state", "when": condition, "effects": [...] }` triggers as soon as the condition is
  true and not again until it has left the stack, e.g. `"when": { "not": { "control": { "hasCounterKind": "phylactery" } } }`.
- **Counter bans**: effect `{ "cantHaveCounters": "target" }` (an object, or a player: "can't get counters") for as long as the
  source stays on the battlefield; `removeAllCounters` also works on players (poison counters).
- **Replacement** `{ "exileUncastEntering": filter }` until end of turn: a matching card that would enter the battlefield without
  having been cast is exiled instead (it makes none of its entering choices).
- **One target per player** ("for each player, choose target permanent that player controls"): a target with `"perPlayer": true`
  becomes one requirement for each player who has a legal choice. `{ "sacrificeIt": "eachTarget" }` has each target's controller
  sacrifice it; subject `"sacrificers"` is the players who sacrificed a permanent this way; `{ "revealTopPut": filter, "who": subject }`
  has each of them reveal the top card of their library and put it onto the battlefield if it matches.
- `{ "exileLibraryAllBut": 1, "who": "target" }` exiles all but the bottom N cards of a library.
- `{ "blink": "self", "transformed": true }` exiles the subject and returns it transformed (a card that isn't double-faced stays exiled).
- A spell's additional cost that sacrifices or discards
  remembers the cards for `sacrificedPower`, `sacrificedToughness` and discard quantities.

## Replacement effects, damage prevention and life

- **Prevention shield on one object**: `{ "preventDamage": true, "to": "target" }` (any subject, e.g. `"self"`): "prevent all damage that
  would be dealt to it this turn". The shield is on the object as it is now: if it leaves the battlefield and comes back it's a new object
  without the shield. Add `"combatOnly": true` for combat damage only.
- **Damage plus N** (card-wide): `"damageBonus": { "sources": filter, "amount": 1 }` ("if another red source you control would deal damage
  to a permanent or player, it deals that much damage plus 1 instead": `"sources": { "colors": ["R"], "other": true }`). The filter's
  controller defaults to `you` and `other` excludes the permanent itself; a source that has just left the battlefield is judged as it last
  existed there, a spell as it is on the stack.
- **Prevent N of the damage to you** (card-wide): `"preventDamageToYou": { "sources": filter, "amount": 1 }` ("if a creature would deal
  damage to you, prevent 1 of that damage": `"sources": { "types": ["creature"] }`; the controller defaults to `any`). It applies once to
  each source's damage in each damage event.
- These, the damage doublers and the other prevention effects are ordered by the player being dealt damage (or the damaged permanent's
  controller) when more than one applies (rule 616.1); each applies once (rule 614.5).
- **Life gain replacements** (`replaces`): `DoubleLifeGain` ("if you would gain life, you gain twice that much life instead"),
  `OpponentsLifeGainBecomesLoss` ("if an opponent would gain life, that player loses that much life instead", lifelink included). With
  `ExtraLifeGain`, `DoubleLifeGainAtFiveOrLess` and these, the player who would gain life chooses the order (rule 616.1); once the gain
  has become a loss, the effects about gaining life no longer apply. A player who can't gain life gains (and loses) nothing.
- **Exile instead of dying** (`replaces`): `ExileInsteadOfDying` ("if this creature would die, exile it instead"), the creature's own
  ability (not while it has lost its abilities).
- **Tokens and "exile it instead"**: `exileUncastEntering` also exiles creature tokens that would be created (they cease to exist) unless
  its filter says `"token": false` ("nontoken creature"), and a copy of a permanent spell resolving wasn't cast either (rule 707.12).
- **Trigger** `endOfCombat`: "at end of combat" (the beginning of each end of combat step). **Condition**
  `{ "attackedThisCombatWithOthers": 2 }`: "if [this] and at least two other creatures attacked this combat" (declared as attackers this
  combat, even if they have left combat; this must be the object that attacked).
- **Effect** `{ "attacksSourceNextTurn": "target" }`: "[it] attacks [this planeswalker] during its controller's next turn if able" — a
  requirement for that creature during the next turn of the player controlling it as the effect happens, obeyed only by attacking this
  planeswalker (as the same object); it is never obeyed at the cost of a restriction.
- A planeswalker that is also a creature (an animated planeswalker) loses loyalty counters and has the damage marked on it (rules 120.3c,
  120.3e).
