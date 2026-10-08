// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>
/// "This" in a resolving ability means the object that triggered or was activated (rule 400.7): if the source left its zone
/// and came back, it is a new object and the ability does nothing to it. An effect of the ability that moves its own source
/// can still find the object it moved ("exile this, then return it"), so later effects follow it.
/// </summary>
public sealed partial class Game
{
    /// <summary>The object version "this" refers to for the resolving ability, or null when any version will do (spells, static abilities).</summary>
    private static int? ExpectedSourceVersion(EffectContext ctx) => ctx.Results.FollowedSourceVersion ?? ctx.SourceVersion;

    /// <summary>The source is still the object the ability came from (or the object an earlier effect of it moved it to).</summary>
    private static bool IsSourceObject(EffectContext ctx) => ExpectedSourceVersion(ctx) is not { } v || v == ctx.Source.Version;

    /// <summary>The ability's own source object is on the battlefield.</summary>
    private static bool SourceOnBattlefield(EffectContext ctx) => ctx.Source.Zone == Zone.Battlefield && IsSourceObject(ctx);

    /// <summary>The ability's own source object is in that zone ("this card in your graveyard").</summary>
    private static bool SourceIn(EffectContext ctx, Zone zone) => ctx.Source.Zone == zone && IsSourceObject(ctx);

    /// <summary>After an effect: if it moved the ability's own source, the rest of the ability refers to the object it became.</summary>
    private static void FollowMovedSource(EffectContext ctx, bool wasSourceObject, int versionBefore)
    {
        if (wasSourceObject && ctx.SourceVersion is not null && ctx.Source.Version != versionBefore)
            ctx.Results.FollowedSourceVersion = ctx.Source.Version;
    }

    /// <summary>"Renowned": as the resolving ability's source last existed on the battlefield if it left (rules 603.4, 608.2h).</summary>
    private bool SourceRenownedNow(Card source)
    {
        if (ResolvingVersionOf(source) is not { } version) return source.Zone == Zone.Battlefield && source.Renowned;
        if (source.Zone == Zone.Battlefield && source.Version == version) return source.Renowned;
        return source.LastKnownInfo is { } lki && lki.Version == version && lki.Renowned;
    }
}
