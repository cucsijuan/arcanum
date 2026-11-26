# Online services

How players find each other and connect: lobby listings, invite codes and the network that carries a game between players.
The game itself (lobby, seats, decks, the running game, reconnecting) is the existing protocol in `Arcanum.Net`; online services
only replace *how a connection to the host is made* and *how a host is found*.

## Pieces

All in `src/Arcanum.Net/Services`:

| Type | What it does |
|---|---|
| `IOnlineServices` | A provider: its `Name`, whether it `IsAvailable`, `SignInAsync`, a lobby directory and a network. |
| `ILobbyDirectory` | Publish, update and remove a hosted lobby; search open lobbies; find one by invite code. |
| `IRelayNetwork` | `Listen(lobbyId)` gives the host an `IConnectionListener`; `ConnectAsync(listing)` gives a player an `IConnection` to that host. |
| `LobbyListing` | What the browser shows: host name, format, Commander or not, seats and free seats, version and content id, invite code, the event (draft/sealed) if any. Address/port are only for providers that connect directly. |
| `InviteCode` | Six characters from an alphabet without look-alikes (no `I`, `L`, `O`, `0`, `1`), shown as `ABC-DEF`; parsing ignores case, spaces and dashes. |
| `InMemoryOnlineServices` | Everything in one process (tests, and a reference for new providers). |
| `LanOnlineServices` | The local network, no accounts: hosts announce lobbies by UDP broadcast on port 47014 every second, the browser listens for 2.5 s, players connect straight to the host over TCP on a port the system picks. |

Because the host's game already accepts any number of `IConnectionListener`s, a lobby can be reachable by address (the existing
TCP listener and port mapping) and through the services at the same time.

## Client flow

`scripts/Client/OnlineService.Directory.cs`:

- **Hosting** (a game or a draft/sealed event): after the lobby starts it gets a lobby id and an invite code, listens on the
  service's network and is published. Seat changes update the listing; once the game or event starts the listing shows no free
  seats; leaving removes it. The lobby screen shows the invite code with a Copy button, above the addresses.
- **Browsing**: the start screen has an *Open lobbies* card with Refresh. Only lobbies with the same version and card content
  and free seats are listed; Join uses the chosen deck.
- **Invite code**: the Join card has an *Invite code* field. The code is looked up in the directory and joined like a browser entry.
- **Reconnecting**: a player who joined through the services reconnects through them (`ConnectToHostAsync`); one who joined by
  address reconnects to the address as before.

The providers are built in `MakeServices()` (`OnlineService.Services` is what the screens use), in this order: the local
network (`LanOnlineServices`), the internet lobby service when the build has it (Epic Online Services, below), and the
career service's relay (`ServiceOnlineServices`, see [Internet lobbies and relay](#internet-lobbies-and-relay-career-service)).
They are combined (`CombinedOnlineServices`): a hosted lobby is published on every available one, and the browser shows a
lobby found on several once, from the first in that order. Players reach a host through the first service that has the
lobby, so a game between PCs goes through the internet lobby service and the relay carries only games with a console in them.

## Internet provider: Epic Online Services

`scripts/Client/Eos/EosOnlineServices.cs`, used together with the local network (`CombinedOnlineServices`: a hosted lobby
is listed on each of the services in use, the browser shows each lobby once, preferring the local network, then this
service, and players join through the service the lobby was found on; see Client flow above).

| Our interface | EOS |
|---|---|
| `SignInAsync` | Connect interface, **Discord** login (`DiscordAccessToken`): the player signs in with Discord first (`Account`). A **Device ID** login (a device account) is used only behind the testing switch below. |
| `ILobbyDirectory` | Lobby interface: the host owns a lobby in bucket `arcanum:<version>` with public attributes (our lobby id, host name, format, commander, seats, free seats, version, content, invite code, listed, event). The browser searches by bucket, content, listed and free seats; invite codes by the code attribute. |
| `IRelayNetwork` | P2P interface, socket `ArcanumGame`, reliable ordered packets (NAT traversal, relays when needed). `PacketRelay` (in `Arcanum.Net`, tested without the SDK) carries our messages over them: several connections per peer, messages split into 1170-byte packets, open/accept/close. |

Every SDK call runs on one thread owned by the provider; packets are handed over through queues.

### Building with it

The SDK is not in this repository (its license doesn't allow it; `LICENSE-EXCEPTION` allows linking with it):

1. Put the EOS C# SDK at `../eos-sdk/SDK` next to the repository (its `Source` folder and, in `Bin`,
   `libEOSSDK-Linux-Shipping.so` and `EOSSDK-Win64-Shipping.dll`), or point the `EosSdkDir` property or environment
   variable to it. `src/Arcanum.EosSdk` compiles it and copies the platform's native library next to the assemblies
   (also in exports); `Arcanum.csproj` references it and defines `ARCANUM_EOS` only when the SDK is there.
2. Write the product's keys: `python3 tools/eos_keys.py <keys.json>` (or with `EOS_PRODUCT_ID`, `EOS_SANDBOX_ID`,
   `EOS_DEPLOYMENT_ID`, `EOS_CLIENT_ID`, `EOS_CLIENT_SECRET` set) writes `scripts/Client/Eos/EosKeys.g.cs`, which is
   ignored by git. Without it the build has only the local network. The optional `encryptionKey` /
   `EOS_ENCRYPTION_KEY` (64 hex characters) encrypts the files in the online services' player storage, which the career
   service's cloud copies are moved from once (see `career-mode.md`); it must never change once players have saved with
   it. Without it nothing is moved. Cloud files themselves are kept by the career service, as sent.

The release workflow does both from repository secrets: `EOS_SDK_TOKEN` (a token that can read the private
`cucsijuan/eos-sdk` repository, which holds the SDK) and the `EOS_*` keys above. Without them it still releases,
with local-network lobbies only.

Testing on one computer: `ARCANUM_ONLINE_INTERNET_ONLY=1` leaves the local network and the relay out, `ARCANUM_ONLINE_RELAY_ONLY=1`
uses only the career service's relay, `ARCANUM_EOS_NEW_DEVICE=1` makes a second instance sign in as a new device account
instead of with Discord (`ARCANUM_EOS_DEVICE=1` signs in with this computer's device account without making a new one),
and `ARCANUM_EOS_LOG=1` shows the SDK's log. `ARCANUM_RELAY_PLAYER=<name>` names the player to the relay instead of
sending a sign-in token; the service must run with `Lobbies:TrustPlayerHeader=true` (it logs a warning when that is on;
never in production).

## Internet lobbies and relay (career service)

A console build has no internet lobby library, so it can't use the provider above. The career service gives such
players internet lobbies and carries their games (`server/Arcanum.CareerService/LobbyRelay.cs`, wired in `Program.cs`).
PCs keep their own lobby service.

Every route needs the player's EOS Connect ID token, as the career routes do (without one: 401). The WebSocket routes
answer 400 when the request isn't a WebSocket request.

| Route | What it does |
|---|---|
| `GET /lobbies?version=&content=&open=` | The listed lobbies of that version and content, by host name, at most 100. `open` (default true) leaves out lobbies with no free seats. |
| `GET /lobbies/code/{code}` | The lobby with that invite code (404 when there is none). |
| `/play/host` (WebSocket) | The host's socket. The lobby lives while it is open. |
| `/play/join?lobby=id` (WebSocket) | A player's socket. 404 when the lobby is gone; 429 when the lobby or the service is full. |

Host socket:

- The first message must be a text message `{"type":"publish","lobby":{…}}` within 10 seconds. The lobby's fields are
  those of a listing (lobby id, host name, format, commander, seats, open seats, version, content, invite code, listed,
  event) and its `port`. Later `publish` messages with the same lobby id update the listing. A lobby that isn't valid,
  or no lobby in time, closes the socket.
- The service sends `{"type":"open","peer":n}` when a player joins and `{"type":"closed","peer":n}` when one leaves. The
  host may send `{"type":"close","peer":n}` to remove a player.
- Binary messages are packets, each starting with a player's peer number (4 bytes, big-endian). The host writes the
  number of the player it sends to; the service adds the number of the player a packet came from.

Player socket: binary messages are packets to and from the host, without the peer number. Text messages are ignored.

The service stamps the host's public address on the listing (the first address in `X-Forwarded-For`, else the connection's
address). The listing's `port` is where the host takes direct connections, tried before the relay; 0 means relay only.

Limits (`LobbyRelayOptions`, from the `Lobbies` section of the configuration; the defaults are used when it is missing):

| Limit | Default |
|---|---|
| Lobbies on the service | 200 |
| Lobbies per player | 2 |
| Players per lobby | 8 |
| Players on the service | 1000 |
| Largest packet | 16 KiB (a larger message closes the socket) |
| Relayed bytes per lobby | 256 KiB a second, with a burst of 1 MiB (`ByteBudget`: a packet waits until the rate has room for it) |
| Time to publish | 10 seconds |

The lobby list and invite-code routes share the `lobbies` rate limit: 60 requests a minute per address. The WebSocket
keep-alive is 30 seconds.

Nothing is stored: lobbies live in memory and are gone when the service restarts. When the host's socket closes, its
lobby is removed and its players are closed with "the host left". A player's socket closing sends `closed` to the host.

Game side: `ServiceOnlineServices` (`src/Arcanum.Net/Services`) is the game's `IOnlineServices` for this relay, named
"Internet relay". Its constructor takes the service address and a function that gives the identity headers (in the game,
`Authorization: Bearer` with the EOS ID token). Publishing a lobby opens the host's WebSocket and sends `publish` again on
each update, reopening the socket if it was lost; removing the lobby closes it. Search and invite codes use the HTTP
routes. A `closed` message or a lost service connection drops the relayed players. Joining tries a direct TCP connection
to the listing's address and port for 3 seconds when the port is above 0, then the relay. The player's connection closes
its WebSocket too. Messages already received are read before a lost connection is reported.

The WebSocket is `WebSocketClient` (`src/Arcanum.Net/Transport/WebSocketClient.cs`). It accepts `ws` and `wss`, sends extra
headers on the upgrade, answers pings and closes after 75 seconds of silence. It takes server messages up to 4 MiB. A
refused upgrade reports its HTTP status (401, 404 or 429).

Client wiring (`scripts/Client/OnlineService.Directory.cs`): the relay is the internet service on a console, which has no
internet lobby library. Its identity is the EOS ID token: on a console, from the web sign-in with Discord; on PC, from the
internet lobby service once it is signed in. Signing in signs in to the internet lobby service first, then to the relay.
A console never accepts connections, so its local network service only finds and joins lobbies (no announce, no
listener), and a console's hosted lobby is reached through the relay. The listing's `port` is set only when the router
mapped it, else 0. `ARCANUM_ONLINE_RELAY_ONLY=1` and `ARCANUM_ONLINE_INTERNET_ONLY=1` are in the testing list above. On a device that takes no incoming connections (`OnlineService.AcceptsConnections` false, the console), the lobby screen lists no addresses and the host is not asked to forward a port: `ListAddresses` and `OpenRouterPort` in `scripts/Client/OnlineService.cs` return early, and `scripts/UI/Menu/OnlineScreen.cs` shows the address list only when there are addresses. Players join such a host through the lobby browser or the invite code.

A measured 14-turn two-player game through the relay moved about 1.5 MB each way on the service's machine (2026-11-26).

## Discord activity

Not an online service: it shows on the player's Discord profile what they are doing ("rich presence"), and needs no sign-in.

- `src/Arcanum.Net/Services/DiscordPresence.cs` talks to the Discord app running on the same computer through its local
  socket: a Unix socket `discord-ipc-0` to `discord-ipc-9` in `XDG_RUNTIME_DIR`, `TMPDIR`, `TMP`, `TEMP` or `/tmp`, also
  inside the Flatpak (`app/com.discordapp.Discord`) and Snap (`snap.discord`) folders; a named pipe on Windows. Frames are an
  opcode, a little-endian int32 length and JSON: a handshake, then `SET_ACTIVITY`. The work runs on a background task; when
  Discord is not running it retries every 20 seconds, and an activity already shown is not sent again.
- `scripts/Client/Presence.cs` decides what is shown. Each screen sets it as it opens (`ShowScreen`, from the scene change in
  `App`): the menus, choosing a game, building a deck, a draft or sealed event, the online lobby, and the career screens.
  The career screens set their second line with `Presence.ShowCareer`: `CareerScreen` says "In their room", "Looking at the
  PC" (website open) or "At the LGS" (store), and `DeckDesk` says "Building a deck" or "Browsing cards". The event screen
  shows the event's name. The game board sets its own activity (`GameBoard.ShowPresence`): the first line is the game's
  label: a match name ("Commander match", "Draft match"), or in an event the event's name or the match name followed by
  the round ("<event> · Round 3"; a playoff round says "Quarterfinals", "Semifinals" or "Final"). A career commander night
  says "<event> · game 2 of 4". The second line is `Mulligan phase` before turn 1
  or `Turn N · Life N` (the local player's life), and the time since the game started. No party is sent, so Discord shows
  no player count. The picture is the repository's `icon.png`, by URL.
- The setting is `SettingsData.DiscordActivity` (default on), shown as **Show activity on Discord** in Settings, Online.
  Turning it off clears the activity at once.

## Not done yet

- Accounts beyond the Discord sign-in (friends lists, invites through a platform overlay).
- Matchmaking (a queue that fills a lobby automatically): the directory search is the basis for it.
- A host's lobby stays listed until the service notices the host left when the game crashes (the service times it out).
