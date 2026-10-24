# Decks

The deck builder is in the main menu under **Decks**. It works with every card the content module has. Your career
has its own deck desk, which uses only the cards you own (see [Career](career.md)).

## The screen

The top bar has a deck picker and these buttons:

| Button | What it does |
|--------|--------------|
| **New** | Starts an empty deck. Asks first if the current deck has unsaved changes. |
| **Import** | Opens a box where you paste a deck list, then builds the deck from it. |
| **Export** | Copies the deck list to the clipboard. |
| **Delete** | Deletes the deck after a confirmation. This can't be undone. |
| **Save** | Saves the deck. |

Choose a saved deck in the picker to open it. Give the deck a name in the name box.

Import reads a text list of the deck. A card the content doesn't know is shown in red, and the message says how many
unknown cards there are.

## Formats

The **Format** box lists the formats of the content module. The deck's problems are checked against its format as you
build, and the screen says **Ready to play.** when the deck is legal. Otherwise it lists the problems.

For Commander formats, put your commander in the **Commander** tab.

## Building

The left side is the card search:

- Type a name, or use the filters, to find cards. **Prev** and **Next** turn the pages.
- Click a card to add a copy to the deck. Right-click a card to take a copy out.
- Hover a card for a large preview. A double-faced card's other face is shown beside it, on the side away from the pointer.
- Pick a set to see that set's printings and their art, then choose the one the deck uses.

The search takes the terms of the search box's help line. Some of them are about prices and printings:

- `eur<1` and `usd>=5` find the cards with a printing at that price (Cardmarket euros, TCGplayer dollars). The price is the
  cheaper of the printing's foil and nonfoil prices.
- `order:eur` and `order:usd` sort by the cheapest printing. Cards with no price come last.
- `is:fullart`, `is:showcase`, `is:etched`, `is:firstprint` and the other `is:` printing kinds, `border:borderless` and
  `frame:2015` (or a frame effect such as `frame:showcase`).
- Curated nicknames such as `is:shockland`, `is:fetchland`, `is:triome`, `is:bounceland` and `is:bear` find exactly the cards
  (or printings) the card data's source tags with them, so the aliases (`is:karoo`, `is:battleland`...) match the same lists.
  `is:alchemy`, `is:rebalanced` and `is:oversized` match no card.
- Promo kinds the printings have, by their own names: `is:prerelease`, `is:serialized`, `is:universesbeyond` (or `is:ub`) and
  the promo kind that names the game or story a crossover card comes from. Underscores are ignored, so `is:promo_pack` works as `is:promopack`.

## Prices

Prices are Cardmarket prices in euros, from when the module's card data was downloaded. They are not live.

- Each result tile shows its price. A tile for a printing shows that printing's nonfoil price. Without a chosen set, a tile
  shows the price of the cheapest printing.
- The deck list ends with the Cardmarket total of the section. Each copy counts at its chosen printing's price (the foil
  price for a foil copy), or at the cheapest printing's when none is chosen. Copies with no price are counted apart.
- The career deck desk keeps its own prices (see [Career](career.md)).

The right side shows the deck grouped by card type, with a mana curve. Tabs switch between:

- **Main**: the cards of the deck.
- **Sideboard**: cards kept outside the main deck.
- **Commander**: the commander, in Commander formats.
- **Tokens**: shown only when the deck's cards make tokens. Choose the pictures the tokens are shown with.

## Suggestions

When the content module has community deck statistics, a **Suggestions** switch appears above the search.

- **In a Commander deck** with a commander chosen, the suggestions are the cards most played with that commander by
  community decks. Sort them by **Most played** or **Highest synergy**. Filter them to all decks, budget decks,
  expensive decks or one theme.
- **In another format**, the suggestions come from community decks like yours. They are the cards those decks play,
  and each card shows how many of those decks play it.

The sort box has **Most played**, **Highest synergy**, **Lowest price** and **Highest price**, in both kinds. The price
sorts use the Cardmarket price of the cheapest printing, and cards with no price come last.

Click a suggested card to add it to your deck. **Use average deck** replaces the main deck with the average of the
suggested decks. A note on the screen gives the decks the numbers come from.

If the module has no statistics for the format, the switch does not appear. If none can be downloaded, the screen says
so.

## Where decks are saved

Saved decks are plain-text lists on this computer. You can share a deck by exporting it and sending the text.

## Related

- [Playing](playing.md) explains how to start a game with a deck.
- [Limited](limited.md) explains the decks you build in draft and sealed events.
- [Career](career.md) explains the career deck desk.
