# Playing

This page explains the game board and the controls. For setting up a game, see [Getting started](getting-started.md).

## Start a game

1. From the main menu, choose **Play**.
2. Choose a mode at the top: each constructed format of the content module, or **Draft** and **Sealed**.
3. Choose **Vs computer** (the computer plays the other seats) or **Hotseat** (everyone plays on this device).
4. Choose the number of players (2, 3 or 4) and check the name and deck of each seat.
5. Click **Start game**.

A deck is only listed when it is legal in the chosen format. If none is, the screen says so and points you to
[Decks](decks.md).

## The board

- **Your half is at the bottom.** Your cards, your hand and your piles are there.
- **The opponents' halves are at the top,** turned round as across a table. With three or four players, the top half
  is shared by the opponents.
- **The divider** holds the phase bar and the turn marker. It shows whose turn it is.
- **Piles** show your library, graveyard and exile. Click a graveyard or exile pile to see every card in it. Press
  Esc to close it.
- **The stack** shows the spells and abilities waiting to resolve. Hover a card in it to see it larger.
- **Hover a card** to see a large preview. For a double-faced card, press F to see its other face.
- **The status panel** next to the preview says what is different about a card from its printed text: changed types or
  colors, counters, damage and changed power or toughness.

## The phase bar

The phase bar shows the steps of the turn, with the current one lit. Each step has two small markers:

- The upper marker sets a **stop** during the opponents' turns.
- The lower marker sets a **stop** during your own turn.

A stop gives you priority after that step's action. The defaults are set in [Settings](settings.md) under
**Default stops**, and you can change them during a game on the phase bar.

The **FULL** button turns on full control. With full control the game stops at every point where you get priority,
even when you have nothing to do.

## Automatic passing

If you have nothing to do and no stop applies, the game passes for you. Combat damage is dealt as soon as its step
begins, so put tricks before that step, at declare blockers. When your creatures are in combat, the game also stops at
declare blockers if you can play something.

## Playing cards and using abilities

- **Click** a card in your hand to play it. A land is played this way too.
- **Drag** a card out of your hand onto the table to play it. Drop it back on your hand to cancel.
- **Click** a card on the table, or a card in a pile or the stack, to use its abilities or play it from there, when the
  game offers that.

When there is more than one thing to do with a card, the action panel lists each one as a button. The panel is on the
right, in your half. It shows:

- The buttons for every legal action (play a card, cast it from the graveyard or exile, activate an ability).
- **Next**, or **Resolve** with the name of the spell on top of the stack. This is the main button. Space presses it.
- **End turn** on your turn. The rest of the turn is skipped unless an opponent acts.
- **Resolve all this turn** when something is on the stack. Then the game stops no more to ask about responses until the
  turn ends.

The panel also shows the top spell of the stack, its rules text and its targets, so you can decide whether to respond.

## Paying costs

By default the game pays mana for you. It picks the lands itself.

To choose the lands yourself, turn on **Confirm mana payment** in [Settings](settings.md). Then, before a spell is
cast, the panel asks you to tap lands. Click the lands you want, then:

- **Auto** taps the lands the game suggests.
- **Cancel** stops the payment and the spell is not cast.
- **Confirm** pays. It stays off until the full cost is paid.

## Targets and choices

When a card needs a target or a choice, the prompt says what to choose. Click the card or player. The buttons under the
prompt can include **Done**, **Back**, **Cancel** and, for an optional target, **None**. Some choices ask for a number
with **+** and **−**, or for yes or no.

## Combat

- **Attacking.** At declare attackers the prompt says **choose attackers**. Click your creatures that attack. In a game
  with more than two players, choose which opponent each attacker goes to, with the buttons shown.
- **Blocking.** At declare blockers, click one of your creatures to choose it as a blocker, then click the attacker it
  blocks. A creature that can block several attackers is clicked again when it has all of them.
- **Identical creatures.** When several identical creatures attack or block together, a small picker asks how many.
  Move the slider with Left and Right, Enter confirms, Esc cancels.
- **Damage.** When an attacker is blocked by several creatures, you divide its damage with the controls shown.
- **Arrows** show each block, from the blocker to the attacker.

## Announcements

When another player does something, a banner shows the card involved, with a sentence and arrows to whatever it
affects. The game waits while announcements are queued, so you can read them. Your turn starts with a banner and a
short chime.

## Mulligan

At the start of the game you see your opening hand. Choose **Keep** to keep it, or **Mulligan** to take a new hand.

## Undo

Press **Ctrl+Z**, or the ↶ button at the top right, to undo. The game goes back to the last decision you made, and
replays the game up to that point with the same shuffle. The button is greyed out when nothing can be undone.

Undo is not available in events, and not in a game run by another player's host.

## The log

Press the ▤ button at the top right, or **Log** at the bottom left, to open the game log. A badge counts the entries you
have not read yet. The log lists what happened, in order.

## The menu

Press Esc, or the ≡ button at the top right, to open the menu:

- **Resume** closes the menu.
- **New game (same decks)** starts a new game with the same decks. It is not offered in a game run by another
  player's host.
- **Main menu** leaves the game.

At the end of a game, the screen offers **New game**, **Back to event** (in an event) and **Main menu**.

## Controls at a glance

| Control | What it does |
|---------|--------------|
| Click a card in hand | Play it |
| Drag a card from hand | Play it on release; drop it back on the hand to cancel |
| Hover a card | Large preview |
| F | Flip a double-faced card's preview to its other face |
| Space | Press the main button of the action panel |
| Ctrl+Z | Undo |
| Esc | Open the menu; close a pile view; cancel the number picker |
| Left and Right | Move the slider in the number picker |
| Enter | Confirm the number picker |

## Online games

In a game hosted by another player, the seats show who plays each one. A player who loses the connection is shown as
**disconnected**, with a countdown to the computer taking the seat. If you host the game, a button lets the computer
play for that player at once. See [Online](online.md).
