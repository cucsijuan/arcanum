// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

/// <summary>Effects carried out in <c>Game.Origins.cs</c> (zone sweeps, casting during resolution, naming and searching).</summary>
public abstract record OriginsEffect : Effect;

/// <summary>
/// "Each player shuffles their hand and graveyard into their library" (Day's Undoing) / "… all cards from their hand and all
/// permanents they own into their library, then draws that many cards" (The Great Aurora): every card moves at once (one event,
/// tokens included and then gone), each library is shuffled, and with <see cref="DrawThatMany"/> each player draws as many cards
/// as they put into their library this way (tokens counted).
/// </summary>
public sealed record ShuffleIntoLibraries(Subject Who, bool Hand, bool Graveyard, bool Permanents) : OriginsEffect
{
    public bool DrawThatMany { get; init; }
}

/// <summary>
/// "Each player may put any number of [filter] cards from their hand onto the battlefield": each player chooses in turn order
/// (rule 101.4), then all the chosen cards enter at the same time, each under the control of the player who put it there.
/// </summary>
public sealed record EachPutsFromHand(Subject Who, ObjectFilter Filter) : OriginsEffect;

/// <summary>
/// "[Player] reveals the top N cards of their library. You may cast [filter] spells from among them without paying their mana
/// costs (up to <see cref="Casts"/>, or <see cref="MoreCasts"/> while <see cref="MoreCastsIf"/> holds). Then that player puts the
/// rest into their graveyard." The spells are cast while this resolves (rule 608.2g), so timing permissions don't matter.
/// </summary>
public sealed record RevealTopCastFree(Subject Whose, int Count, ObjectFilter Filter, int Casts) : OriginsEffect
{
    public Condition? MoreCastsIf { get; init; }
    public int MoreCasts { get; init; }

    /// <summary>The revealed cards not cast go to their owner's graveyard (otherwise they stay on top of the library).</summary>
    public bool RestToGraveyard { get; init; }
}

/// <summary>
/// "Choose a [filter] card name. Search target opponent's graveyard, hand, and library for any number of cards with that name and
/// exile them. That player shuffles." The searcher sees the hand and library and may leave any of those cards (rule 701.19b).
/// </summary>
public sealed record NameThenExileFromAllZones(Subject Whose, ObjectFilter NameFilter) : OriginsEffect;
