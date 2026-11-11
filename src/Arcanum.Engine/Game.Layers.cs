// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Mana value; a spell on the stack counts the X chosen for it (rule 202.3e).</summary>
    private int ManaValueOf(Card card)
    {
        int value = card.ManaValue;
        if (card.Zone == Zone.Stack && card.Definition.ManaCost.XCount > 0 && State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == card.Id) is { } spell)
            value += spell.X * card.Definition.ManaCost.XCount;
        return value;
    }

    /// <summary>
    /// Whether <paramref name="obj"/> (controlled by <paramref name="objController"/>) fits the filter, seen from the ability's side.
    /// With <paramref name="lastKnown"/>, a permanent that left the battlefield is judged as it last existed there (rule 603.10a).
    /// </summary>
    private bool Matches(ObjectFilter filter, Card obj, PlayerId objController, Card? source, PlayerId sourceController, bool lastKnown = false)
    {
        var lk = lastKnown && obj.Zone != Zone.Battlefield ? obj.LastKnownInfo : null;
        var types = lk?.Types ?? obj.Types;
        bool HasSubtype(string s) => lk?.HasSubtype(s) ?? obj.HasSubtype(s);
        bool HasKeyword(Keyword k) => lk?.Keywords.Contains(k) ?? obj.Has(k);
        int power = lk?.Power ?? obj.Power, toughness = lk?.Toughness ?? obj.Toughness;
        var colors = lk?.Colors ?? ColorsOf(obj);
        var supertypes = lk?.Supertypes ?? obj.Supertypes;
        var attachedTo = lk is null ? obj.AttachedTo : lk.AttachedTo;
        if (filter.Types != 0 && (types & filter.Types) == 0) return false;
        if ((types & filter.AllTypes) != filter.AllTypes) return false;
        if ((types & filter.ExcludedTypes) != 0) return false;
        if (filter.Subtype is { } subtype && !HasSubtype(subtype)) return false;
        if (filter.Controller == ControllerFilter.You && objController != sourceController) return false;
        if (filter.Controller == ControllerFilter.Opponent && objController == sourceController) return false;
        if (filter.Other && source is not null && obj.Id == source.Id) return false;
        if (filter.MaxManaValueSourceX && (source is null || ManaValueOf(obj) > source.CastX)) return false;
        if (filter.EnchantedByYours && !State.Battlefield.Select(State.GetCard).Any(c => c.AttachedTo == obj.Id && c.HasSubtype("Aura") && c.Controller == sourceController)) return false;
        if (filter.MinPower is { } min && power < min) return false;
        if (filter.MaxPower is { } maxPower && power > maxPower) return false;
        if (filter.MinToughness is { } minToughness && toughness < minToughness) return false;
        if (filter.MinManaValue is { } minMv && ManaValueOf(obj) < minMv) return false;
        if (filter.MaxManaValue is { } maxMv && ManaValueOf(obj) > maxMv) return false;
        if (filter.Token is { } token && obj.Definition.IsToken != token) return false;
        if (filter.Colors is { Count: > 0 } wanted && !colors.Any(wanted.Contains)) return false;
        if (filter.NotColors is { Count: > 0 } unwanted && colors.Any(unwanted.Contains)) return false;
        if (filter.Keyword is { } keyword && !HasKeyword(keyword)) return false;
        if (filter.WithoutKeyword is { } without && HasKeyword(without)) return false;
        if (filter.Tapped is { } tapped && (lk?.Tapped ?? obj.Tapped) != tapped) return false;
        if (filter.FaceDown is { } faceDown && obj.TurnedFaceDown != faceDown) return false;
        if (filter.TwoOrMoreCardTypes && System.Numerics.BitOperations.PopCount((uint)(obj.Types & ~CardType.Kindred)) + (obj.Is(CardType.Kindred) ? 1 : 0) < 2) return false;
        if (filter.PowerParityChosen && (source?.ChosenParity is not { } powerParity || (obj.Power % 2 == 0 ? "even" : "odd") != powerParity)) return false;
        if (filter.NotNamedLikeYourRooms)
        {
            var names = obj.PrintedDefinition.SplitHalves?.Select(h => h.Name).ToList() ?? new List<string> { obj.Name };
            if (State.PermanentsControlledBy(sourceController).Any(r => r.HasSubtype("Room") && names.Any(n => HasName(r, n)))) return false;
        }
        if (filter.NotAttachedToSource && source is not null
            && (source.Zone == Zone.Battlefield || source.LastKnownInfo is null ? source.AttachedTo : source.LastKnownInfo.AttachedTo) == obj.Id) return false;
        if (filter.Supertype != 0 && (supertypes & filter.Supertype) == 0) return false;
        if (filter.ExcludedSupertype != 0 && (supertypes & filter.ExcludedSupertype) != 0) return false;
        if (filter.MaxManaValueLandCount && obj.ManaValue > State.PermanentsControlledBy(sourceController).Count(c => c.Is(CardType.Land))) return false;
        if (filter.ExcludedSubtype is { } excluded && HasSubtype(excluded)) return false;
        if (filter.Name is { } name && (lk?.Name ?? obj.Name) != name) return false;
        if (filter.NotName is { } notName && (lk?.Name ?? obj.Name) == notName) return false;
        if (filter.MaxManaValueSpentOnSource && (source is null || ManaValueOf(obj) > source.ManaSpent)) return false;
        if (filter.MaxManaValueColorsSpentOnSource && (source is null || ManaValueOf(obj) > source.SpentManaTypes
                .Where(t => t is Mana.ManaType.White or Mana.ManaType.Blue or Mana.ManaType.Black or Mana.ManaType.Red or Mana.ManaType.Green).Distinct().Count())) return false;
        if (filter.OwnedByYou && obj.Owner != sourceController) return false;
        if (filter.PutIntoZoneThisTurn && obj.ZoneChangedTurn != State.TurnNumber) return false;
        if (filter.AttackingYou && State.Combat?.FindAttack(obj.Id)?.Defender != sourceController) return false;
        if (filter.AttackingYouDirectly && (State.Combat?.FindAttack(obj.Id) is not { } direct || direct.Defender != sourceController || direct.Planeswalker is not null)) return false;
        if (filter.AttackingYourOpponent && (State.Combat?.FindAttack(obj.Id) is not { } onOpponent || onOpponent.Planeswalker is not null
                                             || onOpponent.Defender == sourceController || !State.OpponentsOf(sourceController).Contains(onOpponent.Defender))) return false;
        if (filter.FromGraveyard && !(obj.Zone == Zone.Stack ? obj.CastFromGraveyard : obj.EnteredFrom == Zone.Graveyard)) return false;
        if (filter.EnteredOrCastFromGraveyard && !(obj.EnteredFrom == Zone.Graveyard || obj.EnteredCastFromGraveyard)) return false;
        if (filter.CastFromHand && !obj.CastFromHand) return false;
        if (filter.BasePower is { } basePower && obj.BasePower != basePower) return false;
        if (filter.BasePowerOrToughness is { } baseStat && obj.BasePower != baseStat && obj.BaseToughness != baseStat) return false;
        if (filter.WasCast is { } wasCast && obj.WasCast != wasCast) return false;
        if (filter.NotPutByThis && source is not null && obj.PutOntoBattlefieldBy == source.Id) return false;
        if (filter.FromExile && !(obj.Zone == Zone.Stack ? obj.CastFromExile : obj.EnteredFrom == Zone.Exile)) return false;
        if (filter.HasXInCost && obj.Definition.ManaCost.XCount == 0) return false;
        if (filter.ExiledWithSource && (source is null || obj.Zone != Zone.Exile || obj.ExiledWith is not { } with || with.Source != source.Id || with.Version != source.Version)) return false;
        if (filter.ManaValueIsX && obj.ManaValue != _announcedX) return false;
        if (filter.ManaValueIsTriggerAmount && ManaValueOf(obj) != _triggeredAmount) return false;
        if (filter.IsSource && (source is null || obj.Id != source.Id)) return false;
        if (!MatchesOriginsFilters(filter, obj, power, toughness, HasKeyword, sourceController)) return false;
        if (filter.ChosenName && (source?.ChosenName is not { } chosenName || (lk?.Name ?? obj.Name) != chosenName)) return false;
        if (filter.SharesCreatureTypeWithTriggered && !(_triggeredSubject is { } sharedWith && SharesCreatureType(obj, State.GetCard(sharedWith)))) return false;
        if (filter.Renowned && !obj.Renowned) return false;
        if (filter.PreparedSpell && !obj.CastPrepared) return false;
        if (filter.IsRemembered && (source?.Remembered is not { } remembered || remembered.Card != obj.Id || remembered.Version != obj.Version)) return false;
        if (filter.ToughnessAbovePower && obj.Toughness <= obj.Power) return false;
        if (filter.DamagedThisTurnByRemembered && !DamagedByRemembered(obj, source, lastKnown)) return false;
        if (filter.Transformed is { } transformed && !(obj.Zone == Zone.Battlefield && obj.IsDoubleFaced && obj.Transformed == transformed)) return false;
        // An Aura that left the battlefield in the same event as its creature still sees it die, attached as it last was (603.10a).
        if (filter.AttachedToSource && (source is null
                || (source.Zone == Zone.Battlefield || source.LastKnownInfo is null ? source.AttachedTo : source.LastKnownInfo.AttachedTo) != obj.Id)) return false;
        if (filter.DamagedBySource && (source is null || !DamagedThisTurnBy(obj, lastKnown, (r, _) => r.Source == source.Id && r.SourceVersion == DamageSourceVersion(source)))) return false;
        if (filter.Attached is { } attached && (attachedTo is not null) != attached) return false;
        if (filter.TargetsMatching is { } targeted && !(obj.Zone == Zone.Stack && State.Stack.OfType<SpellOnStack>().FirstOrDefault(s => s.Card == obj.Id) is { } targeting
                && targeting.Targets.Any(t => t.Target.Card is { } tc && State.GetCard(tc) is { Zone: Zone.Battlefield } tcard
                                              && Matches(targeted, tcard, tcard.Controller, source, sourceController)))) return false;
        if (filter.AttachedToCreature && !(attachedTo is { } host && State.GetCard(host) is { Zone: Zone.Battlefield, IsCreature: true })) return false;
        if (filter.MaxManaValueSourcePower && source is not null
            && obj.ManaValue > (source.Zone == Zone.Battlefield || source.LastKnownInfo is null ? source.Power : source.LastKnownInfo.Power)) return false;
        if (filter.LessManaValueThanSourcePower && source is not null
            && obj.ManaValue >= (source.Zone == Zone.Battlefield || source.LastKnownInfo is null ? source.Power : source.LastKnownInfo.Power)) return false;
        if (filter.ChosenColor && (source?.ChosenColor is not { } color || !colors.Contains(color))) return false;
        if (filter.ChosenType && (source?.ChosenType is not { } type || !HasSubtype(type))) return false;
        if (filter.AnyOf is { Count: > 0 } anyOf && !anyOf.Any(f => Matches(f with { Controller = ControllerFilter.Any }, obj, objController, source, sourceController, lastKnown))) return false;
        if (filter.WithCounterKind is { } counterKind && (lk is null ? obj.CounterCount(counterKind) : lk.Counters.GetValueOrDefault(counterKind)) <= 0) return false;
        if (filter.HasCounters is { } hasCounters && ((lk is null ? obj.CounterCount(CounterKind.PlusOnePlusOne) : lk.Counters.GetValueOrDefault(CounterKind.PlusOnePlusOne)) > 0) != hasCounters) return false;
        if (filter.Enchanted is { } enchanted && HasAttached(obj, "Aura") != enchanted) return false;
        if (filter.Equipped is { } equipped && HasAttached(obj, "Equipment") != equipped) return false;
        if (filter.Commander is { } commander && obj.IsCommander != commander) return false;
        if (filter.InHand is { } inHand && (obj.Zone == Zone.Hand) != inHand) return false;
        if (filter.MaxToughness is { } maxToughness && toughness > maxToughness) return false;
        if (filter.MaxPowerPlusToughness is { } maxSum && power + toughness > maxSum) return false;
        if (filter.NotChosenType && source?.ChosenType is { } notType && HasSubtype(notType)) return false;
        if (filter.LeastPower && obj.IsCreature
            && State.PermanentsControlledBy(objController).Where(c => c.IsCreature).Any(c => c.Power < power)) return false;
        if (filter.GreatestPowerAmongItsController && obj.IsCreature
            && State.PermanentsControlledBy(objController).Where(c => c.IsCreature).Any(c => c.Power > power)) return false;
        if (filter.PowerGreaterThanEachOtherCreature
            && (!obj.IsCreature || State.Battlefield.Select(State.GetCard).Any(c => c.IsCreature && c.Id != obj.Id && c.Power >= power))) return false;
        // "With the greatest mana value among creatures [and planeswalkers] they control": among the permanents of the filter's types.
        if (filter.GreatestManaValueAmongItsController && (filter.Types == 0 ? CardType.Creature : filter.Types) is var among && (obj.Types & among) != 0
            && State.PermanentsControlledBy(objController).Where(c => (c.Types & among) != 0).Any(c => ManaValueOf(c) > ManaValueOf(obj))) return false;
        if (filter.DamagedThisTurn && !DamagedThisTurnBy(obj, lastKnown, (_, _) => true)) return false;
        if (filter.BlockingSource && (source is null || State.Combat?.FindAttack(source.Id) is not { } blocked || !blocked.Blockers.Contains(obj.Id))) return false;
        if (filter.DealtCombatDamageToYou && (source is null || !obj.CombatDamagedPlayers.Contains(source.Controller))) return false;
        if (filter.MaxManaValueTriggerAmount && ManaValueOf(obj) > _triggeredAmount) return false;
        if (filter.MaxManaValueLifeLostThisTurn && ManaValueOf(obj) > State.GetPlayer(sourceController).LifeLostThisTurn) return false;
        if (filter.MaxManaValueLifeGainedThisTurn && ManaValueOf(obj) > State.GetPlayer(sourceController).LifeGainedThisTurn) return false;
        if (filter.Goaded && !State.Goads.Any(g => g.Card == obj.Id && g.Version == obj.Version) && !State.GoadsForever.Any(g => g.Card == obj.Id && g.Version == obj.Version)) return false;
        if (filter.MaxPowerTriggered && (_triggeredSubject is not { } about || power > TriggeredPowerOf(State.GetCard(about), _triggeredSubjectVersion))) return false;
        if (filter.PowerIsX && _announcedX >= 0 && power != _announcedX) return false; // before X is announced, any power can still be X
        if (filter.DamagedThisTurnByYourSpider && !DamagedThisTurnBy(obj, lastKnown, (r, d) =>
                d.Version == r.SourceVersion && d.Zone == Zone.Battlefield ? d.HasSubtype("Spider") && d.Controller == sourceController
                : d.LastKnownOf(r.SourceVersion) is { } was && was.HasSubtype("Spider") && was.Controller == sourceController)) return false;
        if (filter.LesserPowerThanSource && (source is null || power >= SourcePowerOrLastKnown(source))) return false;
        if (filter.ToughnessLessThanSourcePower && (source is null || toughness >= SourcePowerOrLastKnown(source))) return false;
        if (filter.SharesColorWithYourLegendaryCreature && !State.PermanentsControlledBy(sourceController)
                .Where(c => c.IsCreature && (c.Supertypes & Supertype.Legendary) != 0).Any(c => c.Colors.Intersect(colors).Any())) return false;
        if (filter.NoSharedCreatureTypeWithYours && State.PermanentsControlledBy(sourceController).Where(c => c.IsCreature)
                .Any(c => (lk?.Subtypes ?? obj.CurrentSubtypes).Any(t => c.HasSubtype(t) && IsCreatureType(t)) || (obj.Has(Keyword.Changeling) && c.CurrentSubtypes.Any(IsCreatureType))
                          || (c.Has(Keyword.Changeling) && (lk?.Subtypes ?? obj.CurrentSubtypes).Any(IsCreatureType)))) return false;
        if (filter.GreaterPowerThanSource && (source is null || power <= SourcePowerOrLastKnown(source))) return false;
        if (filter.BlockedOrBlockedByLegendaryThisTurn && !State.BlocksThisTurn.Any(b =>
                (b.Blocker == obj.Id && b.BlockerVersion == obj.Version && WasLegendary(b.Attacker, b.AttackerVersion))
                || (b.Attacker == obj.Id && b.AttackerVersion == obj.Version && WasLegendary(b.Blocker, b.BlockerVersion)))) return false;
        if (filter.Historic && (types & CardType.Artifact) == 0 && (supertypes & Supertype.Legendary) == 0 && !HasSubtype("Saga")) return false;
        if (filter.HasToxic && obj.Definition.Toxic <= 0) return false;
        if (filter.FromBattlefieldThisTurn && obj.LeftBattlefieldTurn != State.TurnNumber) return false;
        if (filter.PaidWithTreasure && !obj.PaidWithTreasure) return false;
        if (filter.ChosenParity && (source?.ChosenParity is not { } parity || (ManaValueOf(obj) % 2 == 0 ? "even" : "odd") != parity)) return false;
        if (filter.SharesNameWithYourLegendary
            && !State.PermanentsControlledBy(sourceController).Any(c => (c.Supertypes & Supertype.Legendary) != 0 && c.Name == obj.Name)) return false;
        if (filter.Multicolored is { } multi && (colors.Count > 1) != multi) return false;
        if (filter.Monocolored && colors.Count != 1) return false;
        if (filter.Colorless is { } colorless && (colors.Count == 0) != colorless) return false;
        if (filter.InCombat is not null || filter.Attacking is not null || filter.Blocking is not null)
        {
            bool attacking = lk?.Attacking ?? State.Combat?.FindAttack(obj.Id) is not null;
            bool blocking = lk?.Blocking ?? State.Combat?.IsBlocking(obj.Id) == true;
            if (filter.InCombat is { } wantInCombat && (attacking || blocking) != wantInCombat) return false;
            if (filter.Attacking is { } wantAttacking && attacking != wantAttacking) return false;
            if (filter.Blocking is { } wantBlocking && blocking != wantBlocking) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ continuous effects (rule 611, 613)

    /// <summary>
    /// Recomputes characteristics changed by continuous effects: static abilities of permanents on the battlefield,
    /// then "until end of turn" effects. Only additive P/T changes (layer 7c) and keyword grants (layer 6) exist so
    /// far, so the order within a layer doesn't change the result.
    /// </summary>
    private void RecomputeContinuousEffects()
    {
        RecomputeEveryCreatureType();
        // "… and permanent spells you control are enchantments in addition to their other types."
        foreach (var spell in State.Stack.OfType<SpellOnStack>().Select(s => State.GetCard(s.Card)))
            spell.GrantedTypes = spell.Definition.Types.IsPermanent() && HasRule(spell.Controller, ExtraRules.YourNonlandPermanentsAndPermanentSpellsAreEnchantments)
                ? CardType.Enchantment : 0;
        // "All cards that aren't on the battlefield, spells, and permanents are the chosen color in addition to their other colors."
        var everythingColors = State.Battlefield.Select(State.GetCard).Where(c => c.HasTextAbilities && c.Definition.EverythingIsChosenColor && c.ChosenColor is not null)
            .Select(c => c.ChosenColor!).Distinct().ToList();
        if (everythingColors.Count > 0 || _everythingColored)
            foreach (var card in State.Cards.Values) card.AddedColors = everythingColors.Count > 0 ? everythingColors : null;
        _everythingColored = everythingColors.Count > 0;
        var battlefield = State.Battlefield.Select(State.GetCard).ToList();
        var sources = battlefield.Concat(State.Emblems.Select(State.GetCard)).ToList();
        // Cards whose static abilities work from their owner's graveyard ("as long as this card is in your graveyard").
        var graveyardSources = State.Players.Where(p => !p.HasLost).SelectMany(p => p.Graveyard).Select(State.GetCard)
            .Where(c => c.Definition.Abilities.OfType<StaticAbility>().Any(a => a.FromGraveyard)).ToList();
        // Timestamps (rule 613.7d–e): permanents that just entered (or became attached), then emblems, in order.
        foreach (var card in sources.Where(c => c.Timestamp == 0)) card.Timestamp = NewTimestamp();
        State.UntilEndOfTurn.RemoveAll(e => State.GetCard(e.Card).Version != e.Version);
        bool SourceGone(CardId id, int version) => State.GetCard(id) is var s && (s.Version != version || s.Zone != Zone.Battlefield);
        State.LastingEffects.RemoveAll(e => State.GetCard(e.Card) is var c && (c.Version != e.Version || c.Zone != Zone.Battlefield
                                                                              || (e.WhileCounter is { } kind && c.CounterCount(kind) == 0))
                                            || (e.WhileSource is { } ws && (SourceGone(ws.Card, ws.Version)
                                                || (e.WhileControlledBy is { } holder && State.GetCard(ws.Card).Controller != holder))));
        // Control effects end with their object; "for as long as you control …" ones once that stops, for good.
        State.ControlEffects.RemoveAll(e => SourceGone(e.Card, e.Version) || (e.WhileMonarch is { } monarchHolder && State.Monarch != monarchHolder)
                                            || (e.WhileCounter is { } held && State.GetCard(e.Card).CounterCount(held) == 0)
                                            || (e.WhileSource is { } src && (SourceGone(src.Card, src.Version) || (!e.WhileSourceRemainsOnly && State.GetCard(src.Card).Controller != e.NewController))));
        var controllersBefore = battlefield.ToDictionary(c => c.Id, c => c.Controller);
        State.DamagePreventions.RemoveAll(d => SourceGone(d.Card, d.Version) || SourceGone(d.Source, d.SourceVersion));
        // Effects from resolved spells and abilities, oldest first.
        var effects = State.LastingEffects.Concat(State.UntilEndOfTurn)
            .Where(e => State.GetCard(e.Card).Zone == Zone.Battlefield).OrderBy(e => e.Timestamp).ToList();

        foreach (var card in battlefield)
        {
            card.PowerBonus = 0;
            card.ToughnessBonus = 0;
            card.Controller = card.BaseController;
            card.GrantedSubtypes.Clear();
            // "Your Ring-bearer is legendary."
            card.GrantedSupertypes = State.GetPlayer(card.Controller).RingBearer is { } bearer && bearer.Card == card.Id && bearer.Version == card.Version
                ? Supertype.Legendary : 0;
            card.IsRingBearerNow = card.GrantedSupertypes != 0;
            if (card.CounterCount(CounterKind.Shadow) > 0) card.GrantedSubtypes.Add("Wraith"); // "as long as it has a shadow counter, it's a Wraith"
            card.TypesOverride = null;
            card.EffectEveryCreatureType = false;
            card.GrantedTypes = 0;
            card.SubtypesOverride = null;
            card.ColorsOverride = null;
            card.NameOverride = null;
            card.LosesTextAbilities = false;
            card.EffectCopy = null;
        }

        IEnumerable<Card> AffectedBy(Card source, StaticAbility ability) =>
            Affected(source, ability.Affects, battlefield).Where(a =>
                ability.Filter is not { } filter || Matches(filter with { Controller = ControllerFilter.Any }, a, a.Controller, source, source.Controller));

        // Static abilities that exist and apply now, oldest source first (an ability that was lost generates no effect).
        List<(Card Source, StaticAbility Ability)> Statics(Func<StaticAbility, bool> relevant) =>
            sources.OrderBy(c => c.Timestamp)
                .SelectMany(source => source.Abilities.OfType<StaticAbility>().Where(a => !a.FromGraveyard).Where(relevant)
                    .Where(a => a.While is not { } condition || Holds(condition, source.Controller, source))
                    .Select(a => (source, a)))
                .Concat(graveyardSources.SelectMany(source => source.Definition.Abilities.OfType<StaticAbility>().Where(a => a.FromGraveyard).Where(relevant)
                    .Where(a => a.While is not { } condition || Holds(condition, source.Owner, source))
                    .Select(a => (source, a))))
                .ToList();

        // Layer 1 (rule 613.1a): "Enchanted creature is a copy of the chosen creature" gives the copiable values the chosen creature has
        // (those it last had, once it has left the battlefield), so everything below works from the copy.
        foreach (var (source, ability) in Statics(a => a.CopiesChosenCreature))
        {
            var chosen = source.ChosenCreature is { } pick && State.GetCard(pick.Card) is { } found && found.Version == pick.Version && found.Zone == Zone.Battlefield ? found : null;
            if (chosen is not null) source.ChosenCreatureValues = chosen.Definition;
            if (source.ChosenCreatureValues is not { } values) continue;
            foreach (var affected in AffectedBy(source, ability).Where(a => a.Id != chosen?.Id).ToList())
                affected.EffectCopy = values with { IsToken = affected.PrintedDefinition.IsToken, Foil = affected.PrintedDefinition.Foil };
        }

        // "Enchanted land is the chosen type" (rule 305.7) removes the abilities from the land's rules text, so it applies before any
        // effect those abilities would generate (rule 613.8a(c) dependency): an Aura making a land an Island, on a land whose ability
        // makes every land a Swamp, means that "each land is a Swamp" doesn't exist. Lands losing their text this way generate no static abilities below.
        foreach (var (source, ability) in Statics(a => a.SetChosenLandType))
            if (source.ChosenType is not null && !source.LosesTextAbilities)
                foreach (var affected in AffectedBy(source, ability).Where(a => a.Is(CardType.Land)).ToList())
                    affected.LosesTextAbilities = true;

        // Which abilities exist decides which static abilities apply at all, so the ability layer is worked out first
        // with printed characteristics, then again once types and colors are settled (rule 613.8 dependency).
        ApplyAbilityLayer(battlefield, effects, Statics, AffectedBy);

        // "This creature has all activated abilities of lands your opponents control except mana abilities" (layer 6, worked out here
        // with control known).
        _borrowedLandAbilities.Clear();
        foreach (var thief in battlefield.Where(c => (c.Replaces & Replacements.AnyManaForItsAbilities) != 0))
            _borrowedLandAbilities[thief.Id] = State.OpponentsOf(thief.Controller).SelectMany(o => State.PermanentsControlledBy(o)).Where(c => c.Is(CardType.Land))
                .SelectMany(c => c.Abilities.OfType<ActivatedAbility>().Where(a => !IsManaAbilityOf(a))).ToList();

        // Layer 2: control-changing effects and static abilities ("You control enchanted permanent"), in timestamp order.
        var controlChanges = new List<(long Timestamp, Card Card, Func<PlayerId?> To)>();
        foreach (var (source, ability) in Statics(a => a.GivesControl || a.GivesControlToMonarch))
            foreach (var affected in AffectedBy(source, ability).ToList())
            {
                var giver = source;
                controlChanges.Add((source.Timestamp, affected, () => ability.GivesControlToMonarch ? State.Monarch : giver.Controller));
            }
        foreach (var control in State.ControlEffects)
            if (State.GetCard(control.Card) is { Zone: Zone.Battlefield } held)
                controlChanges.Add((control.Timestamp, held, () => control.NewController));
        foreach (var (_, card, to) in controlChanges.OrderBy(x => x.Timestamp))
            if (to() is { } newController && !State.GetPlayer(newController).HasLost) card.Controller = newController;
        // A permanent that changes control is a new arrival for its controller and leaves combat (rules 302.6, 506.4).
        foreach (var card in battlefield.Where(c => controllersBefore.TryGetValue(c.Id, out var was) && was != c.Controller))
        {
            card.ControlledSinceTurnStart = false;
            State.Combat?.Remove(card.Id);
            _controlChanged.Add((card.Id, card.Controller, controllersBefore[card.Id]));
        }

        // Layer 1 (names set by copy-like effects), layer 4 (types and subtypes) and layer 5 (colors), in timestamp order.
        var typeChanges = new List<(long Timestamp, Card Card, Action<Card, TypeState> Apply)>();
        foreach (var (source, ability) in Statics(a => a.SetName is not null || a.SetTypes is not null || a.SetSubtypes is not null || a.SetColors is not null
                                                        || a.AddTypes != 0 || a.AddSubtypes is not null || a.AddChosenType || a.SetChosenLandType || a.CreatureTypesOfLastExiled
                                                        || a.RemoveTypes != 0 || a.EveryCreatureType || a.SetChosenColor))
            foreach (var affected in AffectedBy(source, ability).ToList())
            {
                var chosen = ability.AddChosenType ? source.ChosenType : null;
                var landType = ability.SetChosenLandType ? source.ChosenType : null;
                typeChanges.Add((source.Timestamp, affected, (card, t) =>
                {
                    // "Enchanted land is the chosen type" (rule 305.7): its old land types go, its other subtypes stay.
                    if (landType is not null && (t.Types & CardType.Land) != 0)
                    {
                        t.Subtypes = t.Subtypes.Where(s => !Card.IsLandType(s)).Append(landType).ToList();
                        t.LandTypeSet = true;
                    }
                    if (ability.SetName is { } name) t.Name = name;
                    if (ability.EveryCreatureType) t.EveryCreatureType = true;
                    if (ability.SetChosenColor && source.ChosenColor is { } chosenColor) t.Colors = new[] { chosenColor };
                    if (ability.SetTypes is { } types) t.Types = types;
                    if (ability.SetSubtypes is { } subtypes) t.Subtypes = Card.ReplaceSubtypes(t.Subtypes, subtypes);
                    if (ability.SetColors is { } colors) t.Colors = colors;
                    t.Types |= ability.AddTypes;
                    t.Types &= ~ability.RemoveTypes;
                    if (ability.AddSubtypes is { } add) t.Subtypes.AddRange(add);
                    if (chosen is not null) t.Subtypes.Add(chosen);
                    // "Has the creature types of the last creature card exiled with it. It's still a Shapeshifter."
                    if (ability.CreatureTypesOfLastExiled && LastCreatureExiledWith(source) is { } imprinted)
                        t.Subtypes = Card.ReplaceSubtypes(t.Subtypes, imprinted.Definition.Subtypes.Where(Card.IsCreatureType).Append("Shapeshifter").Distinct().ToList());
                }));
            }
        foreach (var effect in effects.Where(e => e.AddTypes != 0 || e.AddSubtypes is not null || e.SetSubtypes is not null || e.SetTypes is not null
                                                  || e.SetColors is not null || e.AddColors is not null || e.LosesLandTypes || e.AddsEveryCreatureType))
            typeChanges.Add((effect.Timestamp, State.GetCard(effect.Card), (card, t) =>
            {
                if (effect.LosesLandTypes) t.Subtypes = t.Subtypes.Where(s => !Card.IsLandType(s)).ToList();
                if (effect.SetTypes is { } types) t.Types = types;
                if (effect.SetSubtypes is { } subtypes) { t.Subtypes = Card.ReplaceSubtypes(t.Subtypes, subtypes); t.EveryCreatureType = false; }
                t.Types |= effect.AddTypes;
                if (effect.AddSubtypes is { } add) t.Subtypes.AddRange(add);
                if (effect.AddsEveryCreatureType) t.EveryCreatureType = true;
                if (effect.SetColors is { } colors) t.Colors = colors;
                if (effect.AddColors is { } addColors) t.Colors = ColorOrder.Where(c => t.Colors.Contains(c) || addColors.Contains(c)).ToList();
            }));
        // Impending (702.176a): "as long as this permanent's impending cost was paid and it has a time counter on it, it's not a creature".
        foreach (var impended in battlefield.Where(c => c.Impended && c.CounterCount(CounterKind.Time) > 0 && c.HasTextAbilities))
            typeChanges.Add((impended.Timestamp, impended, (_, t) => t.Types &= ~CardType.Creature));
        foreach (var group in typeChanges.GroupBy(x => x.Card))
        {
            var card = group.Key;
            var t = new TypeState(card.Definition.Types, card.Definition.Subtypes.ToList(), card.Definition.ColorList, null);
            foreach (var change in group.OrderBy(x => x.Timestamp)) change.Apply(card, t);
            card.TypesOverride = t.Types;
            card.SubtypesOverride = t.Subtypes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!ReferenceEquals(t.Colors, card.Definition.ColorList)) card.ColorsOverride = t.Colors;
            card.NameOverride = t.Name;
            card.LosesTextAbilities = t.LandTypeSet;
            card.EffectEveryCreatureType = t.EveryCreatureType;
        }

        // Layer 6 again with the final types (filters such as "artifact creatures" depend on them).
        ApplyAbilityLayer(battlefield, effects, Statics, AffectedBy);
        foreach (var (thief, stolen) in _borrowedLandAbilities)
            if (State.GetCard(thief) is { Zone: Zone.Battlefield, LosesAbilities: false } holder) holder.GrantedAbilities.AddRange(stolen);

        foreach (var card in battlefield.Where(c => c.Definition.ExtraManaOptions.Any(o => o.ColorsAmongLegendaryCreatureCardsInGraveyard)))
            card.ColorsAmongGraveyardLegends = State.GetPlayer(card.Controller).Graveyard.Select(State.GetCard)
                .Where(c => c.Definition.IsCreature() && (c.Definition.Supertypes & Supertype.Legendary) != 0).SelectMany(c => c.Definition.Colors).Distinct()
                .Select(c => Mana.ManaTypeExtensions.TryParse(c[0], out var t) ? t : (ManaType?)null).OfType<ManaType>().OrderBy(t => t).ToList();
        // "For each color among permanents you control" mana abilities follow the colors settled above.
        foreach (var card in battlefield.Where(c => c.Definition.ExtraManaOptions.Any(o => o.ColorsAmongYourPermanents)))
        {
            var among = card.Definition.ExtraManaOptions.First(o => o.ColorsAmongYourPermanents).ColorsAmongMatching;
            card.ColorsAmongYourPermanents = battlefield.Where(p => p.Controller == card.Controller && (among is null || Matches(among, p, card.Controller, card, card.Controller)))
                .SelectMany(p => p.Colors).Distinct()
                .Select(c => Mana.ManaTypeExtensions.TryParse(c[0], out var t) ? t : (ManaType?)null).OfType<ManaType>().OrderBy(t => t).ToList();
        }

        RecomputeManaTypes(battlefield);

        // Layer 7a: characteristic-defining abilities; 7b: effects that set base power/toughness, in timestamp order.
        // A characteristic-defining ability works in every zone (rule 604.3): a card in a hand, library, graveyard or exile, or a
        // spell, has the power and toughness it defines (counted for its owner, or for the spell's controller).
        foreach (var card in battlefield.Concat(State.Cards.Values.Where(c => c.Zone != Zone.Battlefield
                     && (c.Definition.PowerFrom is not null || c.Definition.ToughnessFrom is not null))))
        {
            var cda = new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            card.BasePowerOverride = card.Definition.PowerFrom is { } pf ? Eval(pf, cda) : null;
            card.BaseToughnessOverride = card.Definition.ToughnessFrom is { } tf ? Eval(tf, cda) : null;
        }
        var setters = new List<(long Timestamp, Card Card, int? Power, int? Toughness)>();
        foreach (var (source, ability) in Statics(a => a.SetPower is not null || a.SetToughness is not null || a.SetPowerFrom is not null || a.SetToughnessFrom is not null))
            foreach (var affected in AffectedBy(source, ability).ToList())
            {
                // Worked out for each affected object on its own ("base power and toughness each equal to its mana value").
                var own = new EffectContext(source.Controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>()) { AffectedCard = affected };
                setters.Add((source.Timestamp, affected, ability.SetPower ?? (ability.SetPowerFrom is { } spf ? Eval(spf, own) : null),
                    ability.SetToughness ?? (ability.SetToughnessFrom is { } stf ? Eval(stf, own) : null)));
            }
        foreach (var effect in effects.Where(e => e.SetPower is not null || e.SetToughness is not null || e.SetPowerFrom is not null || e.SetToughnessFrom is not null))
        {
            var card = State.GetCard(effect.Card);
            var own = new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            setters.Add((effect.Timestamp, card, effect.SetPower ?? (effect.SetPowerFrom is { } pf ? Eval(pf, own) : null),
                effect.SetToughness ?? (effect.SetToughnessFrom is { } tf ? Eval(tf, own) : null)));
        }
        foreach (var (_, card, power, toughness) in setters.OrderBy(x => x.Timestamp))
        {
            if (power is { } p) card.BasePowerOverride = p;
            if (toughness is { } t) card.BaseToughnessOverride = t;
        }

        // Layer 7c: modifications (their order doesn't matter); counters (7d) are counted in Card.Power.
        foreach (var (source, ability) in Statics(a => a.Power != 0 || a.Toughness != 0 || a.PowerBonus is not null || a.ToughnessBonus is not null))
        {
            var ctx = new EffectContext(source.Controller, source, Array.Empty<ChosenTarget>(), Array.Empty<bool>());
            if (DependsOnAffected(ability.PowerBonus) || DependsOnAffected(ability.ToughnessBonus))
            {
                // Worked out for each affected object on its own ("for each other creature that shares a creature type with it").
                foreach (var affected in AffectedBy(source, ability).ToList())
                {
                    var own = ctx with { AffectedCard = affected };
                    affected.PowerBonus += ability.Power + (ability.PowerBonus is { } apb ? Eval(apb, own) : 0);
                    affected.ToughnessBonus += ability.Toughness + (ability.ToughnessBonus is { } atb ? Eval(atb, own) : 0);
                }
                continue;
            }
            int power = ability.Power + (ability.PowerBonus is { } pb ? Eval(pb, ctx) : 0);
            int toughness = ability.Toughness + (ability.ToughnessBonus is { } tb ? Eval(tb, ctx) : 0);
            if (power == 0 && toughness == 0) continue;
            foreach (var affected in AffectedBy(source, ability).ToList())
            {
                affected.PowerBonus += power;
                affected.ToughnessBonus += toughness;
            }
        }
        foreach (var effect in effects)
        {
            var card = State.GetCard(effect.Card);
            card.PowerBonus += effect.Power;
            card.ToughnessBonus += effect.Toughness;
        }
        // Each hone counter on an Equipment grants +1/+0 to the creature it's attached to.
        foreach (var equipment in battlefield.Where(c => c.CounterCount(CounterKind.Hone) > 0 && c.AttachedTo is not null))
            if (State.GetCard(equipment.AttachedTo!.Value) is { Zone: Zone.Battlefield } equipped) equipped.PowerBonus += equipment.CounterCount(CounterKind.Hone);
        // Mana amounts worked out from characteristics ("X mana, where X is this creature's power") use the final values.
        foreach (var card in battlefield)
            card.ManaAmount = card.Definition.ManaAmountFrom is { } ma
                ? Eval(ma, new EffectContext(card.Controller, card, Array.Empty<ChosenTarget>(), Array.Empty<bool>()))
                : card.Definition.ManaAmount;

        // Storied: a player controlling a permanent with it and three or more artifacts, legendaries and/or Sagas has an
        // enduring story for the rest of the game; abilities that depend on it apply at once.
        bool gained = false;
        foreach (var player in State.LivingPlayers.Where(p => !p.HasEnduringStory))
        {
            var mine = battlefield.Where(c => c.Controller == player.Id).ToList();
            if (!mine.Any(c => c.Has(Keyword.Storied))) continue;
            if (mine.Count(c => c.Is(CardType.Artifact) || (c.Supertypes & Supertype.Legendary) != 0 || c.HasSubtype("Saga")) < 3) continue;
            player.HasEnduringStory = gained = true;
            Emit(new EnduringStoryGained(player.Id));
        }
        foreach (var player in State.LivingPlayers.Where(p => !p.HasCitysBlessing))
        {
            var mine = battlefield.Where(c => c.Controller == player.Id).ToList();
            if (!mine.Any(c => c.Has(Keyword.Ascend)) || mine.Count < 10) continue;
            player.HasCitysBlessing = gained = true;
            Emit(new CitysBlessingGained(player.Id));
        }
        if (gained) RecomputeContinuousEffects();
        // Control changes are announced once the layers are settled (the events may recompute again).
        if (_announcingControl || _controlChanged.Count == 0) return;
        _announcingControl = true;
        try
        {
            while (_controlChanged.Count > 0)
            {
                var (id, to, from) = _controlChanged[0];
                _controlChanged.RemoveAt(0);
                Emit(new ControlChanged(id, to) { Previous = from });
            }
        }
        finally { _announcingControl = false; }
    }

    private readonly List<(CardId Card, PlayerId To, PlayerId From)> _controlChanged = new();
    private bool _announcingControl;

    /// <summary>
    /// Mana abilities whose types or availability depend on the game: the commander's color identity, what lands could
    /// produce (worked out until nothing changes, as such lands may look at each other), and conditional abilities.
    /// </summary>
    private void RecomputeManaTypes(List<Card> battlefield)
    {
        // "Whenever you tap a permanent for {C}, add an additional {C}": the extra mana of abilities that add only {C}.
        foreach (var card in battlefield)
            card.ManaMultiplier = (int)Math.Pow(3, battlefield.Count(c => c.Controller == card.Controller && c.HasTextAbilities && c.Definition.TripleManaFromPermanents));
        foreach (var card in battlefield)
            card.ExtraColorless = battlefield.Count(c => c.Controller == card.Controller && ((c.Replaces & Replacements.ExtraColorlessWhenTappedForColorless) != 0
                || (card.Is(CardType.Land) && (c.Replaces & Replacements.ExtraColorlessWhenLandTappedForColorless) != 0)));
        foreach (var card in battlefield)
            card.ExtraRed = card.Is(CardType.Land) && card.HasSubtype("Mountain") ? State.GetPlayer(card.Controller).ExtraRedFromMountainsThisTurn : 0;
        foreach (var card in battlefield)
            card.ExtraGreen = !card.Is(CardType.Land) ? 0
                : battlefield.Count(a => a.AttachedTo == card.Id && a.HasTextAbilities && a.Definition.EnchantedLandExtraGreen)
                  + (card.HasSubtype("Forest") ? battlefield.Count(c => c.Controller == card.Controller && c.HasTextAbilities && c.Definition.YourForestsExtraGreen) : 0);
        foreach (var card in battlefield)
            card.ExtraBlack = card.Is(CardType.Land) && card.HasSubtype("Swamp")
                ? battlefield.Count(c => c.Controller == card.Controller && c.HasTextAbilities && c.Definition.YourSwampsExtraBlack) : 0;
        int tappedWatchers = battlefield.Count(c => c.Tapped && c.HasTextAbilities && (c.Definition.ExtraRules & ExtraRules.LandsTappedAddOneAndDamageWhileTapped) != 0);
        foreach (var card in battlefield)
            card.ExtraWhileWatchersTapped = card.Is(CardType.Land) ? tappedWatchers : 0;
        foreach (var card in battlefield.Where(c => c.Definition.ExtraManaOptions.Count > 0))
        {
            card.InactiveManaOptions.Clear();
            var options = card.Definition.ExtraManaOptions;
            for (int i = 0; i < options.Count; i++)
                if (options[i].While is { } condition && !Holds(condition, card.Controller, card)) card.InactiveManaOptions.Add(i);
            if (options.Any(o => o.CommanderIdentity)) card.CommanderIdentityTypes = CommanderIdentityTypes(card.Controller);
        }
        var watchers = battlefield.Where(c => c.Definition.ExtraManaOptions.Any(o => o.ColorsOpponentsLandsCouldProduce || o.TypesYourLandsCouldProduce)).ToList();
        if (watchers.Count == 0) return;
        foreach (var card in watchers)
        {
            card.OpponentsLandColors = Array.Empty<ManaType>();
            card.YourLandTypes = Array.Empty<ManaType>();
        }
        var lands = battlefield.Where(c => c.Is(CardType.Land)).ToList();
        for (int pass = 0; pass < 4; pass++)
        {
            var byPlayer = State.Players.ToDictionary(p => p.Id, p => lands.Where(l => l.Controller == p.Id)
                .SelectMany(l => l.ManaOptions.Where(o => o.Produces > 0).SelectMany(o => o.Types)).ToHashSet());
            bool changed = false;
            foreach (var card in watchers)
            {
                var opponents = State.OpponentsOf(card.Controller).SelectMany(o => byPlayer[o]).Where(t => t != ManaType.Colorless).Distinct().OrderBy(t => t).ToList();
                var yours = byPlayer[card.Controller].OrderBy(t => t).ToList();
                if (!opponents.SequenceEqual(card.OpponentsLandColors) || !yours.SequenceEqual(card.YourLandTypes)) changed = true;
                card.OpponentsLandColors = opponents;
                card.YourLandTypes = yours;
            }
            if (!changed) break;
        }
    }

    /// <summary>The colors in their usual order (W, U, B, R, G).</summary>
    private static readonly string[] ColorOrder = { "W", "U", "B", "R", "G" };

    /// <summary>Characteristics worked out in layers 1, 4 and 5.</summary>
    private sealed class TypeState(CardType types, List<string> subtypes, IReadOnlyList<string> colors, string? name)
    {
        public CardType Types { get; set; } = types;
        public List<string> Subtypes { get; set; } = subtypes;
        public IReadOnlyList<string> Colors { get; set; } = colors;
        public string? Name { get; set; } = name;
        public bool EveryCreatureType { get; set; }

        /// <summary>Its land subtype was set (rule 305.7).</summary>
        public bool LandTypeSet { get; set; }
    }

    /// <summary>
    /// Layer 6 in timestamp order (rule 613.7): granting abilities adds to what the object has; losing all abilities
    /// removes its printed abilities and everything granted by earlier effects, but not by later ones.
    /// </summary>
    private void ApplyAbilityLayer(List<Card> battlefield, List<UntilEndOfTurnEffect> effects,
        Func<Func<StaticAbility, bool>, List<(Card Source, StaticAbility Ability)>> statics, Func<Card, StaticAbility, IEnumerable<Card>> affectedBy)
    {
        // Abilities existing before this pass decide which statics apply; repeat until that is stable.
        for (int pass = 0; pass < 4; pass++)
        {
            var before = battlefield.ToDictionary(c => c.Id, c => (c.LosesAbilities, c.GrantedAbilities.Count, c.GrantedKeywords.Count));
            var changes = new List<(long Timestamp, Card Card, Action<Card> Apply)>();
            foreach (var (source, ability) in statics(a => a.LosesAllAbilities || a.Keywords is { Count: > 0 } || a.GrantedKeywords.Count > 0
                                                           || a.GrantsAbilities is not null || a.GrantsMana is not null || a.GrantsWard is not null || a.GrantsWardLife > 0
                                                           || a.GrantsGraveyardAbilities is not null || a.GrantsYourCreaturesAbilities is not null || a.GrantsExiledCreatureAbilities || a.GrantsExiledCardAbilities is not null || a.CantAttackYou || a.CantAttackYouOrPlaneswalkers || a.Goads || a.ProtectionFromRingBearers || a.ProtectionFromTypes != 0 || a.ProtectionFromChosenColor
                                                           || a.LosesKeywords is not null || a.PreventsDamage != StaticDamagePrevention.None || a.CantActivateAbilities || a.CantBeSacrificed
                                                           || a.GrantsCantBeBlockedBy is not null || a.GrantsDyingTriggerBonus))
                foreach (var affected in affectedBy(source, ability).ToList())
                    changes.Add((source.Timestamp, affected, card =>
                    {
                        if (ability.LosesAllAbilities) LoseAllAbilities(card);
                        card.GrantedKeywords.UnionWith(ability.GrantedKeywords);
                        card.LostKeywords.ExceptWith(ability.GrantedKeywords); // a later grant gives back a keyword lost earlier (timestamps, rule 613.7)
                        if (ability.LosesKeywords is { } loses) { card.LostKeywords.UnionWith(loses); card.GrantedKeywords.ExceptWith(loses); }
                        card.StaticDamagePrevention |= ability.PreventsDamage;
                        if (ability.CantActivateAbilities) card.AbilitiesCantBeActivated = true;
                        if (ability.CantBeSacrificed) card.StaticCantBeSacrificed = true;
                        if (ability.GrantsAbilities is { } granted) card.GrantedAbilities.AddRange(granted.Select(g => g with { GrantedBy = source.Id }));
                        if (ability.GrantsMana is { } mana) card.GrantedManaOptions.Add(new ManaOption(mana, ability.GrantsManaAmount, ability.GrantsManaOnlyFor));
                        if (ability.GrantsDyingTriggerBonus) card.DyingTriggerBonus++;
                        if (ability.GrantsWard is { } ward) card.GrantedWards.Add(ward);
                        if (ability.GrantsWardLife > 0) card.GrantedWardLives.Add(ability.GrantsWardLife);
                        if (ability.GrantsCantBeBlockedBy is { } unblockable) card.BlockRestrictions.Add(unblockable);
                        if (ability.CantAttackYou) card.CantAttackPlayers.Add(source.Controller);
                        if (ability.CantAttackYouOrPlaneswalkers) card.CantAttackPlayersAndPlaneswalkers.Add(source.Controller);
                        if (ability.Goads) card.StaticGoaders.Add(source.Controller);
                        if (ability.ProtectionFromRingBearers) card.ProtectedFromRingBearers = true;
                        card.ProtectionFromTypes |= ability.ProtectionFromTypes;
                        if (ability.ProtectionFromChosenColor && source.ChosenColor is { } chosenColor) card.AuraColorProtections.Add((chosenColor, source.Id));
                        // "Has all activated abilities of all [filter] cards in your graveyard."
                        if (ability.GrantsGraveyardAbilities is { } fromGraveyard)
                            card.GrantedAbilities.AddRange(State.GetPlayer(source.Controller).Graveyard.Select(State.GetCard)
                                .Where(c => Matches(fromGraveyard with { Controller = ControllerFilter.Any }, c, c.Owner, source, source.Controller))
                                .SelectMany(c => c.Definition.Abilities.OfType<ActivatedAbility>().Where(a => !a.Cost.FromGraveyard && !a.Cost.FromHand))
                                .Select(a => a with { GrantedBy = source.Id }));
                        // "Has all activated abilities of creatures you control that don't have the same name as this creature" (as printed, mana abilities too).
                        if (ability.GrantsYourCreaturesAbilities is { } fromYours)
                        {
                            var others = State.PermanentsControlledBy(source.Controller).Where(c => c.Id != card.Id && c.IsCreature && !c.TurnedFaceDown && !HasName(c, card.Name)
                                && !c.LosesAbilities && Matches(fromYours with { Controller = ControllerFilter.Any }, c, c.Controller, source, source.Controller)).ToList();
                            card.GrantedAbilities.AddRange(others.SelectMany(c => c.Definition.Abilities.OfType<ActivatedAbility>().Where(a => !a.Cost.FromGraveyard && !a.Cost.FromHand))
                                .Select(a => a with { GrantedBy = source.Id }));
                            card.GrantedManaOptions.AddRange(others.SelectMany(c => c.PrintedManaOptions()));
                        }
                        if (ability.GrantsExiledCreatureAbilities)
                            card.GrantedAbilities.AddRange(State.Cards.Values
                                .Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == source.Id && w.Version == source.Version && c.Definition.IsCreature())
                                .SelectMany(c => c.Definition.Abilities.OfType<ActivatedAbility>().Where(a => !a.Cost.FromGraveyard && !a.Cost.FromHand))
                                .Select(a => a with { GrantedBy = source.Id }));
                        if (ability.GrantsExiledCardAbilities is { } exiledFilter)
                        {
                            var linked = State.Cards.Values.Where(c => c.Zone == Zone.Exile && c.ExiledWith is { } w && w.Source == source.Id && w.Version == source.Version
                                                                       && Matches(exiledFilter with { Controller = ControllerFilter.Any }, c, c.Owner, source, source.Controller)).ToList();
                            card.GrantedAbilities.AddRange(linked.SelectMany(c => c.Definition.Abilities.OfType<ActivatedAbility>().Where(a => !a.Cost.FromGraveyard && !a.Cost.FromHand))
                                .Select(a => a with { GrantedBy = source.Id }));
                            card.GrantedManaOptions.AddRange(linked.SelectMany(c => c.PrintedManaOptions()));
                        }
                    }));
            foreach (var effect in effects.Where(e => e.LosesAbilities || e.Keywords.Count > 0 || e.Abilities is not null || e.LoseKeywords is not null || e.CantAttackOwner || e.CantAttackPlayerAndPlaneswalkers is not null
                                                      || e.GrantsMana is not null))
                changes.Add((effect.Timestamp, State.GetCard(effect.Card), card =>
                {
                    if (effect.LosesAbilities) LoseAllAbilities(card);
                    if (effect.GrantsMana is { } mana) card.GrantedManaOptions.Add(new ManaOption(mana, effect.GrantsManaAmount));
                    card.GrantedKeywords.UnionWith(effect.Keywords);
                    card.LostKeywords.ExceptWith(effect.Keywords);
                    if (effect.LoseKeywords is { } lost) { card.LostKeywords.UnionWith(lost); card.GrantedKeywords.ExceptWith(lost); }
                    if (effect.Abilities is { } abilities) card.GrantedAbilities.AddRange(abilities);
                    if (effect.CantAttackOwner) card.CantAttackOwner = true;
                    if (effect.CantAttackPlayerAndPlaneswalkers is { } barred) card.CantAttackPlayersAndPlaneswalkers.Add(barred);
                }));

            foreach (var card in battlefield)
            {
                card.LosesAbilities = false;
                card.GrantedKeywords.Clear();
                card.GrantedAbilities.Clear();
                card.GrantedManaOptions.Clear();
                card.DyingTriggerBonus = 0;
                card.GrantedWards.Clear();
                card.GrantedWardLives.Clear();
                card.LostKeywords.Clear();
                card.UnblockableBy.Clear();
                card.BlockRestrictions.Clear();
                card.CantAttackPlayers.Clear();
                card.CantAttackOwner = false;
                card.CantAttackPlayersAndPlaneswalkers.Clear();
                card.StaticGoaders.Clear();
                card.ProtectedFromRingBearers = false;
                card.ProtectedFromPlayers.Clear();
                card.ProtectionFromTypes = 0;
                card.AuraColorProtections.Clear();
                card.StaticDamagePrevention = StaticDamagePrevention.None;
                card.AbilitiesCantBeActivated = false;
                card.StaticCantBeSacrificed = false;
            }
            foreach (var effect in effects.Where(e => e.UnblockableBy is not null)) State.GetCard(effect.Card).UnblockableBy.Add(effect.UnblockableBy!.Value);
            foreach (var effect in effects.Where(e => e.CantBeBlockedBy is not null)) State.GetCard(effect.Card).BlockRestrictions.Add(effect.CantBeBlockedBy!);
            foreach (var effect in effects.Where(e => e.ProtectionFromTypes != 0)) State.GetCard(effect.Card).ProtectionFromTypes |= effect.ProtectionFromTypes;
            // "You and creatures you control have protection from the chosen card type."
            foreach (var player in State.Players.Select(p => p.Id))
                if (PlayerProtectionFromTypes(player) is var shielded and not CardType.None)
                    foreach (var creature in State.PermanentsControlledBy(player).Where(c => c.IsCreature)) creature.ProtectionFromTypes |= shielded;
            foreach (var effect in effects.Where(e => e.ProtectionFromPlayers is not null)) State.GetCard(effect.Card).ProtectedFromPlayers.UnionWith(effect.ProtectionFromPlayers!);
            // Keyword counters give their keyword (rule 122.1b).
            foreach (var card in battlefield)
            {
                foreach (var (counter, keyword) in KeywordCounters)
                    if (card.CounterCount(counter) > 0) card.GrantedKeywords.Add(keyword);
            }
            foreach (var change in changes.OrderBy(x => x.Timestamp)) change.Apply(change.Card);
            if (battlefield.All(c => before[c.Id] == (c.LosesAbilities, c.GrantedAbilities.Count, c.GrantedKeywords.Count))) break;
        }
    }

    private static void LoseAllAbilities(Card card)
    {
        card.LosesAbilities = true;
        card.DyingTriggerBonus = 0;
        card.CantAttackOwner = false;
        card.GrantedWards.Clear();
        card.GrantedWardLives.Clear();
        card.GrantedKeywords.Clear();
        card.GrantedAbilities.Clear();
        card.GrantedManaOptions.Clear();
    }

    private IEnumerable<Card> Affected(Card source, AffectedFilter filter, IReadOnlyList<Card> battlefield)
    {
        IEnumerable<Card> candidates = filter.Scope switch
        {
            AffectedScope.Self => new[] { source },
            AffectedScope.YourCreatures => battlefield.Where(c => c.IsCreature && c.Controller == source.Controller),
            AffectedScope.OpponentsCreatures => battlefield.Where(c => c.IsCreature && c.Controller != source.Controller),
            AffectedScope.AllCreatures => battlefield.Where(c => c.IsCreature),
            AffectedScope.Enchanted or AffectedScope.Equipped when source.AttachedTo is { } host
                => battlefield.Where(c => c.Id == host),
            AffectedScope.YourPermanents => battlefield.Where(c => c.Controller == source.Controller),
            AffectedScope.AllPermanents => battlefield,
            AffectedScope.OpponentsPermanents => battlefield.Where(c => State.OpponentsOf(source.Controller).Contains(c.Controller)),
            _ => Array.Empty<Card>(),
        };
        if (filter.Other) candidates = candidates.Where(c => c.Id != source.Id);
        if (filter.Subtype is { } subtype) candidates = candidates.Where(c => c.HasSubtype(subtype));
        return candidates;
    }
}
