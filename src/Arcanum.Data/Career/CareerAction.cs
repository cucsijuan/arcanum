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
[JsonDerivedType(typeof(FinishDaily), "result")]
[JsonDerivedType(typeof(BuyBooster), "buyBooster")]
[JsonDerivedType(typeof(BuyPrecon), "buyPrecon")]
[JsonDerivedType(typeof(OpenBooster), "open")]
[JsonDerivedType(typeof(SellCard), "sell")]
public abstract record CareerAction(DateTime Utc);

/// <summary>A new career: its seed and the preconstructed deck chosen to start with.</summary>
public sealed record StartCareer(DateTime Utc, ulong Seed, string Deck) : CareerAction(Utc);

/// <summary>The next daily match starts, with the prize chosen for a win ("cash" or "booster").</summary>
public sealed record StartDaily(DateTime Utc, string Reward) : CareerAction(Utc);

/// <summary>The daily match in progress ended.</summary>
public sealed record FinishDaily(DateTime Utc, bool Won) : CareerAction(Utc);

public sealed record BuyBooster(DateTime Utc, string Set) : CareerAction(Utc);

public sealed record BuyPrecon(DateTime Utc, string Deck) : CareerAction(Utc);

/// <summary>Opens an unopened booster of a set; its cards come from the career seed and the number of boosters opened.</summary>
public sealed record OpenBooster(DateTime Utc, string Set) : CareerAction(Utc);

/// <summary>Sells copies of a card to the store.</summary>
public sealed record SellCard(DateTime Utc, string Card, int Count) : CareerAction(Utc);
