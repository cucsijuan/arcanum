// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Arcanum.Data.Career;

/// <summary>
/// One thing the player did, as kept in the career's journal. The journal replayed with <see cref="CareerRules"/> from
/// the start gives the career's state, so a career can be checked by recomputing it. Each action carries the time it
/// happened (UTC); what it costs or gives is decided by the rules, never by the action.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "do")]
[JsonDerivedType(typeof(StartCareer), "start")]
[JsonDerivedType(typeof(StartDaily), "daily")]
[JsonDerivedType(typeof(FinishMatch), "result")]
[JsonDerivedType(typeof(StartPractice), "practice")]
[JsonDerivedType(typeof(StartAnte), "ante")]
[JsonDerivedType(typeof(BuyBooster), "buyBooster")]
[JsonDerivedType(typeof(BuyPrecon), "buyPrecon")]
[JsonDerivedType(typeof(OpenBooster), "open")]
[JsonDerivedType(typeof(SellCard), "sell")]
[JsonDerivedType(typeof(TakeOffer), "offer")]
[JsonDerivedType(typeof(EnterEvent), "enter")]
[JsonDerivedType(typeof(FinishEvent), "finishEvent")]
[JsonDerivedType(typeof(BuySingle), "buySingle")]
[JsonDerivedType(typeof(BuyListing), "buyListing")]
[JsonDerivedType(typeof(OfferOnListing), "offerListing")]
[JsonDerivedType(typeof(PostSale), "postSale")]
[JsonDerivedType(typeof(AnswerSaleOffer), "answerSale")]
[JsonDerivedType(typeof(TakeDownSale), "takeDownSale")]
[JsonDerivedType(typeof(CheckSales), "checkSales")]
[JsonDerivedType(typeof(BuyPlaymat), "buyPlaymat")]
[JsonDerivedType(typeof(UsePlaymat), "usePlaymat")]
public abstract record CareerAction(DateTime Utc);

/// <summary>A new career: its seed and the preconstructed deck chosen to start with.</summary>
public sealed record StartCareer(DateTime Utc, ulong Seed, string Deck) : CareerAction(Utc);

/// <summary>The next daily match starts, with the prize chosen for a win ("cash" or "booster") and the player's deck list.</summary>
public sealed record StartDaily(DateTime Utc, string Reward, string? Deck = null) : CareerAction(Utc);

/// <summary>A practice match against a regular the player chose.</summary>
public sealed record StartPractice(DateTime Utc, string Opponent, string Deck) : CareerAction(Utc);

/// <summary>
/// An ante match against a regular: the player stakes a card of their collection (<see cref="Card"/>, a <see cref="CardKey"/>
/// text) against the one of the regular's offered cards they chose (<see cref="Theirs"/>); the winner keeps both.
/// Without a card (older journals) each player antes a card at random from their deck.
/// </summary>
public sealed record StartAnte(DateTime Utc, string Opponent, string Deck, string? Card = null, string? Theirs = null) : CareerAction(Utc);

/// <summary>The match in progress (daily, practice or ante) ended.</summary>
public sealed record FinishMatch(DateTime Utc, bool Won) : CareerAction(Utc);

/// <summary>Buys a booster of a set: of a kind ("set", "collector"...), or its play or draft booster when none.</summary>
public sealed record BuyBooster(DateTime Utc, string Set, string? Kind = null) : CareerAction(Utc);

public sealed record BuyPrecon(DateTime Utc, string Deck) : CareerAction(Utc);

/// <summary>Opens an unopened booster of a set; its cards come from the career seed and the number of boosters opened.</summary>
public sealed record OpenBooster(DateTime Utc, string Set, string? Kind = null) : CareerAction(Utc);

/// <summary>Enters today's event, paying with "cash", "boosters" or "tokens"; a constructed event takes the deck list played.</summary>
public sealed record EnterEvent(DateTime Utc, string Event, string Pay, string? Deck = null) : CareerAction(Utc);

/// <summary>
/// The event entered is over: the player's record, their place among the players, and in a draft the cards they
/// drafted (they keep them).
/// </summary>
public sealed record FinishEvent(DateTime Utc, int Wins, int Losses, int Draws, int Place, IReadOnlyList<string>? Pool = null) : CareerAction(Utc);

/// <summary>Takes one of today's offers at the store (its index among them).</summary>
public sealed record TakeOffer(DateTime Utc, int Offer) : CareerAction(Utc);

/// <summary>Sells copies of a card to the store.</summary>
public sealed record SellCard(DateTime Utc, string Card, int Count) : CareerAction(Utc);

/// <summary>Buys one of today's singles in the store's case (its index).</summary>
public sealed record BuySingle(DateTime Utc, int Index) : CareerAction(Utc);

/// <summary>Buys one of today's forum listings at its price (or at the seller's counter-offer when there is one).</summary>
public sealed record BuyListing(DateTime Utc, int Index) : CareerAction(Utc);

/// <summary>Offers a price (cents) for one of today's forum listings: the seller takes it, counters or turns it down.</summary>
public sealed record OfferOnListing(DateTime Utc, int Index, int Amount) : CareerAction(Utc);

/// <summary>Posts a card of the collection for sale on the forum at a price (cents).</summary>
public sealed record PostSale(DateTime Utc, string Card, int Ask) : CareerAction(Utc);

/// <summary>Takes or turns down the offer a buyer made on one of the player's posts.</summary>
public sealed record AnswerSaleOffer(DateTime Utc, int Post, bool Accept) : CareerAction(Utc);

/// <summary>Takes one of the player's posts down: the card comes back.</summary>
public sealed record TakeDownSale(DateTime Utc, int Post) : CareerAction(Utc);

/// <summary>Looks at the forum: the player's posts catch up with the days gone by (sold, offered, expired).</summary>
public sealed record CheckSales(DateTime Utc) : CareerAction(Utc);

/// <summary>Buys one of the store's playmats (its id); the player puts it on the table straight away.</summary>
public sealed record BuyPlaymat(DateTime Utc, string Playmat) : CareerAction(Utc);

/// <summary>Puts one of the player's playmats on the table, or none (an empty id).</summary>
public sealed record UsePlaymat(DateTime Utc, string Playmat) : CareerAction(Utc);
