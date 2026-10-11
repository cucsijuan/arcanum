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
public abstract record CareerAction(DateTime Utc);

/// <summary>A new career: its seed and the preconstructed deck chosen to start with.</summary>
public sealed record StartCareer(DateTime Utc, ulong Seed, string Deck) : CareerAction(Utc);

/// <summary>The next daily match starts, with the prize chosen for a win ("cash" or "booster") and the player's deck list.</summary>
public sealed record StartDaily(DateTime Utc, string Reward, string? Deck = null) : CareerAction(Utc);

/// <summary>A practice match against a regular the player chose.</summary>
public sealed record StartPractice(DateTime Utc, string Opponent, string Deck) : CareerAction(Utc);

/// <summary>An ante match against a regular: each player antes a card at random from their deck; the winner keeps both.</summary>
public sealed record StartAnte(DateTime Utc, string Opponent, string Deck) : CareerAction(Utc);

/// <summary>The match in progress (daily, practice or ante) ended.</summary>
public sealed record FinishMatch(DateTime Utc, bool Won) : CareerAction(Utc);

public sealed record BuyBooster(DateTime Utc, string Set) : CareerAction(Utc);

public sealed record BuyPrecon(DateTime Utc, string Deck) : CareerAction(Utc);

/// <summary>Opens an unopened booster of a set; its cards come from the career seed and the number of boosters opened.</summary>
public sealed record OpenBooster(DateTime Utc, string Set) : CareerAction(Utc);

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
