// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Views;

namespace Arcanum.Engine.Players;

/// <summary>Why a player is choosing cards, so a computer player can judge which to pick.</summary>
public enum CardChoicePurpose
{
    /// <summary>Scry: the chosen cards go to the bottom of the library.</summary>
    ScryToBottom,
    /// <summary>Surveil: the chosen cards go to the graveyard.</summary>
    SurveilToGraveyard,
    /// <summary>The chosen cards are discarded.</summary>
    Discard,
    /// <summary>The chosen cards are put into the chooser's hand (search, return from graveyard...).</summary>
    ToHand,
    /// <summary>The chosen cards are put onto the battlefield.</summary>
    ToBattlefield,
    /// <summary>The chosen permanents are sacrificed.</summary>
    Sacrifice,
}

/// <summary>
/// Choose between <see cref="Min"/> and <see cref="Max"/> of <see cref="Options"/>. The options are shown to the
/// chooser even when they come from a hidden zone (the top of their library, for example).
/// </summary>
public sealed record CardChoiceRequest(
    string Prompt, CardId? Source, IReadOnlyList<CardView> Options, int Min, int Max, CardChoicePurpose Purpose);
