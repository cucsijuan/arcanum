# Card scripts

Content modules describe what cards do with small JSON files in `scripts/`, one per card, named after the
card's oracle id (`scripts/<oracle-id>.json`). A card needs a script only when its rules go beyond what the
engine derives from card data: type line, power/toughness, supported keywords and mana abilities written as
`{T}: Add {G}` / `{T}: Add {R} or {G}` / `{T}: Add one mana of any color`.

Comments (`//`) and trailing commas are allowed.

## Structure

```json
{
  "spell":     { "targets": [...], "effects": [...], "text": "..." },
  "abilities": [
    { "trigger": "enters", "targets": [...], "effects": [...], "text": "..." },
    { "cost": "{1}{R}, {T}", "targets": [...], "effects": [...], "sorcery": false, "text": "..." }
  ]
}
```

- `spell`: what an instant or sorcery does when it resolves.
- `abilities`: a permanent's abilities. An entry with `trigger` is a triggered ability; one with `cost` is an
  activated ability (`"sorcery": true` limits it to sorcery timing).
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
| Tokens | `{ "tokens": 2, "token": { "name": "Soldier", "types": "Creature — Soldier", "power": 1, "toughness": 1, "keywords": [] } }` |

## Triggers

| Value | When |
|-------|------|
| `enters` | This permanent enters the battlefield |
| `dies` | This creature goes from the battlefield to a graveyard |
| `attacks` | This creature attacks |
| `upkeep` | At the beginning of your upkeep |
| `endStep` | At the beginning of your end step |
| `combatDamageToPlayer` | This creature deals combat damage to a player |

## Costs

Comma-separated: mana symbols (`{2}{R}`), `{T}` (tap this permanent) and `sacrifice` (sacrifice this permanent).
