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

## Internet provider: Epic Online Services

`scripts/Client/Eos/EosOnlineServices.cs`, used together with the local network (`CombinedOnlineServices`: a hosted lobby
is listed on both, the browser shows each lobby once, preferring the local network, and players join through the service
the lobby was found on).

| Our interface | EOS |
|---|---|
| `SignInAsync` | Connect interface, **Device ID** login: no Epic account; a device account is created on first use. |
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
   ignored by git. Without it the build has only the local network.

The release workflow does both from repository secrets: `EOS_SDK_TOKEN` (a token that can read the private
`cucsijuan/eos-sdk` repository, which holds the SDK) and the five `EOS_*` keys above. Without them it still releases,
with local-network lobbies only.

Testing on one computer: `ARCANUM_ONLINE_INTERNET_ONLY=1` leaves the local network out, `ARCANUM_EOS_NEW_DEVICE=1`
makes a second instance sign in as a new device account, and `ARCANUM_EOS_LOG=1` shows the SDK's log.

## Not done yet

- Accounts beyond a device identity (friends lists, presence, invites through a platform overlay).
- Matchmaking (a queue that fills a lobby automatically): the directory search is the basis for it.
- A host's lobby stays listed until the service notices the host left when the game crashes (the service times it out).
