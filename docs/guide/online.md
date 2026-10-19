# Online

You can play with other people in two ways:

- On your local network, with no account needed. Other players find your lobby in the list, or join with its invite
  code or address.
- Over the internet. This needs a Discord sign-in. Your online name and identity then follow your Discord account to any
  computer you sign in on.

Every player needs the same game version and the same content module.

## Sign in for the internet

The first time you go online over the internet, the game asks:

- **Sign in with Discord** opens your browser to Discord. Allow the game there and it returns to Arcanum.
- **Local network only** skips the sign-in. You can still host and join on your own network.

You can sign in or out later in **Settings**, under **Gameplay**, in the **Online** section, in the **Discord account**
row. The game only learns your Discord user id and name.

If the Discord app is open on this computer, your Discord profile can also show what you are doing in Arcanum, such as
the game's format or event, its turn and your life total, or the career screen you are on. This needs no sign-in. Turn it off with **Show activity on Discord**, in the same
**Online** section of the settings.

## The online screen

From the main menu, choose **Online**. It has two choices:

- **Host a game** opens a lobby that others join.
- **Join a game** joins a lobby someone else opened.

## Host a game

On the host page, choose what to play:

- **Play**: **Game (bring a deck)**, **Draft** or **Sealed**.
- **Format**: for a game, the format of your deck. For a draft or sealed event, the **Boosters** source and **Matches**
  (best of one or three).
- **Players**: the number of seats.
- **Port**: the port the game listens on. The default is 47013. Change it only if it is in use.

Click **Host**. The lobby then opens with:

- The **invite code**, in the form `ABC-DEF`, with a **Copy** button. Give it to the players who should join.
- The addresses to give to players on other networks, each with a **Copy** button.
- The seats. Each seat shows a player or a free seat. Free seats can be given to the computer: use **Computer**
  (or **Change deck** for a computer seat that already has a deck).
- **Start game**, once the seats are ready. If something is missing, the lobby says what.
- A fill option gives the computer every seat nobody took.
- **Leave** to close the lobby.

The lobby is listed in the **Open lobbies** list of the network or service it was opened on.

For a game on another network, the host's router must let the players in. The game tries this on its own (UPnP). If it
fails, forward the port yourself. A firewall on the host's computer must also allow the port.

## Join a game

Choose **Join a game**, then one of three ways in:

- **Invite code**: type the code the host gave you and click **Join with code**.
- **Address**: type the host's address and click **Join**. Use the host's local address on the same network.
- **Open lobbies**: click **Refresh** to list the lobbies that are open. Pick one and click **Join**. The list shows
  only lobbies with the same game version and content module and with free seats.

Choose the deck you bring. A deck is only listed if it is legal in the lobby's format. In a draft or sealed event, you
build your deck from the boosters you open in the event.

## Events

Hosting a draft or a sealed event works the same way. Players take their seats in the lobby, then draft or open their
boosters. The event itself is the same as in [Limited](limited.md).

Career events are not played online yet. The career screen says so. See [Career](career.md).

## Time limits

In **Settings**, under **Gameplay**, in the **Online** section, the **Time per decision** setting sets how long each
player has for a decision in a game you host: no limit, 30 seconds, 1 minute, 2 minutes or 5 minutes. When the time is
up, the computer makes the decision for that player. The player can keep playing afterwards. Passing with your own
stops never counts against the time.

## If someone disconnects

A player who loses the connection is marked **disconnected**, with a countdown. When the countdown ends, the computer
plays their seat. If you host the game, a button lets the computer take that seat at once.

If you were in an online game that has not finished, the game asks **Get back in** when it opens. Choosing it returns
you to your seat.

If your computer stopped hosting, **Resume interrupted game** (or **Resume interrupted event**) hosts the same game or
event again, and the players get back in with their seats.

## Leave a game

**Leave** in the lobby closes it. In a game, the menu's **Main menu** item returns you to the main menu.
