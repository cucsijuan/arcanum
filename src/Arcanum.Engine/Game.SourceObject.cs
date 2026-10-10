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
        return ResolvingSourceLastKnown(source, version) is { } lki && lki.Renowned;
    }

    /// <summary>
    /// The object that left the battlefield as the ability triggered or was paid for, when the ability's source object
    /// (<paramref name="sourceVersion"/>) is the one it became: a "when this dies / leaves the battlefield" ability, or one that
    /// sacrificed it as a cost. "This" then means the permanent as it last existed on the battlefield (rule 608.2h).
    /// </summary>
    private static int? LeftBattlefieldVersion(Abilities.AbilityDefinition ability, Card source, int sourceVersion)
    {
        bool left = ability is Abilities.TriggeredAbility { Trigger: Abilities.TriggerEvent.Dies or Abilities.TriggerEvent.LeavesBattlefield or Abilities.TriggerEvent.PutIntoGraveyard }
                    || ability is Abilities.ActivatedAbility { Cost.SacrificeSelf: true };
        return left && source.LastKnownOf(sourceVersion - 1) is not null ? sourceVersion - 1 : null;
    }

    /// <summary>How the resolving ability's source object (<paramref name="version"/>) last existed on the battlefield, if it did (rule 608.2h).</summary>
    private LastKnown? ResolvingSourceLastKnown(Card source, int version) =>
        source.LastKnownOf(_resolvingSource is { LeftVersion: { } left } r && r.Card == source.Id ? left : version);

    /// <summary>
    /// "For each counter on this": the counters on the ability's own source object, wherever it is, or as it last existed on the
    /// battlefield once it left (rule 608.2h); a card that came back since is a new object whose counters don't count (rule 400.7).
    /// Without an object version (a static ability, a spell), the card as it is, or as it last existed on the battlefield.
    /// </summary>
    private static int SourceCounterCount(EffectContext ctx, Abilities.CounterKind kind)
    {
        var source = ctx.Source;
        if (ctx.Results.FollowedSourceVersion is null && ctx.SourceLeftVersion is { } left)
            return source.LastKnownOf(left)?.Counters.GetValueOrDefault(kind) ?? 0;
        if (ExpectedSourceVersion(ctx) is not { } version)
            return source.Zone == Zone.Battlefield || source.LastKnownInfo is null ? source.CounterCount(kind) : source.LastKnownInfo.Counters.GetValueOrDefault(kind);
        if (source.Version == version) return source.CounterCount(kind);
        return source.LastKnownOf(version)?.Counters.GetValueOrDefault(kind) ?? 0;
    }

    /// <summary>
    /// "Its power" for the object a trigger is about (<paramref name="version"/>, rule 608.2h): the permanent as it is if it is
    /// still that object on the battlefield, otherwise as that object last existed there; for the object a permanent became as it
    /// left the battlefield ("when enchanted creature dies"), the permanent that left. An object that never was a permanent
    /// has the power it has where it is, or its printed power once it became a permanent (a new object).
    /// </summary>
    private static int TriggeredPowerOf(Card subject, int version)
    {
        if (subject.Version == version)
            return subject.Zone == Zone.Battlefield ? subject.Power : subject.LastKnownOf(version - 1)?.Power ?? subject.Power;
        if ((subject.LastKnownOf(version) ?? subject.LastKnownOf(version - 1)) is { } lki) return lki.Power;
        return subject.Zone == Zone.Battlefield ? subject.PrintedDefinition.Power ?? 0 : subject.Power;
    }
}
