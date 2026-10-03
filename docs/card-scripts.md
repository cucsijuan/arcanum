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

A trigger can have an intervening condition, `"if": ...`: it triggers only if the condition holds, and does
nothing on resolution unless it still holds.

### Filters

```json
{ "types": ["instant", "sorcery"], "not": ["creature"], "subtype": "Elf", "controller": "you", "other": true, "minPower": 4, "token": false }
```

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

## Costs

Comma-separated: mana symbols (`{2}{R}`), `{T}` (tap this permanent) and `sacrifice` (sacrifice this permanent).
