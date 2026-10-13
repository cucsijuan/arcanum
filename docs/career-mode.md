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
| `when` | weekly on Wednesday 18:00–24:00 UTC; monthly last Sunday; or a date range |
| `kind` | constructed (format id), commander pod, draft, sealed, prerelease |
| `set` | set code for limited events / featured set |
| `entry` | cash, boosters, or a qualification token |
| `prizes` | by place or by record (3-0, 2-1…): cash, boosters, promo, qualification token, rank points, reputation |
| `minTier` | reputation tier required |
| `modes` | `pve`, `pvp`, or both; `pveRewardScale` (e.g. 0.5) |
| `rounds`, `bestOf`, `podSize` | Swiss rounds, Bo1/Bo3, Commander pod of 4 |

Default week (illustrative):

| Day | Event | Entry | Notes |
|---|---|---|---|
| Every day | 2 daily matches | free | |
| Wednesday | Draft night | $15 or 3 boosters | featured set |
| Friday | Commander night | $5 | 4-player pods, social reputation |
| Saturday | Weekly tournament | $20 | constructed, Swiss, rank points |
| Last Sunday | Monthly championship | $50 or qualification | top players get a qualification token |
| Seasonal | International tournament | qualification token | PvP only (config) |

Offline, events are always PvE. An event with `online` (start times UTC, check-in minutes, `pvpOnly`, `offlineScale`)
is also played online at those times (empty seats get computer players); unless it's PvP only, it can be played
against the computer any time of its day for `offlineScale` of the prizes. Until online events exist (C5) the game
plays them offline at that share, and PvP-only ones show as online only.

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
  monitor growing from where it stands): daily matches, events, selling cards, unopened boosters and news, and signing
  up for a new career. The boxes of cards on the shelf open the decks; the stack on the desk opens "my cards".
- **The store:** `store.jpg`. The booster rack, the preconstructed decks and the counter (selling cards) open paper
  notes over the dimmed picture. Esc goes back home.
- **Hotspots** glow along their outline under the pointer, show their name, and follow the arrow keys and Enter.
- **The table:** `table.jpg` spans both seats in daily matches (top half behind the opponent, bottom half behind the player).
- **Deck building:** the desk with the computer seen from above (`desk.jpg`, its playmat's area in `desk.json`): the
  deck's controls on the wood above the mat, the cards owned on the left of the mat, the deck on the right in columns
  by mana value, basic lands (free) and the deck's state on the wood below. The owned cards are grouped by canonical
  name (a two-faced card saved under either name counts once). Playmats will be changeable: a picture
  in `mats/` is drawn over the mat's area (`mats/default.jpg` for now).
- **Commander suggestions:** on a Commander deck with its commander chosen, and a module with the commander statistics,
  a *Suggestions* button sits in the top row. It swaps the filters for a group, sort, variant (all decks, budget,
  expensive, a theme), *Only mine* and *Use average deck*. The mat shows every suggested card (basic lands left out),
  each with its inclusion and synergy. Cards not owned are dimmed, marked *Not yours* and can't be added. The note below
  the mat gives the decks behind the numbers and the average deck's card types beside the deck's. *Use average deck*
  replaces the main deck with the average deck using only owned copies (basic lands free) and lists the cards left out.
  Testing: `ARCANUM_SUGGEST=1` with `ARCANUM_OPEN=career` and `ARCANUM_CAREER_PAGE=decks` opens it for the first career deck.
- The art must stay generic: no recognizable toy, game or brand look-alikes (the store's wall is plain where a toy
  look-alike was).

## Online

### Remote config (EOS)

- **Season file** (served by the career service, not EOS Title Storage: the service needs the same file to verify
  careers, so it lives in one place): the module's `career/season.json` lists changes, each from a day on (to a last
  day, optionally): events added, replaced (same id) or cancelled; the featured set; booster prices; a line of news.
  The season editor (started with `tools/season-editor.sh [module dir] [user@host]`, a page on this computer) plans it on a
  calendar (store events, the season's, cancelled ones, featured set, prices and news per day; played days locked),
  with forms for changes and events. It starts from the published season, keeps a draft on this computer
  (`~/.local/share/arcanum-season-editor/<module id>.json`, dropped once published), downloads it as a file and
  publishes it; it never writes the module (its `season.json` is replaced by hand when wanted).
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
- **Leaderboards / Stats:** rank per format, event wins, season standings.
- **Server time:** dailies and event windows use the service's time when online (learned from the season download's
  answer); offline uses the local clock but never goes backwards from the last time seen.

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

- During the event window players **check in**; at fixed start times (e.g. every 30 minutes) the checked-in players are
  paired into pods/Swiss events. Missing seats are filled with bots; matches against bots pay the PvE scale.
- PvE choice: the player can enter the same event against bots only, any time in the window, for the reduced prizes.
- PvP-only events (configured) don't offer that.

### Trust — the main open decision

The game is open source and written in C#, so the client can be read and modified. Two consequences:

- **Player Data Storage alone doesn't stop cheating.** The client writes its own files there with its own sign-in; a
  modified client writes whatever it wants. It is a good *place* for the save (cloud copy, survives reinstalls), not an
  authority.
- **Encrypting or signing the save on the client doesn't either.** Whatever key the client uses ships inside the build
  and can be extracted from the assemblies. It stops casual save editing (worth having), nothing more.

Anything that must be trusted needs a signer the player doesn't control: a key that never leaves a machine the
developer runs.

#### Design: a small career service that signs the profile

- **What it is:** a small .NET web service with a private signing key, on the developer's server (see *Career service
  hosting*).
  The client proves who it is with its EOS Connect ID token (the service verifies it against EOS's public keys).
- **Profile = signed snapshot + journal.** The profile stored in Player Data Storage (and encrypted on disk for offline
  play) is the last snapshot signed by the service plus a journal of what happened since: day started, daily played
  (opponent, result), booster bought, booster opened, card sold, ante won/lost, event entered/finished. The public key
  ships in the game, so the client itself can tell a signed snapshot from a tampered one.
- **Everything random is derived, not stored.** Booster contents, store stock, opponents and offers come from
  `hash(profile seed, day, counter)`. The service recomputes them, so a journal can't claim a different booster.
- **Sync when back online:** the client uploads snapshot + journal; the service replays the journal on the snapshot
  with the same rules code (a shared .NET library) and checks the limits against *its* clock: no more than 2 dailies
  per real day elapsed, prices and rewards as the rules say, fees paid, opened boosters match their seed. Accepted →
  it returns a new signed snapshot (journal folds into it). Rejected → the profile goes back to the last signed
  snapshot; the bad journal is kept for review.
- **Rollback/duplication:** the service keeps, per player, the hash and counter of the last snapshot it signed; an
  older snapshot (restored backup, a second device) is refused or merged, never accepted twice.
- **What can't be verified:** the result of an offline match against a bot (the game ran only on the player's device).
  The answer is bounds instead of proofs: offline rewards are capped by the rules (2 dailies, decaying practice, ante
  only against stakes of the same tier), so the most a cheater gets offline is what a player who wins every game would
  get. Cheating offline = playing perfectly, which is acceptable.
- **Online prizes** (PvP and PvE online events) are granted by the service directly from the event's result: confirmed
  by both players now, by the dedicated host later. They never go through the client's journal.
- **Offline-only players** never need the service; their profile is simply "unverified" and can't enter online events
  until a sync accepts it.

#### Career service hosting

Decided: a small **.NET service on an Oracle Cloud Always Free VM** (not serverless: the service must run the same C#
career rules library as the game, without a second copy in another language).

| Piece | Choice |
|---|---|
| Machine | Always Free compute VM `VM.Standard.E2.1.Micro` (x86, 1 GB RAM + 2 GB swap; Ampere had no capacity), Ubuntu 24.04; ASP.NET runtime from Ubuntu's packages, service built for `linux-x64` |
| Service | ASP.NET minimal API in its own project (e.g. `server/Arcanum.CareerService`), referencing `Arcanum.Data` for the career rules |
| Endpoints | `POST /career/sync` (snapshot + journal → new signed snapshot or rejection), `GET /career/profile` (last signed snapshot, for a new install), `POST /career/prize` (online event results), `POST /auth/discord` (OAuth code → Discord tokens, keeps the client secret), `POST /auth/discord/refresh`, `GET /health` (also the service's clock) |
| Auth | EOS Connect ID token in each request, verified against EOS's public keys; the player id comes from the token, never from the body |
| Data | SQLite: per player the last snapshot hash, counter and signed snapshot; rejected journals for review |
| Keys | Ed25519 signing key in a file readable only by the service user, outside the repo; public key compiled into the game; a key id in each signature so the key can be rotated |
| HTTPS | Reverse proxy with automatic certificates (Caddy) on a domain or free subdomain; only ports 80/443 open in the VM's security list and firewall |
| Running | systemd unit with restart (`server/Arcanum.CareerService/deploy`: `setup.sh` once on the machine, `deploy.sh` builds and copies over SSH); secrets in `/etc/arcanum-career/secrets.env` on the machine only (`Discord__ClientSecret=…`). Later a GitHub Actions workflow does the deploy |
| Backups | daily copy of the SQLite file off the VM (losing it means players fall back to their last signed snapshot, not to nothing) |
| Limits | rate limit per player and per address; journal size cap |

Notes:
- Always Free instances that sit idle can be reclaimed by Oracle; check the current idle rules when setting it up
  (upgrading the account to pay-as-you-go keeps it free within the limits and avoids reclaiming).
- The client must keep working when the service is down: it plays offline and syncs later.
- The same machine can later run the dedicated headless host (M14+) for official PvP events.
- Card content: the service needs the content module (set boosters, card list) to re-derive boosters; it downloads the
  same module version the client reports and refuses unknown versions.

Without the service (until it exists), options are:

1. **Casual trust:** compressed save sealed with an HMAC on disk (and later in Player Data Storage): it stops casual editing, online
   results confirmed by both players, leaderboards "best effort".
2. **Fair formats for the big events:** international/championship events are **limited** (sealed/draft): every
   player's pool comes from the event, so the collection doesn't matter and can't be cheated in.

Recommendation: ship C1–C3 with option 1 but already journal-based (so the service can be added without changing the
save format), big events limited (option 2), and the career service in C5 before online prizes exist. Constructed big
events need the dedicated host for the games themselves.

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
| Career service (sign/verify profiles, online prizes) | `server/Arcanum.CareerService` | ASP.NET on the Oracle Cloud VM; private key only there; EOS ID token check; per-player last-snapshot hash |
| Scheduled online events | `Arcanum.Net` + client | check-in, pairing into existing event/lobby hosts, bot fill, result confirmation |

## Milestones

| # | Content | Done when |
|---|---|---|
| C1 | **Offline core** (done, in testing): profile + save, starter deck choice, real-day calendar, 2 dailies vs bots, store (boosters, precons), opening into the collection, collection deck builder, sell cards, hub screen, store picture with hotspots, store table playmat | a new career can be played for a week of in-game days, entirely offline |
| C2 | **Grind and life** (done, in testing): practice with decaying rewards, ante matches, challenges, opponent roster with tiers and rivals, trades with opponents, opportunity offers, reputation tiers | the store feels alive from day to day |
| C3 | **Offline calendar** (done, in testing): weekly events from the module's default schedule (draft night, commander night, Saturday tournament, monthly championship) against bots, prizes and qualification tokens, in-game card prices reacting to results | a full in-game month has a shape |
| C4 | **Accounts and remote config:** Discord sign-in (replacing Device ID) and account linking screen; Title Storage season file, server time, cached config, cloud save; developer tool to validate a season file before publishing | changing the season file changes next week's events without a release |
| C5 | **Online events:** career service (profile sync and signing, online prizes), check-in windows, pairing, bot fill, PvE option at reduced prizes, result confirmation, leaderboards and rank | two players meet in a scheduled draft night online |
| C6 | **The circuit:** qualification chain (monthly → international), PvP-only events, season standings and end-of-season rewards; constructed big events once the dedicated server exists | a season can be won |

C1–C3 are fully useful without any online part, so the mode can ship early as an offline career.

## Boosters

The store sells every kind of booster each set was sold in (play or draft, set, collector), each sheet with its exact
printings (their own art), traditional and surge foils at their real rates; limited events open only the play or
draft booster. A card is a foil, or a surge foil, only where that printing was made so. Pending (urgent): the third
set's set booster's reprint-list slot (see the roadmap's backlog).

## Decisions to take

1. ~~Trust model~~ decided: signed profile + journal, verified by the career service on an Oracle Cloud Always Free VM.
2. Who hosts scheduled PvP events before a dedicated server exists: the first checked-in player, or the developer runs a
   headless host for them.
3. ~~Starting kit~~ decided: one preconstructed deck of the player's choice + 3 boosters of the featured set.
4. ~~Banking dailies~~ decided: not banked; 2 per real day, unplayed ones are lost.
5. ~~Flavor location~~ decided: store, regulars, opponent decks per tier, news and the default calendar live in the
   content module; the main repo has only the generic system.
