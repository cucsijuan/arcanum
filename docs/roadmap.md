# Roadmap

| # | Milestone | Status |
|---|-----------|--------|
| M0 | Project setup: solution, tests, CI | done |
| M1 | Rules engine core: turns, priority and stack, combat, state-based actions, mulligans | done |
| M2 | Game board: hand, zones, life, card preview, hotseat play | done |
| M3 | Priority flow: stops, end turn, mana payment, targets, stack view, undo, drag to play | done |
| M4 | Card content pipeline: content modules, card data import, keywords, abilities, static effects, card scripts and script generator | done |
| M5 | Main menu, deck builder, formats, settings | done |
| M6 | Computer opponent | done |
| M7 | Commander rules and multiplayer (up to four players), readable play (announcements, pacing) | done |
| M8 | Complete support for a first full card set: every card of the set playable (new keywords, keyword actions, conditions, alternative costs, modal spells, library and graveyard effects) | done |
| M9 | Limited: draft and sealed (booster generation, draft bots, cube draft, limited deck building, Swiss rounds); card printings and sets (exact art, set filter in the deck builder) | done |
| M10 | Online play, direct connection: a player hosts and shares an address; host-authoritative games with hidden information, every game mode including limited events, reconnection, computer takeover of disconnected seats, resuming after the host drops, decision time limit | done (in testing) |
| M10.5 | Complete support for a second full card set and its eternal companion set: adventurer cards, Sagas, amass, storied and enduring stories, recruit, gift, behold, typecycling, hone and keyword counters, attack taxes, abilities that trigger an additional time, the Ring tempting you, phasing, cascade, landwalk and shadow, and the set's booster | done (in testing) |
| M11 | Online services: lobbies and lobby browser, invite codes, NAT traversal and relay, groundwork for advanced online modes | planned |
| M12 | Mobile: touch input, responsive layout, Android and iOS builds | planned |
| M13 | Plugin SDK: sandboxed Lua game-mode plugins distributed with online games | planned |
| M14+ | Dedicated headless server, more card coverage, stronger computer opponent, puzzles, statistics, visual themes | planned |

## Backlog

- Online: the host's device has all the game's information (a modified host could see hidden cards); a dedicated
  headless server (M14+) removes that.
- Online: spectators.
- Online: chat between players.
- Online events: show opponents' deck colors in the standings once their decks have been seen.
