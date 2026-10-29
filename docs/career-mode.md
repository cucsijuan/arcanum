# Career mode — plan

A menu-driven "life of a card player" mode: the player starts with a starter deck and a little cash, plays free daily
matches to earn money, buys boosters and preconstructed decks, builds decks **only from the cards they own**, and climbs
from casual store nights to the big tournaments. Offline it is a single-player loop against computer opponents; online,
scheduled weekly/monthly events are configured remotely and played against other players (or against bots for smaller
rewards, when the event allows it).

This plan takes the original design notes as direction, not as a spec.

## Pillars

1. **Only the card game.** Progress comes from playing, opening, trading and competing. No jobs, no map, no survival.
2. **No dead ends.** Two free daily matches every day; a player with nothing can always rebuild.
3. **A collection that matters.** Decks are built from owned cards; every booster opened is a decision (keep, sell, trade).
4. **A calendar with appointments.** Fixed events during the week give each day a reason to come back.
5. **Remote control of the season.** Events, prizes, featured sets and PvP/PvE rules come from a config file the
   developer publishes, without a new release.

## The loop

```
day starts (real day, UTC reset)
  ├─ 2 daily matches (free)        → cash or a booster each (win pays more than loss, but a loss still pays something)
  ├─ practice matches (unlimited)  → no cash; small reputation, deck testing; first few per day give a little cash
  ├─ ante matches                  → each player puts a card in the pot, winner takes both
  ├─ store                         → boosters, preconstructed decks, singles; sell cards
  ├─ today's event (if any)        → entry fee → prizes, reputation, rank points
  └─ collection / decks            → deck builder limited to owned cards
```

### Daily matches

- 2 per real day, reset at 00:00 UTC (one reset time for everyone, so online events and dailies line up).
- Opponent from the store's roster (see *Opponents*), deck strength scaled to the player's reputation tier.
- Reward chosen before the match ("cash" or "booster of the featured set"), so the choice is visible.
- Missed days are not banked (keeps it a daily habit, not a backlog).

### Grinding (the "a little" part)

- **Practice:** unlimited, rewards decay: first 3 per day pay a small amount, then only reputation/experience. Useful
  to test decks without risk.
- **Ante:** the old ante rule, done properly: before the game each player reveals a random card from their deck (or the
  player picks one from a short offer when the opponent proposes a "stake"); winner keeps both. The opponent's stake is
  shown before accepting. Valuable stakes only from opponents of the same tier, so it can't be farmed against weak bots.
- **Challenges** (instead of more currencies): rotating small goals ("win a daily with a deck worth less than $20",
  "win with a mono-colored deck", "draft a 3-0"), each paying a booster. Gives the grind a direction.

### Store and economy

- **Cash ($)** is the only currency. No real money anywhere, nothing purchasable outside the game.
- **Boosters** from the content module's sets that have a booster definition; sets without one (companion/commander sets)
  are sold as their preconstructed decks. The store has a *featured set* each week (from the config).
- **Playmats:** the store sells the game's built-in playmats, the picture playmats among them (prices in the module's `career/career.json`, `playmats`).
  A career starts without one. Buying a playmat puts it on the table at the player's matches and events; an owned one
  can be swapped in or taken off. Each regular and visitor keeps the same choice every time, from their name, but it depends on how good they are.
  Level: visitors and lowest-tier regulars 0, the top tier 1, rivals one tier higher. They play without a playmat with
  chance 60% × (1 − level) (`NoPlaymatChance`). Otherwise they get one of a window of half the store's playmats, sorted
  by price, sliding from the cheaper half (level 0) to the dearer half (level 1).
- **Singles:** buy specific cards at their price (with a markup), sell at a lower price (buy/sell spread so flipping
  doesn't print money).
- **Card prices:** derived in-game from rarity, set and in-game demand (how often the card shows up in winning decks of
  computer opponents and events), with slow drift. Not real-world prices.
- **Trades with opponents:** an opponent offers specific cards for specific cards (they want what fits their deck),
  priced so the trade is fair ±10%.
- **Opportunity offers** (the "random dilemmas"): rare one-off offers when entering the store (a card below price, a
  stake match, a sealed booster box at a discount). One choice, then gone.

### Reputation and tiers

Reputation from playing (dailies, events, challenges) unlocks tiers; tiers unlock events and stronger opponents.
Example: *Newcomer → Regular → Local champion → Circuit player → Pro*. Tiers never go down (reputation is cumulative);
**rank** (Elo-like, per format) is the competitive number that goes up and down.

## Calendar and events

Every event is defined by data (built-in default schedule in the content module, overridden by the remote config):

| Field | Example |
|---|---|
| `id`, `name` | `draft-night`, "Draft night" |
| `when` | weekly on Wednesday 18:00–24:00 UTC; a weekday of the month (`firstFriday`, `secondX`, `thirdSaturday`, `fourthX`, `lastSunday`); or a date |
| `months` | limits the event to those months (numbers) |
| `days` | the run's length in days from its `when` day (default 1) |
| `kind` | constructed (format id), commander pod, draft, sealed, prerelease |
| `format` | constructed events: a module format id (`standard`, `modern`…); empty: any cards (casual). Commander events use the Commander format |
| `set` | set code for limited events / featured set |
| `entry` | cash, boosters, or a qualification token |
| `prizes` | by place or by record (3-0, 2-1…): cash, boosters, promo, qualification token, rank points, reputation; `minPlayers` (only with that attendance); `qualifies` (an event id: invites the player to that event's next run) |
| `minTier` | reputation tier required |
| `modes` | `pve`, `pvp`, or both; `pveRewardScale` (e.g. 0.5) |
| `rounds`, `bestOf`, `podSize` | Swiss rounds, Bo1/Bo3, Commander pod of 4. `rounds: 0`: rounds by attendance (2 for up to 4 players, 3 up to 8, 4 up to 16, 5 up to 32, 6 up to 64, 7 up to 128, 8 up to 226, 9 up to 409, 10 beyond) |
| `players`, `playersMax` | attendance: a number drawn from the run's seed between `players` and `playersMax` |
| `topCut` | a single-elimination playoff of the top N of the Swiss standings (seeded 1v8, 4v5, 2v7, 3v6) |
| `roundMinutes` | the Swiss rounds' time limit in minutes (0 or absent: untimed); the playoff is always untimed (see *Round time* in *Circuit*) |
| `cutAfter`, `cutPoints` | a cut by record after that many rounds (day 1): only players with `cutPoints` or more play on |
| `invitational` | only players invited by a prize's `qualifies` enter; entering uses up the invitation |
| `tournamentMat` | the games are played at the big tournaments' table, on one mat for both players with the event's name on its crest (see *Look*) |

An event's format decides which decks it takes. Entering checks the player's deck against it (`CareerRules.Enter`, and
the same check when a journal is replayed). The computer players bring module decks legal in the format, any of their
own when none is.

Default week (illustrative):

| Day | Event | Entry | Notes |
|---|---|---|---|
| Every day | 2 daily matches | free | |
| Wednesday | Draft night | $15 or 3 boosters | featured set |
| Friday | Commander night | $5 | 4-player pods, social reputation |
| Saturday | Weekly tournament | $20 | constructed, Swiss, rank points |
| Last Sunday | Monthly championship | $25 | local, any cards, open to any reputation, no qualification; first place $150 and 5 boosters; earns the money for the regional qualifier |
| First weekend, monthly (Friday to Sunday) | Regional qualifier | entry fee (`career/career.json`) | offline, three days; the winner (and the finalist with 33 or more players) is invited to the regional championship; see *Circuit* |
| Third weekend of March, June, September, December (Saturday and Sunday) | Regional championship | entry fee (`career/career.json`) | by invitation; offline, two days; 14 Swiss rounds with a cut after round 8; online slots are design (C6, see *Circuit*) |
| Seasonal | International tournament | qualification token | PvP only (config); not built yet (C6) |

Offline, events are always PvE. An event with `online` (start times UTC, check-in minutes, `pvpOnly`, `offlineScale`)
is also played online at those times (empty seats get computer players); unless it's PvP only, it can be played
against the computer any time of its day for `offlineScale` of the prizes. Only draft events can be entered online
for now. A player who entered online can also play it against the computer at once, for that share (not for PvP-only
events, which show as online only).

**Prerelease:** when the content module gains a new set, a sealed prerelease event for it runs for a week. A natural
hook: new content → new event.

## Opponents

- A roster of named store regulars (invented names and portraits), each with a style (aggro, control, commander
  enthusiast, collector) and a deck per tier, built from the same card pool.
- **Rivals:** a few regulars keep their own small collection and improve their decks over the season (they "buy" cards
  too), so the local meta changes and beating a rival feels earned.
- Strength comes from deck quality first (the bot is the same), plus bot settings as they become available.
- **Drafting:** in a career draft the computer seats draft with a skill: regulars 0.4 to 0.9 by the tier it takes to meet
  them (rivals +0.1), visitors 0.25. Skill sets how far they follow the module's draft statistics and how often they
  misjudge a pick. Their colours and decks stay playable at any skill.
- **Draft grades:** in career drafts the player sees each card's grade and in-hand win rate from the module's draft
  statistics, with the credit line (`DraftRatingsForPlayers` in `CareerService.cs` switches this off; the computer
  seats always use the statistics). A set without statistics shows no grades.

## Look

Pixel-art, warm and cozy, on pictures that fill the window (not the menus of the other modes), with a pixel font.
The art is in the private art pack under `kit/career/`, each picture with a `.json` of its hotspots (F3 in the game
shows them and the pointer's position; Ctrl+click copies a point).

- **The room (home):** `room.jpg`. The computer opens the store's website, drawn crisp over the dimmed room (an old
  monitor growing from where it stands): daily matches, events, the forum's buy and sell board, the "my log" page (unopened
  boosters, news), and signing up for a new career. The boxes of cards on the shelf open the decks; the stack on the desk
  opens "my cards", the collection in the desk's collection mode (every owned printing with its copies and price, scenes,
  and right-click to sell on the forum).
- **The store:** `store.jpg`. The booster rack, the preconstructed decks and the counter (selling cards) open paper
  notes over the dimmed picture. The playmats on the shelf above the computer (hotspot `mats`) open a note listing
  each mat with a preview, *Buy*, *Use* or *On the table*, and *No playmat*. Esc goes back home.
- **Hotspots** glow along their outline under the pointer, show their name, and follow the arrow keys and Enter.
- **The table:** `table.jpg` spans the whole board in daily matches, with the divider between the two seats a little
  above the middle (so the player's three rows fit on the table above its front edge). Each seat's playmat lies on it
  with rounded corners, a shadow and an edge, and that player's rows of cards are laid out on it (the player's mat may
  reach over the divider). Where the mats lie comes from `matTop` and `matBottom` in the JSON next to the table picture:
  `[x, y, w, h]` fractions of the whole board. A picture playmat shows its picture (from the interface art, see
  [building.md](building.md)); otherwise a picture in `kit/career/mats/<id>.jpg` or `.png` replaces a mat's print.
- **The tournament table:** an event with `tournamentMat` (the regional championship) is played at `regional-table.jpg`,
  the big tournaments' hall, on one mat for both players instead of each seat's playmat. The mat covers the whole table
  top (area `tournamentMat` in `regional-table.json`, `[x, y, w, h]` fractions): dark grey with a stitched edge, and a
  band across the line between the two players (outside in: black, white, black, then red, the same on both sides). Its
  crest in the middle is the shield picture (`kit/career/tournament-shield.png`) with the event's name in front, the
  first word above and the rest below, in Bebas Neue (OFL, `fonts/bebas-neue`), white with a black outline. The crest is
  drawn at runtime, so any event name fits. The event screen shows this table too. The table an event is played at comes
  from `CareerService.EventTable`; the board draws the mat from `MatchSetup.TournamentMat` (`TournamentMat.cs`).
- **The broadcast overlay:** on the games of an event with `roundMinutes` or `tournamentMat` (`BroadcastOverlay.cs`). A
  clock tab sits on the line between the two players, at the right edge of the mat, with the event's name above it. The
  tab shows the round's name ("Round 3", or "Quarterfinals" in the playoff) and the time left (mm:ss). After time is
  called it shows "T0/5" to "T5/5" (the additional turn in progress), and "Untimed" in the playoff. Each player has a
  plate: the opponent's at the top left, the player's at the bottom left. A plate has a red stripe, the name, the
  record in the event before this match, and a dot for each win the match needs, filled for each game won. The
  overlay is set in Bebas Neue and the mat's colours. When a lifted card is over the overlay's panels, the overlay
  goes under it (as with the phase bar). The overlay calls time on the game when the round's deadline passes. Its
  data comes from `MatchSetup.Broadcast` (`RoundBroadcast`, `BroadcastPlate`).
- **Deck building:** the desk with the computer seen from above (`desk.jpg`, its playmat's area in `desk.json`): the
  deck's controls on the wood above the mat, the cards owned on the left of the mat, the deck on the right in columns
  by mana value, basic lands (free) and the deck's state on the wood below. The format picker above the mat lists every
  constructed format of the module (casual, standard, pioneer, modern, legacy, vintage, pauper, commander) and saves the
  deck with that format's id. The deck's problems are checked against that format (casual and commander use the
  career's own rules). The owned cards are grouped by canonical
  name (a two-faced card saved under either name counts once). Playmats will be changeable: a picture
  in `mats/` is drawn over the mat's area (`mats/default.jpg` for now).
- **Commander suggestions:** on a Commander deck with its commander chosen, and a module with the commander statistics,
  a *Suggestions* button sits in the top row. It swaps the filters for a group, sort, variant (all decks, budget,
  expensive, a theme), *Only mine* and *Use average deck*. The mat shows every suggested card (basic lands left out),
  each with its inclusion and synergy. Cards not owned are dimmed, marked *Not yours* and can't be added. The note below
  the mat gives the decks behind the numbers and the average deck's card types beside the deck's. *Use average deck*
  replaces the main deck with the average deck using only owned copies (basic lands free) and lists the cards left out.
  Testing: `ARCANUM_SUGGEST=1` with `ARCANUM_OPEN=career` and `ARCANUM_CAREER_PAGE=decks` opens it for the first career deck.
- **Casual deck suggestions:** on a deck of any format other than Commander and a module with the deck search source,
  the same *Suggestions* button sits in the top row. The decks like it are looked for among community decks of the
  deck's format (a casual deck: any 60-card format, up to 90 cards with the sideboard). The variant list shows each deck with the share of its cards the player owns
  (`· 70% yours`), the decks with the most owned cards first. Cards and decks are marked and built as for a commander:
  cards not owned are dimmed and can't be added, and *Use average deck* or a chosen deck is built from the cards owned.
- The art must stay generic: no recognizable toy, game or brand look-alikes (the store's wall is plain where a toy
  look-alike was).

## Online

### Remote config (EOS)

- **Season file** (served by the career service, not EOS Title Storage: the service needs the same file to verify
  careers, so it lives in one place): the module's `career/season.json` lists changes, each from a day on (to a last
  day, optionally): events added, replaced (same id) or cancelled; the featured set; booster prices; a line of news.
  The season editor plans it (see *Season editor*).
  `server/Arcanum.CareerService/deploy/publish-season.sh <module dir> [user@host] [season file]` checks it (`tools/SeasonCheck`: everything it
  names exists, and no day already played changes, since careers are replayed with it) and puts it on the service,
  which serves `/career/season/<module id>.json` as a static file. The game downloads it when it changed (at start;
  the screens refresh when it arrives) and keeps it; offline it uses the last one, or the module's own.
  `schemaVersion`: a file newer than the game understands is ignored (the last one stays).
- **Player Data Storage** (per-user files): cloud copy of the career, so it survives reinstalls and moves between
  devices with the player's account (see *Accounts*). The career's folder (save, career decks, an event in progress)
  goes up as one zip (`career.zip`) a few seconds after each change and when the career opens. The journals decide:
  the one that continues the other wins (a new device takes the cloud's); with the same journal the newer files win;
  journals that went apart ask the player which to keep, and the other is kept as a backup zip in the career's folder.
  Ending a career ends the cloud's too. Needs the product's encryption key (`EOS_ENCRYPTION_KEY`) and a client policy
  with Player Data Storage; without a Discord sign-in the career stays on the device.
- **Leaderboards:** the career service's rank and this month's points, from online matches (see *Scheduled online
  events*). Per format, event wins and season standings aren't built yet (C6).
- **Server time:** dailies and event windows use the service's time when online (learned from the season download's
  answer); offline uses the local clock but never goes backwards from the last time seen.

### Season editor

Started with `tools/season-editor.sh [module dir] [user@host]`, a page on this computer. It plans the season on a
calendar: store events, the season's changes, cancelled events, featured set, prices and news per day. It starts from
the published season and keeps a draft on this computer (`~/.local/share/arcanum-season-editor/<module id>.json`,
dropped once published). It downloads the season as a file and publishes it; it never writes the module (its
`career/season.json` is replaced by hand when wanted).

- **Day tab:** clicking a day lists every event of that day: store events from the module, events changed or added by
  the season, and cancelled ones. Past days are read only.
- **Store events:** *Edit this day only*, *Edit from this day on*, *Cancel this day* and *Cancel from this day on*.
  The editor makes a season change for those days, with a copy of the event under the same id, which replaces the
  store's event.
- **Events of a change that hasn't started** are edited in place. A change that has started can't be changed.
- **Cancelled events** can be restored while their change hasn't started.
- **All changes tab:** the list of every change.
- **Formats:** a constructed event has a *Format* selector: *Any cards (casual)*, or one of the module's formats with a
  legality list (Commander and limited formats aren't listed). The season check reports unknown formats, and a format
  on an event that isn't constructed. To switch a format off (for example for security), edit the events that use it
  from the editor.

### Accounts

Players sign in to EOS with an external account instead of a Device ID, so their identity (and career) follows them
across devices and reinstalls.

- **Identity = the EOS product user id.** EOS Connect gives one product user id per player; every external account
  linked to it signs in to the same id. The career service and Player Data Storage key everything by that id, so
  linking or unlinking accounts never moves a profile.
- **First provider: Discord** (`ExternalCredentialType.DiscordAccessToken`).
  - Discord application (developer portal) with the `identify` scope only (no email, no guilds).
  - The game opens the browser for Discord's OAuth2 authorization (PKCE, redirect to a loopback address
    `http://127.0.0.1:<port>/callback` the game listens on for that moment). The career service exchanges the code for
    tokens, so the Discord client secret stays on the server; the game gets the access token and a refresh token.
  - EOS Developer Portal: Discord added as an identity provider for the product (Discord application id) and allowed
    in the client policy.
  - The refresh token is stored on disk readable only by the user (`user://account.json`); when EOS warns that the login is about to expire, the game gets a
    new Discord access token and logs in again without asking the player.
  - Confirmed (2026-10-11): EOS accepts the access token of the plain OAuth2 flow (the SDK documents the one the
    Discord SDK returns; both are OAuth2 access tokens of the same Discord application).
- **More providers later: Epic, Google** (`Epic` / `EpicIdToken` through Epic Account Services, which needs Epic's
  approval of the product; `GoogleIdToken`). Apple (`AppleIdToken`) becomes mandatory when the game ships on iOS with
  other sign-in options.
- **Linking, any combination:** Settings → Account lists every provider with Link/Unlink. A player may link one or all.
  - Linking: sign in with the new provider while signed in; if that external account has no EOS user yet,
    `LinkAccount` attaches it to the current one.
  - Already in use: if the external account already belongs to another EOS user (another profile), it can't be
    linked; the player is told and may switch to that profile instead (EOS has no account merge).
  - Unlinking is refused for the last linked account, so a player can't lock themselves out.
  - Signing in on a new device with a provider that has no EOS user yet asks: *new profile* or *link to an existing
    one* (sign in with a provider already linked there first).
- **Display name:** after signing in with Discord, its display name (or user name) is offered as the player's name
  when it's another one; the player name setting keeps working and can be changed.
- **Device ID:** no longer used for players. Kept only behind a developer switch (`ARCANUM_EOS_NEW_DEVICE` and similar)
  so two instances can be tested on one computer without two Discord accounts. Online play through the internet
  requires a signed-in account; local-network play and everything offline do not.
- **Career service auth** doesn't change: it verifies the EOS Connect ID token, which is the same whatever provider
  was used to sign in.

### Superdrops

Limited-time drops controlled from the remote config (the developer sets each drop's title and contents), laid out
like the real limited-run drops: singles, card packs, decks and Commander decks, very expensive, sold only while the
drop lasts. The week after, some of the drop's cards appear on the store's secondary market as singles, dearer per card
than in the drop. Their art and finishes are the real ones (printings with their own art, foils where they were foil).

### Scheduled online events

Built (C5) for draft events with an `online` spec. Other kinds can't be entered online yet (constructed and Commander
online come later).

- **Entering:** *Register online* on the event's forum post. It needs a career the career service has checked (see
  *Trust*). The entry is an `EnterEvent` with `Online`.
- **Check-in:** from `online.checkIn` minutes before each start (default 30) until the start, for the start's time
  in `online.starts` (UTC, `HH:mm`). `POST /events/checkin` takes the event, the day and the name the player plays under
  (32 characters at most). A start that has begun can't be checked in for.
- **Seating:** the first request for the table after the start (`GET /events/table`) seats everyone checked in for that
  start, once. The people fill the fewest tables of `players` seats (at least 2), shared out evenly in check-in order;
  names are made unique at a table. Each table gets a random seed (the event's boosters and pairings), kept with it.
  Before the start the answer is the number checked in. Someone who wasn't checked in for that start gets no table
  (`missed`).
- **Host:** the first person of a table hosts it. When no lobby code is posted by the start, the next person hosts,
  one more for each 10 minutes that pass (`OnlineEvents.HostAt`). Only the host can post the code
  (`POST /events/tables/{id}/code`).
- **Lobby:** the host's game opens an unlisted lobby (not in the browser) and posts its invite code. The others get the
  table from `GET /events/tables/{id}` and join the lobby under their seat's name. The host's game starts the event when
  everyone of the table is in, or 5 minutes after it opened the lobby. Empty seats get computer players.
- **Results:** when the event ends, each device keeps its own result (record, place, cards drafted) and reports it
  (`POST /events/tables/{id}/result`). The host also reports every seat and every match of the table once
  (`POST /events/tables/{id}/report`). A seat's result is confirmed when the host's report and the person's own report
  agree. The host's seat is confirmed once another person's result at the table is; alone at a table, the host's
  report is the result.
- **Into the career:** the confirmed result is written as a `FinishEvent` that names the table, its seats and whether
  another person played (`Pvp`). The career service seals it only when it is that confirmed result.
- **Prizes:** the online prizes are paid only when another person played at the table. A table of one pays the offline
  share.
- **Leaving without a result** (missed start, or the player leaves) is last place with no record.
- **Against the computer:** the same event can be played against the computer at once for the offline share, unless it
  is PvP only (`pvpOnly`).
- **Standings:** online results count for the rank and this month's points (see *Leaderboards*).
- The online regional championship (design, see *Circuit*) uses two fixed time slots, each with a 30-minute check-in
  before its start, and plays on two days in the player's slot.

### Trust

The game is open source and written in C#, so the client can be read and modified. Two consequences:

- **Player Data Storage alone doesn't stop cheating.** The client writes its own files there with its own sign-in; a
  modified client writes whatever it wants. It is a good *place* for the save (cloud copy, survives reinstalls), not an
  authority.
- **Encrypting or signing the save on the client doesn't either.** Whatever key the client uses ships inside the build
  and can be extracted from the assemblies. It stops casual save editing (worth having), nothing more.

Anything that must be trusted needs a signer the player doesn't control: a key that never leaves a machine the
developer runs. That is the career service (built in C5, below).

#### The career service that seals careers

- **What it is:** a small .NET web service (`server/Arcanum.CareerService`) with a sealing key, on the developer's server
  (see *Career service hosting*). It runs the career rules of `Arcanum.Data` (`CareerChecker`), the same code the game
  runs. The client proves who it is with its EOS Connect ID token, and the service verifies it.
- **The seal** (`CareerSeal`, `Arcanum.Data/Career/CareerSeal.cs`): the player, how many journal actions the service has
  checked (`Count`), a hash that chains those actions (SHA-256 of the previous hash and the next action's JSON), and an
  ECDSA P-256 signature of the player, count and hash (`arcanum-career-seal-v1`). The game carries the public keys
  (`CareerSeals.PublicKeys`, key id `k1`), so it can tell a genuine seal from a made-up one. A career is *checked* when
  its seal covers the whole journal.
- **Profile = seal + journal.** The service keeps the career it checked (journal, the state it gives, the seal). A new
  device or a career that went apart takes that profile (`GET /career/profile`). Player Data Storage holds the game's
  copy, which is not trusted on its own.
- **Everything random is derived, not stored.** Booster contents, store stock, opponents and offers come from
  `hash(profile seed, day, counter)`. The service recomputes them, so a journal can't claim a different booster.
- **Sync** (`POST /career/sync`): the client sends the actions after the seal it has (its count and hash), with the
  content and game versions. The service applies them with the rules and its own clock, and seals every action up to
  the first that doesn't hold. Actions dated more than 5 minutes ahead of its clock are refused, and so is more than
  20,000 actions in one sync. The answer is one of: `Ok` (all checked); `Conflict` (the service has another journal; its
  seal comes back); `Rejected` (the reason, and the seal of the part that held); `Unsupported` (another game version, or
  content the service doesn't have). Syncs of one player run one at a time.
- **Careers the service hasn't seen** are replayed from their first action. A career that starts before
  `Career:ImportBefore` (`appsettings.json`, 2026-12-01) and no longer replays with today's rules and content is taken
  as the game sent it, its state included. A new career (its first action differs from the checked one's) replaces the
  checked one.
- **Rollback/duplication:** a sync must build on the seal the service has. A seal from another device or a restored
  backup gives `Conflict`; the game builds on a newer genuine seal of the same career, and otherwise reports the career
  as differing. An older copy is never accepted again.
- **Online results** are checked against the table's result the service confirmed (see *Scheduled online events*): a
  `FinishEvent` naming a table is sealed only when it is that confirmed result, so the prize is the rules' prize for it.
  The result never comes from the player's word alone.
- **Refused syncs:** the service keeps the reason (`refused` table), not the journal. The game keeps playing its local
  career and offers *go back to the checked career*, which keeps the local files in a backup zip
  (`TakeCheckedCareerAsync`).
- **What can't be verified:** the result of an offline match against a bot (the game ran only on the player's device).
  The answer is bounds instead of proofs: offline rewards are capped by the rules (2 dailies, decaying practice, ante
  only against stakes of the same tier), so the most a cheater gets offline is what a player who wins every game would
  get. Cheating offline = playing perfectly, which is acceptable.
- **Offline-only players** never need the service; their career is simply not checked, and it can't check in for an
  online event until a sync seals all of it.

#### Career service hosting

Decided: a small **.NET service on an Oracle Cloud Always Free VM** (not serverless: the service must run the same C#
career rules library as the game, without a second copy in another language).

| Piece | Choice |
|---|---|
| Machine | Always Free compute VM `VM.Standard.E2.1.Micro` (x86, 1 GB RAM + 2 GB swap; Ampere had no capacity), Ubuntu 24.04; ASP.NET runtime (`aspnetcore-runtime-8.0`) from Ubuntu's packages, service built for `linux-x64`; about 420 MB in use with the career world warm |
| Service | ASP.NET minimal API in its own project (e.g. `server/Arcanum.CareerService`), referencing `Arcanum.Data` for the career rules |
| Endpoints | `POST /career/sync` (actions → checked and sealed, or refused), `GET /career/profile` (the checked career, for a new install); online events: `POST /events/checkin`, `GET /events/table` (the player's table for a start, seated on first ask after it), `GET /events/tables/{id}`, `POST /events/tables/{id}/code` (host's lobby code), `POST /events/tables/{id}/report` (host's report of the table), `POST /events/tables/{id}/result` (each person's own result), `GET /leaderboard/{rank\|month}`; `POST /auth/discord` (OAuth code → Discord tokens, keeps the client secret), `POST /auth/discord/refresh`, `GET /health` (also the service's clock). The season files are static files, `GET /career/season/<module id>.json`, served by Caddy |
| Auth | EOS Connect ID token in each request (`Authorization: Bearer`), RS256, keys from EOS's JWKS (fetched again at most once a minute for an unknown key id); expiry, issuer, product and deployment ids (`Eos` in `appsettings.json`) are checked; the player id is the token's subject, never from the body |
| Data | SQLite `career.db` in the service's state folder (`/var/lib/arcanum-career`): `careers` (per player: the checked career, journal, state and seal, compressed), `refused` (the reason of each refused sync; no journal), and for online events `checkins`, `seated`, `tables` (each table and its host's report) and `results` (each person's own result) |
| Keys | ECDSA P-256 sealing key, `/etc/arcanum-career/seal-key.pem`, readable by root only, made once by `setup.sh` on the machine and never copied off it; systemd gives the service a copy (`LoadCredential`). Its public key (base64, SubjectPublicKeyInfo) is in `CareerSeals.PublicKeys` under the key id `k1`, which each seal names, so a key can be rotated (a retired key stays listed) |
| HTTPS | Reverse proxy with automatic certificates (Caddy, `deploy/Caddyfile`) on a domain or free subdomain (`arcanum-career.duckdns.org`); the season files are served from `/srv/arcanum-career`, everything else goes to the service on `127.0.0.1:5080`; only ports 80/443 open in the VM's security list and firewall |
| Running | systemd unit `arcanum-career.service` with restart (`server/Arcanum.CareerService/deploy`): `setup.sh` once on the machine (packages, user, sealing key, Caddy); `deploy.sh [user@host]` builds and copies the service over SSH, writes the game version it checks (`Career__Game`, from `project.godot`) and restarts it; `publish-content.sh <module dir>` puts a content module version on the machine; `publish-season.sh` checks and puts a season file. Secrets in `/etc/arcanum-career/secrets.env` on the machine only (`Discord__ClientSecret=…`). Later a GitHub Actions workflow does the deploy |
| Content and world | `/var/lib/arcanum-career/content/<module id>/<version>/` holds the module and the game's compact card data (`cards.jsonl.gz`). `CareerWorlds` builds the career world of the newest content at start and every 5 minutes (so the first sync doesn't wait), keeps one world (rebuilt when the season file changes) |
| Backups | not set up yet. Planned: a daily copy of the SQLite file off the VM (losing it means players fall back to their last checked career, not to nothing) |
| Limits | rate limits per address (the address Caddy passes on): 30 requests a minute for careers and events, 10 a minute for sign-ins; at most 20,000 actions in one sync |

Notes:
- Always Free instances that sit idle can be reclaimed by Oracle; check the current idle rules when setting it up
  (upgrading the account to pay-as-you-go keeps it free within the limits and avoids reclaiming).
- The client must keep working when the service is down: it plays offline and syncs later.
- The same machine can later run the dedicated headless host (M14+) for official PvP events.
- Card content: the service needs the content module version the client reports (set boosters, card list) to re-derive
  boosters. `publish-content.sh` puts each version on the machine; a sync for a version it doesn't have gets `Unsupported`.
- Game version: the service checks careers only for the game version it runs (`Career__Game`). Redeploy it after each
  game release, so careers of that release are checked. Syncs from another version get `Unsupported` until then.

Without the service, the options would be:

1. **Casual trust:** compressed save sealed with an HMAC on disk (and later in Player Data Storage): it stops casual editing, online
   results confirmed by both players, leaderboards "best effort".
2. **Fair formats for the big events:** international/championship events are **limited** (sealed/draft): every
   player's pool comes from the event, so the collection doesn't matter and can't be cheated in.

Recommendation: ship C1–C3 with option 1 but already journal-based (so the service can be added without changing the
save format), big events limited (option 2), and the career service in C5 before online prizes exist. Constructed
online games need a host (*Decisions to take*, 2).

Decided for the circuit (design): the circuit's regional qualifiers and regional championships use the real formats,
constructed included, instead of option 2. The risk is accepted for now; see *Circuit*, *Formats*.

## Circuit: qualifying events (C6)

Part implemented: the offline regional qualifier and the offline regional championship. The online championship (time
slots, check-in) and the international tournament are design, not built yet. The qualification chain:

```
regional qualifier → regional championship → international tournament
```

Entry fees and prizes for the qualifier and the championship are set in the module's `career/career.json`.

### Regional qualifier

- Offline only, against computer players.
- Runs the first weekend of every month, Friday to Sunday (a run of three days). The player enters on any day of the
  run and plays its rounds over those days. Entering is once per run.
- Attendance is 16 to 64 players, drawn from the run's seed. Swiss rounds by attendance (`rounds: 0`), best of three,
  then a top 8 single-elimination playoff. Each Swiss round is timed (50 minutes; see *Round time*).
- The winner is invited to the regional championship. The finalist is invited too when 33 or more players took part.
  Invitations are listed in the forum's pinned post.

### Regional championship

- Offline only, against computer players, for now.
- Played at the big tournaments' table (`tournamentMat`): one tournament mat for both players, with the event's name on
  its crest (see *Look*).
- Runs the third weekend of March, June, September and December, Saturday and Sunday (a run of two days).
- By invitation only (`invitational`): only players with an invitation from the qualifier enter, and entering uses it up.
- Attendance is 128 to 256 players. 14 Swiss rounds, best of three. After round 8 on day 1, only players with 18 points
  (6-2) or better play on. Then a top 8 single-elimination playoff. Each Swiss round is timed (50 minutes; see
  *Round time*).
- Prizes are cash, boosters and reputation. No one qualifies for the international tournament yet, since that event
  doesn't exist yet.
- **Online (design):** two fixed time slots (for example one for Europe and Africa, one for the Americas), each with a
  30-minute check-in before its start. Empty seats get computer players, as in *Scheduled online events*. Day 2 is the
  next day in the same slot, for the players who make the cut.
- **Online qualification (design):** offline grants less qualification than online. For example, only the champion
  qualifies offline, against the top finishers online. The online event stays worth playing.

### Round time

- The qualifier and the championship have a 50-minute limit on each Swiss round (`roundMinutes`). The playoff is untimed.
- A match's clock starts when its first game begins. It runs in real time, also between games and while the player is
  away from the event. The event screen shows the time left.
- Once time is up, no new game begins and the match ends with the games played. A game that ends after the deadline is
  the match's last game. A match between games ends when the event screen sees that time is up
  (`LimitedService.EndIfTimeUp`). Then the event goes on.
- If time runs out during a game, the end-of-match procedure starts (the game's `CallTime`). The turn in progress is
  turn 0, then 5 additional turns follow (extra turns count among them). A game not over after them is a draw. The
  board announces "Time!" and each additional turn. The match then ends with the games played, and the event goes on.

### Multi-day runs

- A run starts on its `when` day and lasts `days` days (UTC). The state of the run is kept in the career folder
  (`event.json`), so the player plays rounds on different days.
- When the last day is over and a round wasn't played, that round is lost: the player's match in progress is conceded
  and the player is dropped. The others play the event out. This applies only to events of more than one day.
- Dropping out with the "Drop out" button works the same way: the match in progress is conceded, and the player keeps
  their place in the final standings instead of the last place.

### Standings

- Swiss standings use match points, then opponents' match-win %, game-win % and opponents' game-win %. Each of these is
  at least 33%. A bye is a 2-0 win and isn't counted as an opponent. Byes go to the lowest-placed player without one.
- Final places: by how far each player went in the playoff. Players out in the same round are ordered by their Swiss
  place. Everyone else is ordered by the Swiss standings.
- A playoff match can't end in a draw: games continue until someone wins.

### Formats

- The same formats as the real events, constructed included (Standard, Modern, Pioneer, and so on), and sealed or draft.
  This replaces the earlier plan to limit the big events, for these events only.
- **Accepted risk:** the collection and the offline results can't be validated, because the client reports them. A
  modified client could enter a constructed deck with cards it doesn't own, or claim an offline qualification. The only
  gain is entry to the international tournament, which is played against people. The career service checks (journal
  replay, signed profile) narrow this later.

## Building blocks already there

- Engine, bots, formats and deck legality; Commander pods of 4.
- Booster generation per set, draft/sealed with bots, Swiss rounds (`Arcanum.Data/Limited`, `LimitedService`).
- Online events host/client, lobbies, invite codes, EOS sign-in, P2P relay.
- 3D booster opening.
- Deck store (plain-text lists in `user://decks`).

## New pieces

| Piece | Where | Notes |
|---|---|---|
| `CareerProfile` (cash, collection, sealed product, reputation, rank, day, used dailies, history) | `Arcanum.Data/Career` (pure .NET) | JSON save in `user://career/`, with a checksum to catch damaged files (not a security measure) |
| Collection (card + printing + count) | same | the deck builder gains a *collection* mode: only owned cards, counts limited |
| Economy (prices, store stock, sell/buy, trades) | same | deterministic from a seed + day, testable |
| Calendar / season config | same | parses the module default and the remote file; given a time, lists the open events |
| Opponent roster and rivals | content module (data) + `Arcanum.Bots` (deck building per tier) | names/portraits generic |
| Career hub screen | `scenes/career` | the dashboard: day, cash, dailies, today's event, store, collection, decks, news |
| Store screen with hotspots, table playmat (one image split across both seats) | `scenes/career`, `scripts/UI/Board/Playmat.cs` | art in `ui-art/LGS-1*.jpg` |
| Remote config + cloud save | `scripts/Client/CareerService.cs`, `scripts/Client/Eos` | season download and server time from the career service; Player Data Storage |
| Accounts: Discord sign-in, linking/unlinking, Settings → Account | `scripts/Client/Eos`, career service (`/auth/discord` code exchange) | replaces Device ID login |
| Career rules library (journal replay, derived randomness, limits) | `Arcanum.Data/Career` | the same code runs in the client and in the career service |
| Career service (checks and seals careers, online tables, leaderboards) | `server/Arcanum.CareerService` (`Program.cs`, `CareerStore.cs`, `EventDesk.cs`, `EosIdentity.cs`, `CareerWorlds.cs`); `Arcanum.Data/Career/CareerChecker.cs`, `CareerSeal.cs` | ASP.NET on the Oracle Cloud VM; sealing key only there; EOS ID token check; per-player checked career |
| Scheduled online events | rules in `Arcanum.Data/Career/OnlineEvents.cs`; tables in `server/Arcanum.CareerService/EventDesk.cs`; client in `scripts/Client/CareerService.Online.cs`, the table's lobby in `scripts/Client/OnlineService.CareerTable.cs`, screens in `scripts/UI/Career/EventScreen.Online.cs` and `CareerScreen.Standings.cs` | check-in, seating, host, confirmed results, ranks and monthly points |

## Milestones

| # | Content | Done when |
|---|---|---|
| C1 | **Offline core** (done): profile + save, starter deck choice, real-day calendar, 2 dailies vs bots, store (boosters, precons), opening into the collection, collection deck builder, sell cards, hub screen, store picture with hotspots, store table playmat | a new career can be played for a week of in-game days, entirely offline |
| C2 | **Grind and life** (done): practice with decaying rewards, ante matches, challenges, opponent roster with tiers and rivals, trades with opponents, opportunity offers, reputation tiers | the store feels alive from day to day |
| C3 | **Offline calendar** (done): weekly events from the module's default schedule (draft night, commander night, Saturday tournament, monthly championship) against bots, prizes and qualification tokens, in-game card prices reacting to results | a full in-game month has a shape |
| C4 | **Accounts and remote config** (done): Discord sign-in (replacing Device ID) and account linking screen; Title Storage season file, server time, cached config, cloud save; developer tool to validate a season file before publishing | changing the season file changes next week's events without a release |
| C5 | **Online events** (done, in testing): career service (checks and seals careers, EOS Connect sign-in), check-in for draft events, tables seated by the service, the first checked-in player hosts an unlisted lobby, results confirmed by host and player, online prizes only for confirmed results, bot fill, PvE option at reduced prizes, rank and monthly standings. Not in C5: online constructed and Commander events (later: the service will hand each table the decks entered, which the career journal already holds) | two players meet in a scheduled draft night online |
| C6 | **The circuit** (in progress): offline regional qualifier and offline regional championship done, see *Circuit*. The official table, tournament mat, broadcast overlay and round clock are done. To do: the online regional championship (slots, check-in; needs C5), the international tournament, PvP-only events, season standings and end-of-season rewards; constructed big events once the dedicated server exists | a season can be won |

C1–C3 are fully useful without any online part, so the mode can ship early as an offline career.

## Boosters

The store sells every kind of booster each set was sold in (play or draft, set, collector), each sheet with its exact
printings (their own art), traditional and surge foils at their real rates; limited events open only the play or
draft booster. A card is a foil, or a surge foil, only where that printing was made so. Pending (urgent): the third
set's set booster's reprint-list slot (see the roadmap's backlog).

## Decisions to take

1. ~~Trust model~~ decided: signed profile + journal, verified by the career service on an Oracle Cloud Always Free VM.
2. ~~Who hosts scheduled PvP events~~ decided: the first checked-in player of each table hosts (the next person after 10
   minutes without a lobby). A dedicated server comes later (M14+).
3. ~~Starting kit~~ decided: one preconstructed deck of the player's choice + 3 boosters of the featured set.
4. ~~Banking dailies~~ decided: not banked; 2 per real day, unplayed ones are lost.
5. ~~Flavor location~~ decided: store, regulars, opponent decks per tier, news and the default calendar live in the
   content module; the main repo has only the generic system.
