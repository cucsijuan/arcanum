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
| M10.5 | Complete support for a second full card set and its eternal companion set: adventurer cards, Sagas, amass, storied and enduring stories, recruit, gift, behold, typecycling, hone and keyword counters, attack taxes, abilities that trigger an additional time, the Ring tempting you, phasing, cascade, landwalk and shadow, and the set's booster; then a third full set built on the same mechanics (Ring-bearers chosen each time, keyword counters, lasting control, goad, delayed abilities, token and life replacements), two commanders (partner, partner with, friends forever, Backgrounds, Doctors) and player tags on the board | done (in testing) |
| M10.6 | The third set's commander companion set and its four preconstructed decks: the monarch, voting, timestamped control effects, attack restrictions, commander-identity mana, multikicker, replicate, squad, storm, dash, delve, conspire, miracle, splice, split second, exert, devour, persist and undying, renown, exalted, regeneration, hideaway, suspend, split cards with aftermath, snow mana, and four-player Commander soak tests | done (in testing) |
| M11 | Online services: lobbies and lobby browser, invite codes, NAT traversal and relay, groundwork for advanced online modes | done (in testing): local-network and internet lobbies (device accounts, lobby directory, peer-to-peer with relays), lobby browser and invite codes (see online-services.md) |
| Career | Career mode, a life of a card player (plan in [career-mode.md](career-mode.md)): C1 offline core (starting deck, daily matches, store with boosters and preconstructed decks, collection, deck building on the desk, selling cards); C2 practice, ante, challenges, offers, rivals; C3 calendar events (draft night, commander night, tournaments), market prices, foils and every booster kind; C4 Discord sign-in, career service, remote season and its editor, cloud saves | C1–C4 done (in testing); C5 online events next |
| M12 | Mobile: touch input, responsive layout, Android and iOS builds | planned |
| M13 | Plugin SDK: sandboxed Lua game-mode plugins distributed with online games | planned |
| M14+ | Dedicated headless server, more card coverage, stronger computer opponent, puzzles, statistics, visual themes | planned |

## Backlog

- Career, online (with the remote season config): **superdrops**, the way the real limited-run drops work. A drop
  is announced and sold for a limited time, very expensive: singles, card packs, decks and Commander decks (anything
  those drops ever sold). The developer controls each drop's title and which cards are in it from the remote config.
  The week after a drop, some of its cards show up on the store's secondary market as singles, at a higher price per
  card than in the drop.

- **Urgent:** the third set's set booster is sold without its reprint-list slot (a card from a list of older cards in
  a quarter of the boosters). Its 250 cards need scripts first, and some need new mechanics: banding, infect,
  cumulative upkeep, buyback, rebound, convoke, domain, assist and others. Then add the slot back to the module's set
  booster (the booster definition it was converted from has it).

- Code review after M11: rules fixes, duplicated paths and hygiene, prioritized in [review-backlog.md](review-backlog.md).

- Online: the host's device has all the game's information (a modified host could see hidden cards); a dedicated
  headless server (M14+) removes that.
- Online: spectators.
- Online: chat between players.
- Online: an opponent's custom playmat image is sent to the other players and shown on their side of the table; it is
  only cached for that game (never added to this player's own playmat choices). Today only built-in playmats are shared.
- Online events: show opponents' deck colors in the standings once their decks have been seen.
