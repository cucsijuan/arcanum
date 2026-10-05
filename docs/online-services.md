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

The provider today is `LanOnlineServices`. Swapping it is one line (`OnlineService.Services`).

## Internet provider (planned): Epic Online Services

Chosen because it is free, cross-platform (desktop and mobile, which M12 needs) and covers all three pieces without running our
own servers:

| Our interface | EOS interface |
|---|---|
| `SignInAsync` | Connect interface, **Device ID** login (no account, no launcher, no Epic account needed by players); the display name is ours. |
| `ILobbyDirectory` | Lobby interface: create a lobby with attributes (version, content, format, commander, seats, invite code, host name) and `PublicAdvertised`/`InviteOnly` permission; search by attributes; find by code with an attribute search on the code. |
| `IRelayNetwork` | P2P interface: NAT traversal with automatic fallback to Epic's relays. A socket name per lobby; packets carry our existing message framing over a reliable ordered channel. |

What is needed, none of which can go in the repository:

1. An Epic Games developer account and an organization/product in the Developer Portal.
2. The product's **ProductId, SandboxId, DeploymentId, ClientId and ClientSecret** (a client policy allowing Connect Device ID,
   Lobbies and P2P). They are injected at build time (environment variables read by the export step into a generated file that
   is git-ignored), never committed.
3. The **EOS C SDK** for each platform (downloaded from the portal after accepting its license) and a C# binding. The SDK ships its
   own C# wrapper; it would be referenced from the client only, so the engine and net libraries stay provider-free.
4. Decisions: the product name shown in the portal, whether lobbies are listed publicly by default or only by code, and the
   region/deployment (one live deployment and one for development).

Until then everything works on the local network, and the provider can be developed against the in-memory implementation and
the `Arcanum.Net.Tests` service tests (which every provider should pass).

## Not done yet

- The EOS provider itself (needs the items above).
- Accounts beyond a device identity (friends lists, presence, invites through a platform overlay).
- Matchmaking (a queue that fills a lobby automatically) — the directory search is the basis for it.
