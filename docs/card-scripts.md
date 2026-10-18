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
- **Preparation cards** (layout `prepare`): the script describes the creature face as usual and its prepare spell under
  `"prepare": { "spell": { … } }` (a script of its own). The card is a creature in every zone. Effects `{ "becomePrepared": subject }`
  and `{ "becomeUnprepared": subject }` prepare and unprepare it; only a creature with a prepare spell can become prepared, and one
  already prepared can't again. `"entersPrepared": true` prepares it as it enters. While it's prepared, a copy of the spell waits in
  exile; only the creature's current controller may cast it, at the spell's normal timing and cost, and casting it unprepares the
  creature. Unpreparing, or the creature leaving the battlefield, makes the copy cease to exist. Condition `"prepared"` (or
  `{ "not": "prepared" }`). The keyword `Prepared` is handled by these parts.
- **Empower Jace** (effect `{ "empowerJace": N }`, N a quantity): puts N loyalty counters on a Jace planeswalker token you control
  (you choose one if you control several; never a nontoken Jace). If you control none, a blue Jace token with 0 loyalty,
  "−1: Surveil 1" and "−3: Draw a card" is created first. The keyword `Empower Jace` is handled by this effect.
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
`"while"`), `revealUntil` (`"battlefieldIf": filter`; with `"castFree"`, `"creatureHasteAndSacrifice": true`: if you cast a creature
spell this way, it gains haste and "At the beginning of the end step, sacrifice this creature."), `copy` (`"notLegendary"`), `become` (`"powerFrom"`,
`"toughnessFrom"`, `"continuous"`, `"whileSource"`), `pump` (`"whileSource"`), `returnExiledWithThis` and
`searchExileWithThis` (`"count"`), `emblem` (`"untilEndOfTurn"`), and `whenYouDo` (`"about"`: what "that creature" is,
`"amount"`: worked out as the reflexive ability is created), `"carryExiled": true` (the cards this spell or ability exiled go with the
reflexive ability, so its target can use the filter `amongFound`: "target creature card exiled this way").

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

- `extraMana` entries also take `"oneOfEach": true` (one mana of each of its `types` at once, instead of `amount` mana of one type)
  and `"combination": true` ("add two mana in any combination of …": the player chooses
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
  sacrificed creature), `"yourPermanentLeft"`, `"attackedThisTurn"`, `"greatestPower"`, `"triggeredPlayerAttackedYou"`,
  `"triggeredPlayerIsYou"` (the player the event happened to is you: `"when": "triggeredPlayerIsYou"` on
  `creatureCombatDamageToPlayer` is "whenever a creature deals combat damage to you").
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
- `{ "tapAllToDamage": filter, "to": "target" }`: taps all your untapped permanents matching the
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
- **Entering**: `"chooseOnEnter": "revealOrTapped"` with `"enterReveal": filter`; `"entersTappedUnless": { "opponents": 2 }`; `"entersPrepared": true` (see the preparation cards above);
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
`becomePrepared` and `becomeUnprepared`, `empowerJace` (see the preparation cards and Empower Jace above),
`preventDamage` (`"toYou"`, or with `"combatOnly"`, `"dealtBy"`, `"sources"`), `tripleDamage`, `cantLoseThisTurn`,
`ownersGainControl`, `protectionFromOpponents`, `drawUpTo`, `exileTopFaceDown` and `playOneExiledFree`, `takeCountersOfTriggered`,
`moveAllCounters`, `mayBounceSharingType`, `copyTriggeredAbility`, `swapGraveyardAndBattlefield`, `temptingOfferSearch`,
`copyEachYouControl`, `opponentsExileGreatestPower` (with `"damageIf"`), `copyIfOpponentsTopSharesType`, `suspendWhenResolves`,
`bounceSameManaValue`, `atNextEndStepAbout`, `destroyPowerAbove`, `exileFromGraveyardChosen` (then subject `exiled`),
`returnFromGraveyardAll`, `thisSpellToLibraryBottom`, `exileThisSpell`. Options: `castFromGraveyard` takes `"of"`, `"free"`,
`"maxManaValue"`, `"card"`, `"fromMilled"` (only among the cards milled this way) and `"anyMana"` (mana of any type can be spent to cast it); an instant or
sorcery cast with `castFromGraveyard` is exiled instead of going to a graveyard; `search` takes `"shareLandType"`, `"withExiled"` and
`"mayBattlefieldIf": condition` (decided once the card is found: if the condition holds, you may put that card onto the battlefield
tapped; if you don't, it goes into your hand); `returnFromGraveyard` takes `"differentManaValues"` and `"anyGraveyard"`; `revealUntil` takes
`"attachTo"`; `reanimate` takes `"counterKinds"`; `may` takes `"else"`; `atNextUpkeep` takes `"yours"` and `"player"`; `mayPayX`
takes `"max"`; `piles` takes `"revealed"`; `exileTopPlayable` takes `"anyMana"` and several libraries; `tokens` takes `"size"` (an X/X
token); copies take `"exileAtEndOfCombat"`; `exileUntilLeaves` takes `"castable"`.

Triggers: `opponentSacrifices`, `opponentActivatesAbility`, `opponentTapsArtifactForMana`, `creatureLeaves`, `creaturesAttackOpponent`
(subject `attackersOfTriggered`), `playerAttacks` (amount: creatures attacking you), `youActivateNonManaAbility`,
`damageToYouPrevented`. Static abilities: scope `permanents`, `"goads"`, `"protectionFromRingBearers"`, `"fromGraveyard"`,
`spellCostIncrease`. Keywords: `Split second`, `Skulk`, `Exalted`. Conditions: `"exertedThisTurn"`, `"renowned"`, `"prepared"`,
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
  under your control later in the turn too). With `"to"`: `"putCounters": true` ("if damage would be dealt to that creature this turn,
  prevent that damage and put that many +1/+1 counters on it": a replacement, so the counters are put as the damage is prevented);
  `"fromOpponents": true` (only damage from sources an opponent of the effect's controller controls); `"whenCreatureDamagePrevented":
  [effects]` with `"whenPreventedText"` ("whenever damage from a creature is prevented this way, …": a delayed triggered ability for
  the rest of the turn about the creature that would have dealt the damage, subject `"triggered"`; the text is its rules text).
  Static: `"preventDamage": "all" | "noncombat"` (to the affected permanents, e.g.
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
- **Auras entering without being cast** (any effect: `reanimate`, `reanimateAll`, `warpWorld` …): the player it enters for chooses
  what it enchants, among what its enchant ability allows that it isn't protected from and that isn't entering at the same time;
  with nothing legal it stays where it is (rules 303.4f–g).
- **Names**: `"chooseOnEnter": "cardName"` chooses any card name without looking at anything;
  `"lookAtOpponentsHandThenCardName"` first looks at an opponent's hand; `"nonbasicLandCardName"` offers only names of nonbasic land cards. Names come from the game's card database
  (`GameConfig.CardNames`) or, without one, from the cards the chooser knows of, so a hidden card is never given away.
- **Effects**: `{ "copySpell": "triggered", "eachOtherPlayer": true }` (each player other than the spell's controller copies it, in APNAP
  order; a spell that already left the stack is copied as it last existed there), `{ "warpWorld": true }` (each player shuffles their permanents into their library and puts that many back),
  `{ "rebuildLibraryFromExile": 7 }` (choose up to seven exiled cards and replace your library with them), `{ "exileGraveyardAndNamesakes": "target", "filter": f }` (the
  searcher may leave cards, rule 701.19b), `{ "searchThenNameCard": "triggeredPlayer", "filter": f }` (the searcher
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

## Combat, costs, filters and targeting (Magic Origins)

- **Block tax** (card-wide): `"blockTax": "{1}"` with `"blockTaxIf": "attacking"` ("as long as this creature is attacking,
  creatures can't block unless their controller pays {1} for each of those creatures"). Every permanent whose condition holds adds
  its tax; the defending player pays it for each blocking creature as blockers are declared (rules 509.1d, 509.1h-i), activating
  mana abilities as needed; a declaration that isn't paid for is made again (after a few, no blocks). While blocking costs
  something, no blocking requirement has to be obeyed (509.1c: a requirement is never paid for); the block request carries
  `TaxPerBlocker`. Together with `"attackTax": "{1}", "attackTaxIf": "untapped"` this gives both taxes.
- **Keyword** `Can block only creatures with flying` (grant it with a static `"keywords"`): a blocking restriction; requirements to
  block an attacker it can't block don't apply to it.
- **"Attacks alone"**: `{ "attackersExactly": 1 }` counts every attacking creature, whoever controls the ability; all attackers are
  declared before any attack trigger looks at the attack.
- **Costs**: `tapPermanents:2:artifact` ("tap two untapped artifacts you control"; `artifact|creature` for either kind): any
  untapped matching permanents you control, the source included, summoning sick or not (rule 302.6 is only about {T});
  `exileGraveyardCards:1:instant|sorcery` takes several card types. A spell's `additionalCost` takes `"exileGraveyard": 2` with
  `"exileGraveyardFilter": filter` ("as an additional cost to cast this spell, exile two creature cards from your graveyard").
- **Filters**: `"without": ["Flying", "Reach"]` (has none of them: `"cantBeBlockedBy": { "without": ["Flying", "Reach"] }` is "can't
  be blocked except by creatures with flying or reach"), `"powerNotEqualToughness": true`, `"notOwnedByYou": true` ("a permanent
  owned by another player", tokens included), `"notTriggered": true` (not the object the trigger is about: "+1/+1 for each other
  creature you control"), `"maxManaValueX": true` (mana value X or less, X being the resolving spell's) and `"targetsYou": true` (a
  spell with the ability's controller among its targets: "target instant or sorcery spell that targets you", checked as it is cast
  and as it resolves).
- `bounceAll` returns everything at once (one event).
- **Targeting restriction** (card-wide): `"cantBeTargetedBy": filter`, matched against the spell or the ability's source (as it last
  existed if it left the battlefield), for every player: `{ "notColors": ["G"] }` is "can't be the target of nongreen spells or
  abilities from nongreen sources" (colorless ones included).
- **Conditional uncounterability** (card-wide): `"uncounterableIf": condition` ("Spell mastery — If …, this spell can't be
  countered"), checked whenever something tries to counter the spell, for its controller. Only the exact line "This spell can't be
  countered" makes a card uncounterable from its rules text.
- **Effects**: `{ "copySpell": "target", "counteredThisWay": true }` copies the spells an earlier `counter` of the same spell or
  ability countered, as they last existed on the stack (the copy's controller may choose new targets); `{ "changeTarget": "target",
  "to": "self" }` is "you may change a target of target spell or ability to this creature": its controller picks one of the
  targets (of any number) for which the source is a legal choice (meets that target's requirement, isn't chosen twice for one
  "target" word, keeps the rules between targets, rule 115.7), or none; nothing changes once the source has left the battlefield.

## "This" as one object, last known renown and "up to N" targets

- **"This" is one object** (rule 400.7): every effect that refers to the source (`"self"`, `"attached"`, "this card in your
  graveyard", `"whileSourceRemains"` and the like) only finds the object that triggered or was activated. If the source left its
  zone and came back before the ability resolves, it is a new object and the ability does nothing to it (a renown trigger
  puts no counters on a creature flickered in response; a second "exile this, then return it transformed" does nothing).
  An effect of the ability that moves its own source can still find the object it became, so later effects follow it:
  `[{ "blink": "self" }, { "counters": 1, "what": "self" }]` puts the counter on the returned permanent. Delayed abilities
  created by a permanent's ability ("at the beginning of the next upkeep, …") remember the object that created them.
  Scripts don't need workarounds such as `"if": { "not": "transformed" }` for this any more (they stay harmless).
- **`"renowned"` uses last known information**: an intervening `"if": "renowned"` (or `{ "not": "renowned" }`) is checked on
  resolution against the source as it last existed on the battlefield if it left (rules 603.4, 608.2h).
- **"Up to N target …"**: a target with `"upTo": N` (a number or a quantity such as `"X"`) becomes N optional, different targets
  for spells, activated and loyalty abilities as well as triggered abilities. X is announced first (601.2b), so `"upTo": "X"`
  works with `{X}` in an activated ability's cost. A spell or ability whose only targets are "up to" ones can be cast or
  activated, and a trigger put on the stack, with no legal target at all. Effects use `"eachTarget"` (or `"target"`,
  `"target2"`, …). Writing the target out N times with `"optional": true` works the same way.

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

## Triggers and turn tracking (renown, blocks, targeting, last turn, damage this turn)

- **Triggers**: `becomesRenowned` ("when this creature becomes renowned") and `creatureBecomesRenowned` with a `filter` ("whenever a
  creature you control becomes renowned"; the watcher sees itself too; `"triggered"` is that creature): they trigger when a permanent
  goes from not renowned to renowned on the battlefield, whatever made it (`becomeRenowned`; renown itself stays a scripted
  trigger, rule 702.112). `creatureBecomesBlocked` with a `filter` ("whenever a creature you control becomes blocked"): once for each
  attacker as it becomes blocked, however many creatures block it (rule 509.3c); `"triggered"` is the attacker.
  `becomesTargetOfYours` with a `filter` ("whenever a creature an opponent controls becomes the target of a spell or ability you
  control"; subject: that permanent): spells and abilities the watcher's controller controls, as they are put on the stack and when
  one of their targets is changed to it. `permanentBecomesTarget` ("… of a spell or ability an opponent controls") now asks whether
  that spell or ability's controller is an opponent of the watcher's controller (multiplayer), and also sees changed targets.
- **Batching per player**: `"batchedPerPlayer": true` instead of `"batched": true` ("whenever one or more artifact creatures you
  control deal combat damage to a player"): once for each player the simultaneous events are about. `"batched"` alone stays once per
  event ("… to one or more players").
- **Conditions**: `{ "spellsCastLastTurn": 2 }` (a player cast that many spells during the previous turn, whoever's turn it was; usable
  as an intervening `"if"`), `{ "dealtDamageThisTurn": 3 }` (the source has dealt that much damage this turn, all of it after prevention
  and replacement, combat or not; counted for the object it is: an ability resolving after its source left counts what that object
  dealt, rule 400.7).
- **Players dealt damage**: subject `"playersDamagedThisWay"` (players this spell or ability dealt damage to, prevented damage left out).
  `emblem` takes `"for": subject` (each of those players gets an emblem of their own: they own it, so its "your upkeep" is theirs, and
  "you" is its owner) and `"about": subject` (the object its abilities call "that creature", remembered as that object). Filter
  `"damagedThisTurnByThat": true` matches creatures dealt damage this turn by that object (as they last existed if they died), so an
  `"untilEndOfTurn"` emblem with a `creatureDies` trigger is "whenever a creature dealt damage by that creature this turn dies" (damage
  dealt before it was created counts; `"triggeredPlayer"` is the dying creature's last controller).
- Effects of an ability about its source (`"blink": "self"`, `"sacrificeIt": "self"`, `gainControl` with `"whileYouControl"`) only
  affect the object the ability came from, not a new object the card became (rule 400.7); `"sacrificeIt": "self"` sacrifices it only
  if the ability's controller controls it (rule 701.21a).

## Sweeps into libraries, casting while resolving, naming and storage counters

- `{ "shuffleIntoLibraries": "everyone", "hand": true, "graveyard": true }` ("each player shuffles their hand and graveyard into their
  library"): everything moves at once (one event), then each library is shuffled. `"permanents": true` adds the permanents each player
  owns (tokens too, which then cease to exist); `"drawThatMany": true` then has each player draw as many cards as they put into their
  library this way, tokens counted.
- `{ "eachPutsFromHand": filter, "who": "everyone" }`: each player, in turn order, chooses any number of matching cards from their hand;
  then all of them enter the battlefield at the same time under the control of the player who put them there.
- `{ "revealTopCastFree": 7, "of": "target", "filter": f, "casts": 1, "moreCastsIf": condition, "moreCasts": 2, "rest": "graveyard" }`:
  the player reveals the top cards of their library; you may cast up to `casts` (or `moreCasts` when the condition holds as it resolves,
  e.g. spell mastery) matching spells from among them without paying their mana costs, while this resolves (rule 608.2g: timing doesn't
  matter, targets, additional costs and "can't cast" effects do); then the revealed cards not cast go to their owner's graveyard.
- `{ "nameAndExile": "target", "nameFilter": { "types": ["creature"] } }`: choose a creature card name (any creature card's name, from
  `GameConfig.CreatureCardNames`), then search that player's graveyard, hand and library for any number of cards with that name (the
  searcher sees the hand and library and may leave some, rule 701.19b), exile them, and that player shuffles.
- `discardChosen` takes `"optional": true` ("you may choose …") and `"else": [ … ]` ("if you don't, …", also when there was nothing to
  choose), done for that player (`"target"` still names them).
- `keepOneOfEachType` takes `"chooser": "you"` ("for each player, you choose …"), `"kinds": ["artifact", "creature", "enchantment",
  "planeswalker"]` and `"nonlandOnly": true` ("sacrifices all other nonland permanents"). All choices are made first, in turn order; then
  every player sacrifices at the same time. One permanent can be chosen for each of its types.
- `copy` takes `"gainsHaste": true` ("that token gains haste"): an effect on the token, not one of its copiable values (rule 707.9b covers
  only "except …"; `"haste": true` is "except it has haste"). A copy of `"triggered"` whose object already left the battlefield uses its
  last known copiable values (rule 608.2h).
- `become` takes `"loseAbilities": true` ("loses all abilities and becomes …", one effect). `setSubtypes` on `become` and static abilities
  replaces only the subtypes of the same kind as the new ones (rule 205.1a): a land creature that becomes a Frog stays a Forest.
- `castFromGraveyardThisTurn` takes `"exileInstead": true` ("if that spell would be put into your graveyard, exile it instead"); a card
  that may also be cast from the graveyard some other way that turn is cast the way that doesn't exile it.
- Quantities `{ "power": "found" }` / `{ "toughness": "found" }`: the revealed card's (`revealTop`), as it was when revealed; `{ "manaValue":
  "affected" }` in a static ability: the mana value of each affected object. Static `"setPower"` / `"setToughness"` take quantities, worked
  out for each affected object in layer 7b ("base power and base toughness each equal to its mana value").
- Card-wide `"chooseOnEnter": "opponentsRevealHandsThenNonlandName"` (each opponent reveals their hand; you choose the name of a nonland
  card revealed this way, none without one) and `"opponentsCantCastChosenName": true` (a static ability: while the permanent is on the
  battlefield with its abilities, its controller's opponents can't cast spells with the chosen name; a split card can still be cast as a
  half with another name). Both also work with the other `chooseOnEnter` name choices.
- `extraMana` entries take `"removeCounters": "storage"`: "{T}, Remove any number of storage counters from this: Add {C} for each counter
  removed this way" (a mana ability, rule 605.1a). Paying a cost uses as many counters as needed; activated on its own, the player chooses
  how many. Counter kind `storage`.
- `endTurn` exiles the spell that ends the turn too (rule 723.1b).

## Cost filters, counts, conditions and casting restrictions

- **Trigger** `combatDamageToPlayerOrPlaneswalker`: "whenever this creature deals combat damage to a player or planeswalker", once for
  each player or planeswalker it damaged (amount: that damage; `"triggerAmount"` is "that many").
- **Cost filters** (activated abilities): `"sacrificeFilter": filter` says what a `sacrifice:` / `sacrificeAny:` cost sacrifices
  ("sacrifice another black creature": `"sacrifice:creature"` with `{ "types": ["creature"], "colors": ["B"] }`); `"tapFilter": filter`
  says what a `tapPermanents:` / `tapCreatures:` cost taps ("tap three untapped Zombies you control": `"tapPermanents:3:creature"` with
  `{ "types": ["creature"], "subtype": "Zombie" }`, the source included). Cost `life:commanderColors` is "pay life equal to the number of
  colors in your commanders' color identity" (none without a commander).
- **Filters**: `"allTypes": ["artifact", "creature"]` needs every listed type ("artifact creature"), where `types` needs any one.
- **Quantities**: `"triggeredToughness"` (the toughness of the object the trigger was about), `{ "halfDown": quantity }` (half, rounded
  down), `{ "devotion": "B" }` (devotion to black, rule 700.5; a list for several colors), `{ "graveyard": f, "of": "everyone" }` /
  `"of": "opponents"` (all graveyards / your opponents' graveyards), `"playersWhoDrewThisWay"` (players who drew a card in an earlier
  draw effect of the same spell or ability: "for each opponent who drew a card this way"), `{ "opponentsWithMore": "cards" }` (opponents
  with more cards in hand than you; also `"life"`, `"creatures"`, `"lands"`).
- **Conditions**: `{ "opponentDealtDamage": 7 }` (an opponent was dealt that much damage this turn), `{ "greatestManaValue": filter }`
  (you control the matching permanent with the greatest mana value or one tied for it), `{ "sameName": filter, "count": 3 }` (you
  control that many matching permanents with the same name), `{ "opponentControls": filter, "count": 4 }` (an opponent controls that
  many), `"attachedControllerIsMonarch"` (the controller of the permanent this is attached to is the monarch).
- **Static abilities**: `"preventDamage": "combat"` (prevent all combat damage that would be dealt to the affected permanents).
- **Card-wide**: `"cantCastDuringFirstTurns": 3` ("you can't cast this spell during your first, second, or third turns of the game", for
  every way of casting it), `"graveyardCastIf": condition` ("you may cast this card from your graveyard as long as you control a
  Zombie"), `replaces` `OpponentsNontokenCreaturesExiledInsteadOfDying` (with the `creatureExiledInstead` trigger).
- Ability words `Lieutenant`, `Enrage`, `Celebration`, `Pack tactics` and `Imprint` only label rules text the script spells out.
- **Damage from another object**: `damage` takes `"from": subject` ("it deals X damage", "it deals damage equal to its power"): that object
  deals the damage (its deathtouch and lifelink apply); with `"triggered"`, an object that has left the battlefield still deals it as it
  last existed.
- **Trigger** `permanentLeaves` with a `filter` ("whenever another permanent you control leaves the battlefield", judged as it last
  existed; permanents leaving at the same time see each other), usually with `"if": "triggeredHadCounters"`. `permanentBecomesTarget`
  and `becomesTargetOfYours` now make `"triggeredPlayer"` the controller of the spell or ability ("deals 3 damage to that player").
- **Filters**: `"attackingYouDirectly": true` (attacking you, not one of your planeswalkers: "creatures attacking you"), `"greatestPower":
  true` (a creature with the greatest power among those its controller controls).
- **Casting**: card-wide `"escalate": "{G}"` (paid once for each mode chosen beyond the first; no more modes than can be paid for),
  `"improvise": true` (each untapped artifact tapped while paying pays for {1}), `alternativeCost` with `"extra"` (a non-mana part, e.g.
  `{ "tap": { "types": ["creature"], "keyword": "Flying" }, "tapCount": 4 }`), `"gift": "card"` (the promised opponent draws a card). A
  permanent spell's promised gift is given as it enters (a triggered ability), an instant's or sorcery's before its other effects; a target
  with `"onlyIfGiftPromised": true` exists only if the gift was promised.
- **Effects**: `{ "myriad": true }` (rule 702.116: for each opponent other than the defending player, you may create a token copy tapped
  and attacking that player or a planeswalker they control, exiled at end of combat), `{ "doublePowerAndToughness": subject }` (until end
  of turn). Card-wide `"attackTaxPlayerOnly": true` limits an `attackTax` to attacks on the player ("creatures can't attack you unless …").
- Quantity `"greatestCommanderManaValueAnywhere"` (among your commanders wherever they are); ability word `Living weapon` labels the
  scripted enters trigger (a 0/0 black Phyrexian Germ token, then `{ "attach": "created" }`).
- **More**: `replaces` `KeepRedMana` ("you don't lose unspent red mana as steps and phases end"); `mayPayX` takes `"manaType": "{R}"` ("pay
  any amount of {R}"); `{ "proliferate": true }` (rule 701.34: the controller picks permanents with counters and players with poison
  counters); `divideDamage` takes a quantity (`"greatestPower"`), worked out as the division is announced; `bite` takes `"excessIfTrample":
  true`; trigger `diesOrExiled` ("when this dies or is put into exile from the battlefield"; `"self"` then finds the card in the graveyard or
  exile); card-wide `"entersAsCopyUntilEndOfTurn"` and `"entersAsCopyWithHaste"` with `entersAsCopy`; `"othersEnterFilter": filter` limits
  `othersEnterWithCounters` ("each Dragon you control", `{ "chosenType": true }`); `{ "enteredThisTurn": filter, "count": 2 }`
  (celebration); static `"cantBeBlockedBy": filter`; quantity `"triggeredPlayerAttackers"` (creatures the player the trigger is about attacks
  with this combat).
- **Costs**: `returnPermanent:artifact` ("return an artifact you control to its owner's hand", the source too).
- **Subjects**: `"otherOpponents"` (each opponent other than the player the trigger is about), `"attackedBySelf"` (the player or planeswalker
  the source is attacking).
- **Mana**: `replaces` `ExtraColorlessWhenTappedForColorless` ("whenever you tap a permanent for {C}, add an additional {C}": abilities that
  add only {C} produce one more, which mana payment knows about); `addManaCombination` takes `"abilitiesToo": true`.
- **Imprint**: static `"creatureTypesOfLastExiled": true` with `"setPower": "lastExiledCreaturePower"`, `"setToughness":
  "lastExiledCreatureToughness"` and `"while": "creatureCardExiledWithThis"` (cards exiled with `{ "exile": …, "linked": true }`).
- **Effects**: `{ "tokensFightOpponentsCreatures": token }` (a token for each creature your opponents control, each fighting a different one,
  all damage at once); `{ "reselectAttack": subject }` (choose again what an attacking creature attacks, never its controller or their
  permanents) with condition `{ "step": "DeclareAttackers" }`; `{ "discover": 10 }` (rule 701.57; `"anyNonland": true` stops at any nonland
  card, castable free only within the mana value, and `"restStaysExiled": true` leaves the others in exile; quantity `"discoverShortfall"`);
  `{ "discardAnyNumber": true }` (then `{ "discarded": {} }` counts them); `exileTopPlayable` takes `"untilYourNextEndStep": true` and
  `"damagePerStillExiled": 2` (at your next end step, that much damage to each opponent for each of the cards still exiled).
- **Card-wide**: `"readAhead": true` (a Saga's controller chooses its starting chapter as it enters; earlier chapters don't trigger),
  `"chooseOnEnter": "option"` with `"enterOptions": ["Khans", "Dragons"]` and condition `{ "chosenOption": "Khans" }`,
  `"castFromLibraryTop": filter` with `"castFromLibraryTopHaste": true` ("you may cast creature spells with power 4 or greater from the top of
  your library; if you do, it gains haste").

## Heroes, summons and jobs

- **Card frames**: `layout: meld` cards are single-faced; `"meldsWith": name`, `"meldInto": name` name the partner and the combined
  face, and effect `{ "meld": true }` exiles both and returns them melded (rule 701.42; its mana value is the sum). The combined face is
  found by name but never listed, searched, collected or allowed in a deck. Flavor words and ability words before " — " ("Gae Bolg —
  Equip {4}") only label the line, so keywords after them are still derived. `Job select` is derived (create a 1/1 Hero, attach this).
- **Costs**: Phyrexian symbols `{B/P}` (pay the colored mana or 2 life); `"tiered"` spells give each mode a `"cost"` (rule 702.183; only
  affordable modes are offered); `"overload": cost` with `"overloaded": spell` (702.96); `"ninjutsu"` / `"commanderNinjutsu"` with ability
  cost `"…, ninjutsu"` and effect `"ninjutsuEnter"`; `"kickerCost"`, `"discardUpTo"`, `"returnToHand"` extra costs; `"minX"`,
  `"cantBeCopied"` on abilities; mana options `exileSelf`, `copyNextInstantOrSorcery` (`copies`), `equipOnly`, `addsCounter`, `times`,
  `abilitiesToo`, `onlyFor`.
- **Counters**: stun counters stop any untap (one is removed instead, 122.1d); finality counters exile instead of the graveyard;
  `blight` and `point` kinds. `"countersUpTo"`, `"removeCounterFromAnyNumber"`, `"putSourceCountersOn"`, `"entersWithAdditionalCounters"`.
- **Effects**: `flipCoin` (`won` / `lost`), `rollDie`, `additionalEndStep` (`"ifFirst"`), `copyAbility` (target `ability:you`, `count`),
  `copyNextInstantOrSorcery`, `meld`, `ninjutsuEnter`, `searchLibraryOrGraveyard`, `attachFormerlyAttached`, `attachAnyNumber`,
  `unattachAtNextEndStep`, `unattachWhenControlLost`, `revealAndAlternate`, `rememberChosen`, `exileTop` (`playableDuringLoreTurns`,
  `playableThisTurn`), `nextCreatureSpell` (`haste`, `counters`), `eachExilesTopPlayLesser`, `exileRandomPermanentCardCopyTapped`,
  `shuffleFromGraveyard`, `lookHandLandsGraveyard`, `doubleDamageUntilYourNextTurn`, `eachDealsDamageEqualToPower`, `doublePower` /
  `triplePowerAndToughness`, `thisSpellOntoBattlefieldTransformed`, `castExiledNow`, `handToTop`, `commanderToHand`,
  `damageRandomOpponent`, `addManaOneColor`, `addManaCombination` (`types`), `exileUntilLesserCastFree`, `revealOnePerCardType`;
  `gainControl` with `"to"` gives a permanent away; `putFromHand` `tappedAttackingIf`; tokens `attackingEachOpponent` /
  `attackingChosenDefender`; `mayPay` `lifeFrom`; counter spells `unlessPays`, `exileIfCountered`; `sacrificeAtNextUpkeep`,
  `sacrificeAtYourNextEndStep`, `sacrificeAtEndOfCombat` on copies; search `differentNames`; `bite` `times`.
- **Quantities**: `manaSpent` (on the triggering spell), `{ "manaValueSum": f }`, `controlGainedThisWay`, `foundManaValue`,
  `{ "found": f }`, `discardedManaValue`, `cardTypesAmongDiscarded`, `creaturesDiedThisTurn`, `countersRemovedThisWay`, `rolled`,
  `opponentsDealtCombatDamage`.
- **Subjects**: `defendingPlayer`, `{ "milled": f }`, `enteredThisWay`, `exceptChosen`.
- **Filters**: `notSubtypes`, `zone`, `hasFlashback`; target kinds `ability`, graveyard-or-exiled card; `targetsTotalManaValueAtMost`.
- **Triggers**: `youSurveil`, `dealsDamage` (once per event, the total), `attachedDealtDamage`, `equippedAttacks`,
  `equippedCombatDamageToPlayer`, `opponentGainsControlFromYou`, `chosenCreatureLeaves`, `chosenPlayerLoses`, `asTransformsInto`,
  `postcombatMain`, `mainPhase`, `cardPutIntoGraveyard`; `fromCommandZone` (eminence).
- **Conditions**: `opponentsCreaturesDied`, `lifeAtMostHalfStarting`, `playerDealtCombatDamage`, `opponentDealtCombatDamageByLegendary`,
  `firstCombatPhase`, `yourTurnNumberAtMost`, `crewedBySource`, `duringCombat`, `addedManaWithThisThisTurn`.
- **Static abilities**: `extraTriggersAttached`, `grantsDyingTriggerBonus`, `removeTypes`; `become` with `whileCounter`,
  `loseLandTypes`, `grantsMana`.
- **Card-wide**: `payLifeForBlackMana`, `winsDrawingFromEmptyLibrary`, `extraLandPlays`, `opponentsCastAtSorcerySpeed`,
  `opponentsCantCastNoncreatureSpells`, `libraryTopSpellsForLife`, `tripleManaFromPermanents`, `extraManaFromNonlandPermanents`,
  `extraTriggersFromAttacking`, `playFromGraveyardOnYourTurn`, `playFromLibraryTop` / `playFromLibraryTopIf`, `graveyardArtifactsForLife`,
  `wardLifeFromPower`; `replaces` `ExtraColorlessWhenLandTappedForColorless`, `FirstCoinFlipEachTurnWins`, `OpponentsMillFourMore`,
  `OpponentsNontokenCreaturesExiledGainTwoLife`, `DamageNotRemovedInCleanup`, `RedirectYourDamageToSelf`, `DoubleWizardDamage`,
  `DoubleCreaturePlusOneCounters`, `ExtraFrogWithTokens`, `ExtraTriggersFromLandsAndBirdsEntering`, `YouHaveProtectionFromOpponents`,
  `YourLandsEnterUntapped`, `ExileInsteadOfYourGraveyard`.

## Commander decks of the heroes set

- **Keywords** (from the card's keyword line; no script key): `Intimidate` (rule 702.13: can't be blocked except by artifact
  creatures and/or creatures that share a color with it), `Melee` (702.121: +1/+1 until end of turn for each opponent attacked
  this combat), `Evolve` (702.100: a built-in trigger with its intervening "if"), `Training` (702.149), `Convoke` (702.51: each
  tapped creature pays {1} or one mana of its color), `Unearth [cost]` (702.84a), `Rebound` (702.88a: the delayed cast from exile
  without paying its mana cost), `Foretell {cost}` (702.143: the cost to cast it from exile on a later turn).
- **Blitz** (rule 702.152): `"blitz": cost` (the mana part), `"blitzExtra": cost` (the non-mana part), `"blitzFromGraveyard": true`
  (may be cast from the graveyard using blitz). The keyword gives haste, a draw when it dies and a sacrifice at the next end step.
- **Keyword actions**: `"adapt": N` (701.46a: N +1/+1 counters if it has none) and `"monstrosity": N` (701.37a: N counters, and it
  stays monstrous until it leaves the battlefield); both take `"what"` (default: the source). `"explore": subject` (701.44a: a land
  goes to hand; otherwise a +1/+1 counter, and the card may go to the graveyard). `"connive": subject` (701.50a: draw, then discard;
  a nonland discard puts a +1/+1 counter). `Support N` is a keyword action whose counters and targets are ordinary script effects.
- **Shield counters** (kind `shield`, rule 122.1c): damage dealt to the permanent is prevented and one shield counter is removed. A
  destroy effect removes one instead; when that and regeneration both apply, the controller chooses.
- **Effects**:
  - `"sacrificeAllBut": N` with `"filter"`, `"who"` (default `you`), `"exactly": true` (keep as many as they can, up to N, not fewer),
    `"putCounter": kind` (a counter on each kept permanent) and `"keptCantAttackYou": true` (with `putCounter`: the kept permanents
    can't attack you or your planeswalkers while they have it). Each player chooses in turn order, then all sacrifice at once (101.4).
  - `"opponentSplitsExiled": true`: an opponent chooses one of the cards exiled with the cost; you put it on the bottom of your
    library and return the others tapped. Only cards still in exile are chosen or returned.
  - `"opponentsChooseFromYourGraveyard": filter`: starting with the next opponent in turn order, each opponent chooses one matching
    card in your graveyard that hasn't been chosen; those return to the battlefield under your control.
  - `"exileRandomFromOpponentsGraveyards": true`: exile a random card from each opponent's graveyard.
  - `"castExiledFreeAnyNumber": true` with `"ownersLoseLife": true`: cast any number of the exiled cards without paying their mana
    costs; with `ownersLoseLife`, each owner loses life equal to the mana value of their card cast this way.
  - `"chooseOneOf": subject`: the player chooses one of the objects in the subject ("return one of them").
  - `"exileWithSameNameTokens": subject`: exile the target and all tokens its controller has with the same name (one event).
  - `"putExiledWithThis": filter` with `"counterKinds": [kinds]`: put one matching card exiled with this object onto the battlefield
    under your control, with those counters.
  - `"whenDiesThisTurn": subject` with `"effects": […]` and `"text"`: a delayed trigger about that object (rule 603.7). The default
    text is "When that creature dies this turn".
  - `"attackTaxUntilYourNextTurn": "{cost}"`: creatures can't attack you unless their controller pays the cost for each.
  - `"distributeCountersFromSelf": kind`: move any number of those counters, one creature at a time, onto other creatures.
  - `"becomeCopy": subject` with `"target"` (default: the first target), `"exceptName"`, `"lasting": true`, `"keywords": […]`
    and `"keepThisAbility": true`. Without `"lasting"` the copy lasts until cleanup. `"lasting"` gives no duration ("you may have
    this become a copy of that creature"); a later lasting copy overrides an earlier until-end-of-turn one. An until-end-of-turn
    copy over a lasting copy restores the lasting copy at cleanup. `"keywords"` and `"keepThisAbility"` are exceptions to the copy
    ("… and it has vigilance and this ability"), part of its copiable values (707.9b).
  - `"exileOneFromEachGraveyard": filter`: the controller chooses one matching card in each graveyard that has one; they are
    exiled together, linked to this object.
  - `"warpStrike": "self"`: exile it. At the beginning of its owner's next declare attackers step it returns under the owner's
    control, tapped and attacking (the owner chooses the player it attacks), and it can't be blocked that combat. A creature put
    onto the battlefield attacking doesn't skip the blocker and damage steps unless no creature was declared as an attacker or put
    there attacking (508.8).
  - Engine (no key): a creature put onto the battlefield attacking (tapped-and-attacking tokens, `putFromHand` attacking,
    warp strike) has its controller choose any opponent, or a planeswalker an opponent controls, for it to attack (508.4). It isn't
    limited to the players the other attackers attack.
  - `"adventureFromGraveyardUntilYourNextTurn": subject` (on a dies trigger): the card may be cast from its owner's graveyard only as
    its Adventure, until the end of its owner's next turn.
  - `"playExiledWithThisThisTurn": true` with `"costLess": N`: until end of turn the controller may play the cards exiled with this
    object, including cards exiled later that turn (607.2a). Spells cast this way cost {N} less.
  - `castExiledNow` takes `"oneOf": filter` (the caster picks one of the matching cards exiled with it that can be cast, or none)
    and `"anyMana": true` (mana of any type can be spent to cast it).
  - `exileTopFaceDown` takes `"optional": true`: look at the top card of your library; you may exile it face down. The player decides
    after looking, and can still look at it in exile.
  - `"moveCounters"` takes `"count": 1` (in the move, exactly one counter of a kind the player chooses, when there is one).
  - `"choose"` takes `"perOpponent": true` (one choice for each opponent, among what that player controls).
  - `mayPay` takes `"payZero": true` (with `lifeFrom`: X may be 0, and paying 0 life still does the effects).
  - `exileTopPlayable` takes `"untilAnotherExiled": true` ("you may play that card until you exile another card with this").
  - Modes: `"randomMode": true` on an ability or spell with `modes` (choose one at random, rule 700.2).
- **Subjects**: `exiledWithThis` (cards in exile that the source object exiled, rule 607.2a), `sourceController` (the controller of
  the source, e.g. "your graveyard" inside "each opponent …"), `convokers` (the creatures that convoked the spell, 702.51c),
  `opponentsWhoDiscardedHand` (the opponents who discarded their hand this way).
- **Quantities**: strings `colorsAmongYourPermanents` (0 to 5), `counterKindsOnSelf` (kinds of counters on the source),
  `playersWhoLostLifeThisTurn`, `greatestPowerAsCast` (the greatest power among your creatures as you cast the spell),
  `triggeredCounterCount` (counters of every kind on the object the trigger is about, as it last was on the battlefield),
  `sacrificedManaValue` (total mana value of the permanents sacrificed this way), `opponentsCombatDamagedBySelfOrDragon` (opponents
  dealt combat damage this turn by the source or by a Dragon). Objects: `{ "manaValue": "exiled" }` (total mana value of the cards
  exiled this way), `{ "opponentsControlling": filter }`, `{ "diff": [a, b] }` (a minus b).
- **Conditions**: `castFromExile` (this spell was cast from exile, 702.143), `triggeredControllerHasMoreLands` (that player controls
  more lands than you), `youPutCounterOnCreatureThisTurn`, `counterPutOnSelfThisTurn` (a counter was put on this object this turn),
  `cardLeftYourGraveyardThisTurn`, `youLostLifeThisTurn`, `{ "playerLostLife": N }` (a player lost N or more life this turn),
  `fullParty` (rule 700.8), `attackersMatchChosenNumber` (the creatures attacking the player the trigger is about have power or
  toughness equal to the number chosen as it entered), `attachedToCreature` (the source is attached to a creature),
  `creatureBlocking` (a creature is blocking), `triggeredPlayerIsActive`, `{ "opponentGraveyard": N }` (an opponent has N or more
  cards in their graveyard), `{ "blockedAttackingYourOpponent": N }` (the attacker attacks one of your opponents or their
  planeswalker and is blocked by at least N creatures).
- **Triggers**: `youPlayLand` (whenever you play a land, rule 305.1: playing it, not putting it onto the battlefield; with `"filter"`
  `"fromExile": true`), `castOrCopy` (you cast or copy a spell matching the filter), `attackedUnblockedByOpponent` (once for each
  opponent with an unblocked creature attacking you; `"triggeredPlayer"` is that opponent), `youWinCoinFlip`,
  `opponentBecomesMonarch` (`"triggeredPlayer"` is that opponent), `creaturesTargetedByActivatedAbility` (once for each such
  ability; `"triggeredPlayer"` is its controller), and `countersPlaced` with `"perCounter": true` (once for each counter).
- **Filters**: `fromExile` (a spell cast from exile, or a permanent or land that entered from exile), `enteredOrCastFromGraveyard`,
  `enteredThisTurn` (a permanent that entered the battlefield this turn), `ownedByYou` (owned by the controller of the ability,
  whoever controls it), `amongFound` (one of the cards a reflexive ability carries, e.g. "target creature card exiled this way"),
  `modified` (rule 700.9: it has a counter on it, is equipped, or is enchanted by an Aura its controller controls), `blocked` (an
  attacking creature that a creature blocked this combat), `greatestManaValueAmongTheirs` (the creature with the greatest mana value
  among the creatures its controller controls).
- **Static abilities**: `"filter"` (a filter, see above) narrows the affected objects, e.g.
  `{ "static": { "affects": "creatures:you", "filter": { "token": true }, "pump": [1, 1] } }` ("creature tokens you control get +1/+1").
- **Card-wide and costs**: `"flashIfTargets": filter` (cast as though it had flash only if it targets a matching object);
  `"escalateExtra": cost` (a non-mana escalate cost, paid once for each mode beyond the first); `"castFromLibraryTopIf": condition`
  (cast creature spells from the top of your library while it holds); `"opponentsCantCastDuringYourTurnWhileAttached": true`;
  `"creaturePlusOneCountersPlusOne": true` (+1/+1 counters put on your creatures: one more than they would be);
  `"entersRemovingCountersFrom": filter` (as it enters, remove all counters from any number of matching permanents; it enters with
  twice that many +1/+1 counters); `"graveyardCastOncePerTurn": { "filter", "notFromBattlefieldThisTurn", "entersTapped" }` (once during each of your turns
  you may cast a matching spell from your graveyard; `notFromBattlefieldThisTurn` limits it to cards put there from anywhere
  other than the battlefield this turn; `entersTapped` makes a permanent cast this way enter tapped);
  `spellCost` takes `"nth": N` (only the Nth spell you cast each turn); `{ "spellsHaveImprovise": filter }` (ability: matching spells
  you cast have improvise while this is on the battlefield, 702.126).

## More vocabulary (turn tracking, loyalty, discards and quantities)

### Costs, tokens and card-wide rules
- **Discard this card**: `"discardThis"` in a cost (`"cost": "discardThis, {1}"`). The ability is activated from the hand, and the card
  is discarded as part of the cost.
- **Land tokens**: a token with a basic land type (`"types": "Land Creature — Forest"`) taps for that type's color, as a land does.
- **Token mana**: `"tapForMana": "{R}{G}"` (the token taps for one of those colors) or `"tapForMana": "any"` (any of the five colors).
  `"sacrificeForMana": true` with `"manaAmount": 3` makes its mana ability sacrifice the token and add that much mana (default 1).
- **Taxes on opponents**: `{ "static": { "spellCostIncrease": { "spells": { "controller": "opponent" }, "amount": 1 } } }` ("spells your
  opponents cast cost {1} more"). The filter's `controller` picks whose spells are taxed; the default taxes all spells.
- **Damage plus N, narrowed**: in `damageBonus`, `"noncombat": true` (only noncombat damage) and `"toOpponents": true` (only damage to
  an opponent of the source's controller, or to a permanent an opponent controls).
- **Extra prowess**: `"extraProwess": 1` gives one more instance of prowess ("Prowess, prowess"); each instance triggers.
- **Casting restriction**: `"castOnlyIf": condition` ("you can't cast this spell unless …"). It is checked for the caster, however the
  spell is cast.
- **Keyword Exhaust**: the script marks the activated ability `"onlyOnce": true`, so it can be activated once in the game.

### Triggers
- `youScryOrSurveil`: whenever you scry or surveil. One event for either.
- `youActivateLoyaltyAbility`: whenever you activate a loyalty ability. The amount is the loyalty counters removed to activate it.
- `opponentActivatesLoyaltyAbility`: whenever an opponent activates a loyalty ability. The triggered player is that opponent.
- `opponentDealtNoncombatDamage`: whenever an opponent is dealt noncombat damage, by any source. The triggered player is that
  opponent and the amount is the damage. Works with `"batched"`.
- `discardedThis`: when you discard this card, whatever caused it, from wherever it went.
- `playerDiscards`: whenever a player discards one or more cards. The triggered player is the discarder. Use `"batchedPerPlayer": true`:
  discarding several cards in one event is one event.
- `eachUpkeep` with `"fromGraveyard": true`: the ability works from its owner's graveyard, at the beginning of each upkeep.
- Cast triggers: `"spellTargetsOpponent": true` (the spell may target an opponent instead of a matching object, with `"spellTargets"`), and
  `"orSpellTargets": true` (the spell matches the filter or targets what `"spellTargets"` describes).

### Conditions
- `"scriedOrSurveilledThisTurn"`: you have scried or surveilled this turn.
- `"opponentDealtNoncombatDamageThisTurn"` / `"opponentDealtNoncombatDamageLastTurn"`: an opponent was dealt noncombat damage (from any
  source) this turn / last turn.
- `"activatedLoyaltyAbilityThisTurn"`: you have activated a loyalty ability this turn.
- `"dealtCombatDamage"` (on the source): the source has dealt combat damage since it entered. Use `{ "not": "dealtCombatDamage" }` for
  "hasn't dealt combat damage yet".

### Quantities
- `{ "divide": quantity, "by": 3 }`: the quantity divided by N, rounded down ("for every three …").
- Strings: `"cardTypesInAllGraveyards"` (card types among cards in all graveyards), `"drawnThisWay"` (cards drawn by an earlier effect of
  this spell or ability), `"domain"` (basic land types among lands you control), `"distinctPowers"` (different powers among your creatures).
- Objects: `{ "loyalty": "target" }` (loyalty counters on the first target), `{ "greatestManaValueInGraveyard": filter }` (the greatest
  mana value among matching cards in your graveyard, 0 if none), `{ "milledThisTurn": "target" }` (cards put into the first target
  player's graveyard from their library this turn).

### Filters
- `"preparedSpell": true`: a copy of a prepare spell cast from exile (see the preparation cards above).
- `"greatestManaValueAmongTheirs"` compares among the permanents of the filter's types, so with creatures and planeswalkers listed it
  compares both. Without types, it compares creatures.

### Effects
- `{ "loyaltyAtInstantSpeed": "Seer" }`: until end of turn, you may activate the loyalty abilities of planeswalkers you control with
  that subtype at instant speed, on any turn. Still one loyalty ability per planeswalker each turn.
- `{ "removeUpToCounters": 3, "from": subject }`: you choose how many counters of each kind to remove from each permanent of the
  subject (`"from"` defaults to `"target"`), at most 3 in all.
- `{ "nextSpellUncounterable": true }`: the next spell you cast this turn can't be countered.
