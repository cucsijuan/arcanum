// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

/// <summary>A game-state check an ability or effect depends on ("if you attacked this turn", ...).</summary>
public abstract record Condition;

/// <summary>"If you attacked this turn" (raid).</summary>
public sealed record AttackedThisTurn : Condition;

/// <summary>"If a creature died this turn" (morbid).</summary>
public sealed record CreatureDiedThisTurn : Condition;

/// <summary>"If you gained life this turn" / "if you gained N or more life this turn".</summary>
public sealed record GainedLifeThisTurn(int AtLeast = 1) : Condition;

/// <summary>"If there are N or more cards in your graveyard" (threshold: seven).</summary>
public sealed record CardsInGraveyard(int AtLeast, ObjectFilter? Filter = null) : Condition;

/// <summary>"If you control [N or more] [objects]" — e.g. ferocious is a creature with power 4 or greater.</summary>
public sealed record YouControl(ObjectFilter Filter, int AtLeast = 1) : Condition;

/// <summary>"If you have N or more life".</summary>
public sealed record LifeAtLeast(int Amount) : Condition;

/// <summary>"If [condition] isn't true" / "unless".</summary>
public sealed record Not(Condition Inner) : Condition;

/// <summary>"If it was kicked" / "if this spell was kicked".</summary>
public sealed record WasKicked : Condition;

/// <summary>"If an opponent lost life this turn".</summary>
public sealed record OpponentLostLifeThisTurn : Condition;

/// <summary>"If it's your turn" / "during your turn".</summary>
public sealed record YourTurn : Condition;

/// <summary>"If the source has N or more +1/+1 counters on it".</summary>
public sealed record SourceHasCounters(int AtLeast, CounterKind Kind = CounterKind.PlusOnePlusOne) : Condition;

/// <summary>"As long as it's attacking".</summary>
public sealed record SourceAttacking : Condition;

/// <summary>"If it's a Zombie card" / "if it was a creature card": the target at <paramref name="Index"/> matches.</summary>
public sealed record TargetMatches(int Index, ObjectFilter Filter) : Condition;

/// <summary>"As long as your life total is at least N greater than your starting life total".</summary>
public sealed record LifeAboveStarting(int AtLeast) : Condition;

/// <summary>"If [condition A] and [condition B]".</summary>
public sealed record All(IReadOnlyList<Condition> Conditions) : Condition;

/// <summary>"If creatures you control have total power N or greater".</summary>
public sealed record TotalPowerAtLeast(int Amount) : Condition;

/// <summary>"If you attacked with N or more creatures" (counts creatures you control that are attacking).</summary>
public sealed record AttackingCreatures(int AtLeast) : Condition;

/// <summary>The target player at <paramref name="Index"/> has exactly <paramref name="Life"/> life.</summary>
public sealed record TargetLifeExactly(int Index, int Life) : Condition;

/// <summary>The value chosen for X is at least <paramref name="AtLeast"/>.</summary>
public sealed record XAtLeast(int AtLeast) : Condition;

/// <summary>The object the trigger was about was attacking when it left the battlefield.</summary>
public sealed record TriggeredWasAttacking : Condition;

/// <summary>The source had the subtype when it was last on the battlefield ("if it wasn't a Demon").</summary>
public sealed record SourceWasSubtype(string Subtype) : Condition;

/// <summary>The source had counters of a kind when it was last on the battlefield.</summary>
public sealed record SourceHadCounters(CounterKind Kind) : Condition;

/// <summary>This effect sacrificed at least one permanent of yours.</summary>
public sealed record YouSacrificedThisWay : Condition;

/// <summary>This effect created at least one token so far ("When you do" after "create …").</summary>
public sealed record CreatedThisWay : Condition;

/// <summary>You control at least <paramref name="AtLeast"/> matching permanents with different names.</summary>
public sealed record DifferentNames(ObjectFilter Filter, int AtLeast) : Condition;

/// <summary>This ability has resolved at least <paramref name="Times"/> times this turn (including now).</summary>
public sealed record ResolvedThisTurn(int Times, bool Exactly = false) : Condition;

/// <summary>"If you cast it from your hand" / "if you cast it".</summary>
public sealed record WasCastFromHand : Condition;

/// <summary>The object the trigger was about has at least <paramref name="AtLeast"/> +1/+1 counters.</summary>
public sealed record TriggeredHasCounters(int AtLeast) : Condition;

/// <summary>The target at <paramref name="Attached"/> is attached to the target at <paramref name="To"/>.</summary>
public sealed record TargetAttachedTo(int Attached, int To) : Condition;

/// <summary>The source currently matches the filter ("if Kellan is a Scout").</summary>
public sealed record SourceIs(ObjectFilter Filter) : Condition;

/// <summary>A quantity is at least <paramref name="AtLeast"/> ("if at least one creature card was exiled this way").</summary>
public sealed record QuantityAtLeast(Quantity Quantity, int AtLeast) : Condition;

/// <summary>"If you cast it" (from anywhere).</summary>
public sealed record WasCast : Condition;

/// <summary>"As long as it's untapped": the source permanent is untapped.</summary>
public sealed record SourceUntapped : Condition;

/// <summary>The target at <paramref name="Index"/> was controlled by the ability's controller (as it last existed if it left).</summary>
public sealed record TargetControlledByYou(int Index) : Condition;

/// <summary>"If this spell was cast from a graveyard".</summary>
public sealed record WasCastFromGraveyard : Condition;

/// <summary>"If you've drawn N or more cards this turn".</summary>
public sealed record CardsDrawnThisTurn(int AtLeast) : Condition;

/// <summary>"Creatures with total power N or greater" are attacking for the controller.</summary>
public sealed record AttackingPowerAtLeast(int Amount) : Condition;

/// <summary>"If the gift was promised".</summary>
public sealed record GiftPromised : Condition;

/// <summary>The object the trigger was about matches the filter as the ability resolves ("if that creature is legendary").</summary>
public sealed record TriggeredMatches(ObjectFilter Filter) : Condition;

/// <summary>An opponent has the most life or is tied for it.</summary>
public sealed record OpponentHasMostLife : Condition;

/// <summary>"If you have the city's blessing" (ascend).</summary>
public sealed record HasCitysBlessing : Condition;

/// <summary>Exactly N creatures you control are attacking ("attacks alone": 1).</summary>
public sealed record AttackingCreaturesExactly(int Count) : Condition;

/// <summary>"If you attacked with N or more creatures this turn".</summary>
public sealed record AttackedWithAtLeast(int Count) : Condition;

/// <summary>"As long as you have an enduring story" (storied).</summary>
public sealed record HasEnduringStory : Condition;

/// <summary>The source is its controller's Ring-bearer.</summary>
public sealed record IsRingBearer : Condition;

/// <summary>"If you control a Ring-bearer" (your Ring-bearer is on the battlefield under your control).</summary>
public sealed record HasRingBearer : Condition;

/// <summary>"If a creature died under your control this turn" (at least that many).</summary>
public sealed record YourCreaturesDied(int AtLeast) : Condition;

/// <summary>"If you sacrificed a Food this turn": you sacrificed at least that many permanents matching the filter.</summary>
public sealed record SacrificedThisTurn(ObjectFilter Filter, int AtLeast = 1) : Condition;

/// <summary>"If a permanent you controlled left the battlefield this turn".</summary>
public sealed record YourPermanentLeftThisTurn : Condition;

/// <summary>"If this creature attacked this turn".</summary>
public sealed record SourceAttackedThisTurn : Condition;

/// <summary>"If the sacrificed creature was legendary": a permanent sacrificed for this spell or ability matches (as it last existed).</summary>
public sealed record SacrificedMatches(ObjectFilter Filter) : Condition;

/// <summary>"If they attacked you that turn": the player the trigger is about attacked the controller this turn.</summary>
public sealed record TriggeredPlayerAttackedYou : Condition;

/// <summary>"As long as [equipped creature] is blocking or blocked by a [filter]".</summary>
public sealed record EquippedInCombatWith(ObjectFilter Filter) : Condition;

/// <summary>"If you control a creature with the greatest power among creatures on the battlefield" (ties included).</summary>
public sealed record YouControlGreatestPower : Condition;
