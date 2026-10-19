// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Abilities;

/// <summary>
/// Effects carried out in <c>Game.HeroesCommander.cs</c>, first needed by the heroes set's commander decks: keyword actions
/// (support, adapt, monstrosity, explore, connive) and the rules around them.
/// </summary>
public abstract record HeroesCommanderEffect : Effect;

/// <summary>
/// Evolve's intervening "if" (rule 702.100a): "if that creature's power is greater than this creature's power and/or that
/// creature's toughness is greater than this creature's toughness". Checked as the ability triggers and as it resolves, each
/// creature as it last existed on the battlefield if it has left (702.100b-c).
/// </summary>
public sealed record Evolves : Condition;

/// <summary>"Adapt N" (rule 701.46a): "If this permanent has no +1/+1 counters on it, put N +1/+1 counters on it."</summary>
public sealed record Adapt(Quantity Count, Subject What) : HeroesCommanderEffect;

/// <summary>
/// "Monstrosity N" (rule 701.37a): "If this permanent isn't monstrous, put N +1/+1 counters on it and it becomes monstrous." Monstrous
/// is a designation that stays until the permanent leaves the battlefield (701.37b).
/// </summary>
public sealed record Monstrosity(Quantity Count, Subject What) : HeroesCommanderEffect;

/// <summary>
/// "[It] explores" (rule 701.44a): its controller reveals the top card of their library. A land card goes into their hand; otherwise
/// (also when nothing is revealed) a +1/+1 counter goes on the exploring permanent and they may put the revealed card into their
/// graveyard. A permanent that has left the battlefield still explores, for its last controller, without the counter (701.44c).
/// </summary>
public sealed record Explore(Subject What) : HeroesCommanderEffect;

/// <summary>
/// "[It] connives" (rule 701.50a): its controller draws a card, then discards a card; if a nonland card was discarded, a +1/+1 counter
/// goes on the conniving permanent. One that has left the battlefield still connives, for its last controller (701.50c). Several
/// connive one at a time: in turn order, each player chooses which of theirs connives next (701.50e).
/// </summary>
public sealed record Connive(Subject What) : HeroesCommanderEffect;

/// <summary>Rebound's delayed ability (rule 702.88a): "you may cast this card from exile without paying its mana cost".</summary>
public sealed record CastReboundedFree : HeroesCommanderEffect;

/// <summary>
/// "[Player] chooses up to N [filter] they control, then sacrifices the rest": each player chooses in turn order, then all of them sacrifice
/// at the same time (rule 101.4).
/// </summary>
public sealed record SacrificeAllBut(ObjectFilter Filter, int Keep, Subject Who) : HeroesCommanderEffect
{
    /// <summary>"Each player puts a vow counter on a creature they control and sacrifices the rest": the player must keep as many as they can (up to <c>Keep</c>), not fewer.</summary>
    public bool Exactly { get; init; }

    /// <summary>A counter put on each permanent kept this way.</summary>
    public CounterKind? PutCounter { get; init; }

    /// <summary>"Each of those creatures can't attack you or planeswalkers you control for as long as it has a [counter] on it": the effect's controller; needs <see cref="PutCounter"/>.</summary>
    public bool KeptCantAttackYouWhileCounter { get; init; }
}

/// <summary>
/// "An opponent chooses one of the exiled cards. You put that card on the bottom of your library and return the other to the battlefield
/// tapped" (the cards exiled to pay the ability's cost): you choose the opponent; only cards still in exile can be chosen or returned.
/// </summary>
public sealed record OpponentSplitsExiled : HeroesCommanderEffect;

/// <summary>"If that player controls more lands than you" (the player the trigger is about).</summary>
public sealed record TriggeredPlayerHasMoreLands : Condition;

/// <summary>Turn tracking: "if you put a counter on a creature this turn", "if a counter was put on [this] this turn", "if a card left your graveyard
/// this turn", "if you lost life this turn" (with "each player": that player), "if a player lost N or more life this turn".</summary>
public sealed record TurnHappened(TurnEvent Event, int AtLeast = 1) : Condition;

public enum TurnEvent { YouPutCounterOnCreature, CounterPutOnSource, CardLeftYourGraveyard, YouLostLife, PlayerLostLife }

/// <summary>"If you have a full party" (rule 700.8): you control a Cleric, a Rogue, a Warrior and a Wizard, each a different creature.</summary>
public sealed record FullParty : Condition;

/// <summary>
/// "If any of those creatures have power or toughness equal to the chosen number": the creatures attacking the player the trigger is about
/// (not their planeswalkers), against the number the source chose as it entered.
/// </summary>
public sealed record AttackersMatchChosenNumber : Condition;

/// <summary>"You may move any number of +1/+1 counters from this creature onto other creatures": one creature at a time, as many as the player chooses.</summary>
public sealed record DistributeSourceCounters(CounterKind Kind) : HeroesCommanderEffect;

/// <summary>The source is attached to a creature ("as long as this Equipment is attached to a creature").</summary>
public sealed record SourceAttachedToCreature : Condition;

/// <summary>The player the trigger is about is the active player ("if it isn't that player's turn": its negation).</summary>
public sealed record TriggeredPlayerIsActive : Condition;

/// <summary>"If an opponent has N or more cards in their graveyard".</summary>
public sealed record OpponentGraveyardAtLeast(int Count) : Condition;

/// <summary>"Until your next turn, creatures can't attack you unless their controller pays [tax] for each of those creatures."</summary>
public sealed record AttackTaxUntilYourNextTurn(Mana.ManaCost Tax) : HeroesCommanderEffect;

/// <summary>"When that creature dies this turn, [effects]": a delayed triggered ability (rule 603.7) about that object; "triggered" is the card it became.</summary>
public sealed record WhenDiesThisTurn(Subject What, IReadOnlyList<Effect> Effects, string Text) : HeroesCommanderEffect;

/// <summary>
/// "You may put a [filter] card exiled with [this] onto the battlefield under your control [with a finality counter on it]": one card, chosen
/// among those this object exiled.
/// </summary>
public sealed record PutExiledWithThis(ObjectFilter Filter, IReadOnlyList<CounterKind> CounterKinds) : HeroesCommanderEffect;

/// <summary>"Exile target [permanent] and all tokens that player controls with the same name as that permanent" (one event).</summary>
public sealed record ExileWithSameNameTokens(Subject What) : HeroesCommanderEffect;

/// <summary>The attacker the trigger is about is attacking one of your opponents (or their planeswalker) and is blocked by at least N creatures.</summary>
public sealed record TriggeredBlockedAttackingYourOpponent(int Blockers) : Condition;

/// <summary>"Return one of them …": the player chooses one object among <paramref name="Among"/>; it becomes "found" for the next effects.</summary>
public sealed record ChooseOneOf(Subject Among) : HeroesCommanderEffect;

/// <summary>"A creature is blocking" (combat with at least one blocking creature).</summary>
public sealed record CreatureBlocking : Condition;

/// <summary>
/// "Starting with the next opponent in turn order, each opponent chooses a [filter] card in your graveyard that hasn't been chosen. Return each
/// card chosen this way to the battlefield under your control."
/// </summary>
public sealed record OpponentsChooseFromYourGraveyard(ObjectFilter Filter) : HeroesCommanderEffect;

/// <summary>"Exile a card at random from each opponent's graveyard" (the cards become "exiled").</summary>
public sealed record ExileRandomFromOpponentsGraveyards : HeroesCommanderEffect;

/// <summary>
/// "You may cast any number of spells from among cards exiled this way without paying their mana costs. Then each player who owns a spell you
/// cast this way loses life equal to its mana value" (with <see cref="OwnersLoseLife"/>).
/// </summary>
public sealed record CastExiledFreeAnyNumber(bool OwnersLoseLife) : HeroesCommanderEffect;

/// <summary>
/// "Becomes a copy of [target] until end of turn, except its name is [ExceptName]": lasts until cleanup (rule 707). With <see cref="Lasting"/>
/// it has no duration ("you may have this become a copy of that creature"); <see cref="Keywords"/> and <see cref="KeepThisAbility"/> are the
/// other exceptions ("… and it has vigilance and this ability"), part of its copiable values (rule 707.9b).
/// </summary>
public sealed record BecomeCopy(Subject What, Subject Target, string? ExceptName = null) : HeroesCommanderEffect
{
    public bool Lasting { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public bool KeepThisAbility { get; init; }
}

/// <summary>
/// "Exile a [filter] card from each graveyard": the controller chooses one in each graveyard that has one, and they're exiled together, with this object
/// (linked, rule 607).
/// </summary>
public sealed record ExileOneFromEachGraveyard(ObjectFilter Filter) : HeroesCommanderEffect;

/// <summary>
/// Warp-strike: "Exile [this]. Return it to the battlefield under its owner's control tapped and attacking at the beginning of that player's next
/// declare attackers step. It can't be blocked that combat."
/// </summary>
public sealed record WarpStrike(Subject What) : HeroesCommanderEffect;

/// <summary>The delayed part of <see cref="WarpStrike"/>: the card the ability is about returns tapped and attacking, unblockable that combat.</summary>
public sealed record ReturnTappedAttackingUnblockable : HeroesCommanderEffect;

/// <summary>"You may cast it from your graveyard as an Adventure until the end of your next turn" (about the card that died, rule 400.7).</summary>
public sealed record AdventureFromGraveyardUntilYourNextTurn(Subject What) : HeroesCommanderEffect;

/// <summary>"Until end of turn, you may play cards exiled with [this]. Spells you cast this way cost {N} less to cast" (<see cref="ExiledWithPlayRight"/>).</summary>
public sealed record PlayExiledWithThisThisTurn(int CostLess) : HeroesCommanderEffect;

/// <summary>"If this spell was cast from exile" (foretold cards, rule 702.143).</summary>
public sealed record WasCastFromExile : Condition;

/// <summary>"Nonartifact spells you cast have improvise" (rule 702.126): while the source is on the battlefield, spells its controller casts that match
/// <paramref name="Spells"/> have improvise, as if they printed it.</summary>
public sealed record SpellsHaveImprovise(ObjectFilter Spells) : AbilityDefinition;

/// <summary>"Instant and sorcery spells you cast have storm" (an emblem's or permanent's ability; each instance triggers separately, rule 702.40b).</summary>
public sealed record SpellsHaveStorm(ObjectFilter Spells) : AbilityDefinition;

/// <summary>"Enchantment spells you cast from your hand have cascade."</summary>
public sealed record SpellsHaveCascade(ObjectFilter Spells) : AbilityDefinition;

/// <summary>"If you would create a Fish token, create a 3/3 blue Shark creature token instead" (a replacement effect, rule 614.1a).</summary>
public sealed record TokenReplacement(string Subtype, Cards.CardDefinition Instead) : AbilityDefinition;
