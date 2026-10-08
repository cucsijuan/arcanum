// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

/// <summary>"If a player cast two or more spells last turn": some player cast at least that many spells during the previous turn.</summary>
public sealed record SpellsCastLastTurn(int AtLeast) : Condition;

/// <summary>
/// "If [this] has dealt 3 or more damage this turn": the damage the source object has dealt this turn, all of it (combat or not, to
/// anything), as it was dealt after prevention and replacement; a source that left the battlefield is the object it was (rule 400.7).
/// </summary>
public sealed record SourceDealtDamageThisTurn(int AtLeast) : Condition;
