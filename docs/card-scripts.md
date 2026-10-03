# Card scripts

Content modules describe what cards do with small JSON files in `scripts/`, one per card, named after the
card's oracle id (`scripts/<oracle-id>.json`). A card needs a script only when its rules go beyond what the
engine derives from card data: type line, power/toughness, supported keywords and mana abilities written as
`{T}: Add {G}` / `{T}: Add {R} or {G}` / `{T}: Add one mana of any color`.

Comments (`//`) and trailing commas are allowed.

Also derived from rules text without a script: `Equip {N}`, `Enchant creature` (and land, artifact,
enchantment, permanent, "... you control") and "This land/creature enters tapped".

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
`tapped`, `inCombat` (attacking or blocking), `attacking`, `supertype` (`"basic"`), `notSubtype`.

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
