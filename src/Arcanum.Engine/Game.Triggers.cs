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
    private sealed record PendingTrigger(CardId Source, TriggeredAbility Ability, PlayerId Controller, TriggerInfo? Info = null)
    {
        /// <summary>The source's object version when it triggered.</summary>
        public int SourceVersion { get; init; } = -1;

        /// <summary>For a delayed ability: how many times its source had transformed when it was created (rule 701.27f).</summary>
        public int? SourceTransforms { get; init; }
    }

    private readonly List<PendingTrigger> _pendingTriggers = new();

    // ------------------------------------------------------------------ triggers (rule 603)

    /// <summary>Notes triggered abilities that trigger on <paramref name="e"/>; they go on the stack before the next priority.</summary>
    private void CollectTriggers(GameEvent e)
    {
        TriggerInfo About(Card card, PlayerId? player = null, int amount = 0) => new(card.Id, card.Version, player, amount);
        // "Whenever an opponent is dealt noncombat damage" (any source).
        if (e is DamageDealt { IsCombat: false, TargetPlayer: { } burnedOpponent, Amount: > 0 } noncombat)
            foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => State.OpponentsOf(c.Controller).Contains(burnedOpponent)).ToList())
                Queue(card.Id, TriggerEvent.OpponentDealtNoncombatDamage, card.Controller, new TriggerInfo(noncombat.Source, State.GetCard(noncombat.Source).Version, burnedOpponent, noncombat.Amount));
        // "Whenever a creature card leaves your graveyard" (wherever it goes).
        if (e is CardMoved { From: Zone.Graveyard } left) AsInGraveyard(State.GetCard(left.Card), card => QueueObservers(TriggerEvent.LeavesGraveyard, card, left.Owner));
        if (e is CardMoved { From: Zone.Library or Zone.Graveyard, To: Zone.Exile } exiledFromZone) QueueObservers(TriggerEvent.ExiledFromLibraryOrGraveyard, State.GetCard(exiledFromZone.Card), exiledFromZone.Owner);
        if (e is CardMoved { From: Zone.Battlefield, To: not Zone.Graveyard } leaving)
        {
            Queue(leaving.Card, TriggerEvent.LeavesBattlefield, leaving.LastController);
            Queue(leaving.Card, TriggerEvent.LeavesWithoutDying, leaving.LastController);
        }
        // "When this dies or is put into exile from the battlefield."
        if (e is CardMoved { From: Zone.Battlefield, To: Zone.Exile } exiled)
        {
            Queue(exiled.Card, TriggerEvent.DiesOrExiled, exiled.LastController);
            if (State.GetCard(exiled.Card).LastKnownInfo is { } exiledLki && (exiledLki.Types & CardType.Creature) != 0)
                QueueObservers(TriggerEvent.CreatureDiesOrExiled, State.GetCard(exiled.Card), exiled.LastController);
        }
        // "Whenever a creature you control leaves the battlefield" (judged as it last existed there).
        if (e is CardMoved { From: Zone.Battlefield } departed && WasCreature(State.GetCard(departed.Card)))
            foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == departed.LastController && o.Card.Id != departed.Card))
                foreach (var watching in abilities.Where(x => x.Trigger == TriggerEvent.CreatureLeaves))
                    if (Matches((watching.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, State.GetCard(departed.Card), departed.LastController, observer, observer.Controller, lastKnown: true))
                        AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(departed.Card, State.GetCard(departed.Card).Version, departed.LastController));
        // "Whenever another permanent you control leaves the battlefield" (judged as it last existed there).
        if (e is CardMoved { From: Zone.Battlefield } leftField) QueueObservers(TriggerEvent.PermanentLeaves, State.GetCard(leftField.Card), leftField.LastController);
        // "Whenever this creature deals damage to a Goblin or Orc."
        if (e is DamageDealt { TargetCard: { } hurtId } damageToCreature && State.GetCard(damageToCreature.Source) is { } damager)
            foreach (var ability in TriggerAbilitiesOf(damager).Where(a => a.Trigger == TriggerEvent.DealsDamageToCreature))
                if (Matches(ability.Filter ?? ObjectFilter.Anything, State.GetCard(hurtId), State.GetCard(hurtId).Controller, damager, damager.Controller, lastKnown: true))
                    AddPending(damager.Id, ability, damager.Controller, About(State.GetCard(hurtId), State.GetCard(hurtId).Controller, damageToCreature.Amount));
        // "Whenever this creature deals damage to an opponent" (combat or noncombat damage).
        if (e is DamageDealt { TargetPlayer: { } victim } toPlayer && State.GetCard(toPlayer.Source) is { } dealer
            && State.OpponentsOf(SourceController(dealer)).Contains(victim))
            Queue(dealer.Id, TriggerEvent.DealsDamageToOpponent, SourceController(dealer), new TriggerInfo(dealer.Id, dealer.Version, victim, toPlayer.Amount));
        // "Whenever you create a token" (once for each token).
        if (e is TokenCreated created)
            foreach (var card in State.PermanentsControlledBy(created.Controller).ToList())
                Queue(card.Id, TriggerEvent.TokenCreated, created.Controller, About(State.GetCard(created.Card), created.Controller));
        // "Whenever you create one or more creature tokens": once for the tokens one effect created, if any of them matches.
        if (e is TokensCreated batchCreated)
            foreach (var card in State.PermanentsControlledBy(batchCreated.Controller).ToList())
                foreach (var ability in TriggerAbilitiesOf(card).Where(a => a.Trigger == TriggerEvent.TokensCreated))
                    if (batchCreated.Cards.Any(t => Matches(ability.Filter ?? ObjectFilter.Anything, State.GetCard(t), State.GetCard(t).Controller, card, card.Controller, lastKnown: true)))
                        AddPending(card.Id, ability, card.Controller, new TriggerInfo(Player: batchCreated.Controller, Amount: batchCreated.Cards.Count));
        // "When this Class becomes level N": the abilities of that level section, which exist from now on.
        if (e is ClassLevelChanged levelled)
        {
            var leveller = State.GetCard(levelled.Card);
            foreach (var ability in TriggerAbilitiesOf(leveller).Where(a => a.Trigger == TriggerEvent.BecomesClassLevel && a.ClassLevel == levelled.Level))
                AddPending(leveller.Id, ability, leveller.Controller, About(leveller, leveller.Controller));
        }
        // "When you unlock this door": that door's own abilities; "whenever you fully unlock a Room": every permanent of that player's.
        if (e is DoorUnlocked unlocked && State.GetCard(unlocked.Card) is { Zone: Zone.Battlefield } room && room.PrintedDefinition.SplitHalves is { } doors)
        {
            foreach (var ability in TriggerAbilitiesOf(room).Where(a => a.Trigger == TriggerEvent.YouUnlockThisDoor && doors[unlocked.Door].Abilities.Contains(a)))
                AddPending(room.Id, ability, unlocked.Player, About(room, unlocked.Player));
            if (unlocked.Fully)
                foreach (var card in PermanentsAndEmblemsOf(unlocked.Player).ToList())
                    Queue(card.Id, TriggerEvent.YouFullyUnlockRoom, unlocked.Player, About(room, unlocked.Player));
        }
        // "When this is put into a graveyard from anywhere."
        if (e is CardMoved { To: Zone.Graveyard } toYard && State.GetCard(toYard.Card) is { Zone: Zone.Graveyard } yardCard)
            foreach (var ability in yardCard.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.Trigger == TriggerEvent.ThisPutIntoGraveyard))
                AddPending(yardCard.Id, ability, yardCard.Owner, new TriggerInfo(yardCard.Id, yardCard.Version, yardCard.Owner));
        // "Whenever enchanted player is dealt damage."
        if (e is DamageDealt { TargetPlayer: { } hurtPlayer } auraDamage)
            foreach (var aura in State.Battlefield.Select(State.GetCard).Where(c => c.AttachedPlayer == hurtPlayer).ToList())
                Queue(aura.Id, TriggerEvent.EnchantedPlayerDealtDamage, aura.Controller, new TriggerInfo(Player: hurtPlayer, Amount: auraDamage.Amount));
        // "When this is turned face up"; "whenever a permanent you control is turned face up" (the filter says whose).
        if (e is TurnedFaceUp faceUp && State.GetCard(faceUp.Card) is { Zone: Zone.Battlefield } turned)
        {
            Queue(turned.Id, TriggerEvent.TurnedFaceUp, turned.Controller, About(turned, turned.Controller));
            QueueObservers(TriggerEvent.YourPermanentTurnedFaceUp, turned, turned.Controller);
        }
        // "Whenever you manifest dread": the subject is the card put into the graveyard this way, if any.
        if (e is ManifestedDread dread)
            foreach (var card in PermanentsAndEmblemsOf(dread.Player).ToList())
                Queue(card.Id, TriggerEvent.YouManifestDread, dread.Player, dread.ToGraveyard.FirstOrDefault() is { } binned && dread.ToGraveyard.Count > 0
                    ? new TriggerInfo(binned, State.GetCard(binned).Version, dread.Player) : new TriggerInfo(Player: dread.Player));
        // "Whenever you expend N": the spell's mana took the turn's total past N - 1.
        if (e is ManaSpentOnSpell spent)
            foreach (var card in State.PermanentsControlledBy(spent.Player).ToList())
                foreach (var ability in TriggerAbilitiesOf(card).Where(a => a.Trigger == TriggerEvent.YouExpend && spent.Before < a.Expend && a.Expend <= spent.After))
                    AddPending(card.Id, ability, card.Controller, new TriggerInfo(Player: spent.Player));
        // "Whenever you give a gift".
        if (e is GiftGiven given)
            foreach (var card in State.PermanentsControlledBy(given.Giver).ToList())
                Queue(card.Id, TriggerEvent.YouGiveGift, given.Giver, new TriggerInfo(Player: given.Giftee));
        // "Whenever you forage".
        if (e is Foraged foraged)
            foreach (var card in State.PermanentsControlledBy(foraged.Player).ToList())
                Queue(card.Id, TriggerEvent.YouForage, foraged.Player, new TriggerInfo(Player: foraged.Player));
        if (e is PhasedIn phasedIn) QueueObservers(TriggerEvent.PhasesIn, State.GetCard(phasedIn.Card), State.GetCard(phasedIn.Card).Controller);
        // An Aura enchanting a player: an attack between its controller and that player (or their planeswalkers), either way.
        if (e is AttacksDeclared enchantedAttack && State.Combat is { } enchantedCombat)
            foreach (var aura in State.Battlefield.Select(State.GetCard).Where(c => c.AttachedPlayer is not null).ToList())
                foreach (var ability in TriggerAbilitiesOf(aura).Where(a => a.Trigger == TriggerEvent.AttackBetweenYouAndEnchanted))
                {
                    var enchanted = aura.AttachedPlayer!.Value;
                    var against = enchantedAttack.Player == aura.Controller ? enchanted : enchantedAttack.Player == enchanted ? aura.Controller : (PlayerId?)null;
                    if (against is { } defender && enchantedCombat.Attacks.Any(x => x.Defender == defender))
                        AddPending(aura.Id, ability, aura.Controller, new TriggerInfo(Player: enchantedAttack.Player));
                }
        // "Whenever enchanted player is attacked".
        if (e is AttacksDeclared cursedAttack && State.Combat is { } cursedCombat)
            foreach (var aura in State.Battlefield.Select(State.GetCard).Where(c => c.AttachedPlayer is not null).ToList())
                if (cursedCombat.Attacks.Any(x => x.Defender == aura.AttachedPlayer && x.Planeswalker is null))
                    foreach (var ability in TriggerAbilitiesOf(aura).Where(a => a.Trigger == TriggerEvent.EnchantedPlayerAttacked))
                        AddPending(aura.Id, ability, aura.Controller, new TriggerInfo(Player: cursedAttack.Player));
        // "Whenever one or more [creatures] you control attack a player": once for each player attacked.
        if (e is AttacksDeclared declaredAttacks && State.Combat is { } attackCombat)
            foreach (var card in State.PermanentsControlledBy(declaredAttacks.Player).ToList())
                foreach (var ability in TriggerAbilitiesOf(card).Where(a => a.Trigger == TriggerEvent.YouAttackPlayer))
                    foreach (var group in attackCombat.Attacks.Where(a => a.Planeswalker is null).GroupBy(a => a.Defender))
                    {
                        int count = group.Count(a => State.GetCard(a.Attacker) is var attacker && attacker.Controller == declaredAttacks.Player
                                                     && Matches(ability.Filter ?? ObjectFilter.YourCreatures, attacker, attacker.Controller, card, card.Controller));
                        if (count > 0) AddPending(card.Id, ability, card.Controller, new TriggerInfo(Player: group.Key, Amount: count));
                    }
        switch (e)
        {
            case CardMoved { To: Zone.Battlefield } m when _enteringTogether is not null:
                _enteringTogether.Add(m.Card);
                break;
            case CardMoved { To: Zone.Battlefield } m:
                QueueEnterTriggers(m.Card);
                break;
            case CardMoved { From: Zone.Battlefield, To: Zone.Graveyard } m:
                // Modified as it died: its counters as it last existed, or what was still attached to it (rule 700.9).
                if (State.GetCard(m.Card) is { LastKnownInfo: { } lastSeen } deadCard && (lastSeen.Types & CardType.Creature) != 0
                    && (lastSeen.Counters.Values.Any(v => v > 0) || State.Battlefield.Select(State.GetCard).Any(a => a.AttachedTo == deadCard.Id
                        && (a.HasSubtype("Equipment") || (a.HasSubtype("Aura") && a.Controller == m.LastController)))))
                    State.GetPlayer(m.LastController).ModifiedCreatureDiedThisTurn = true;
                if (State.GetCard(m.Card) is { LastKnownInfo: { } diedWithAuras } diedCard && (diedWithAuras.Types & CardType.Creature) != 0
                    && State.Battlefield.Select(State.GetCard).Where(a => a.AttachedTo == diedCard.Id && a.HasSubtype("Aura")).Select(a => a.Controller).ToList() is { Count: > 0 } auraControllers)
                    State.AurasAtDeath[(diedCard.Id, diedCard.Version)] = auraControllers;
                QueuePersistAndUndying(State.GetCard(m.Card), m.LastController);
                QueueYourGraveyard(State.GetCard(m.Card), Zone.Battlefield);
                Queue(m.Card, TriggerEvent.LeavesBattlefield, m.LastController);
                Queue(m.Card, TriggerEvent.PutIntoGraveyard, m.LastController);
                QueueObservers(TriggerEvent.PermanentDies, State.GetCard(m.Card), m.LastController); // any permanent type: "a creature or planeswalker you control dies"
                if (!WasCreature(State.GetCard(m.Card))) break;
                Queue(m.Card, TriggerEvent.Dies, m.LastController);
                if (State.GetCard(m.Card).LastKnownInfo is { } diedAs)
                    foreach (var watch in State.WhenDiesThisTurn.Where(w => w.Card == m.Card && w.Version == diedAs.Version && w.Turn == State.TurnNumber && (!w.UnderControllersControl || w.Controller == m.LastController)).ToList())
                    {
                        State.WhenDiesThisTurn.Remove(watch);
                        AddPending(watch.Source, watch.Ability, watch.Controller, new TriggerInfo(m.Card, State.GetCard(m.Card).Version, m.LastController));
                    }
                if (State.GetCard(m.Card).LastKnownInfo is { Blitzed: true }) AddPending(m.Card, BlitzDraw, m.LastController);
                Queue(m.Card, TriggerEvent.DiesOrExiled, m.LastController);
                QueueObservers(TriggerEvent.CreatureDies, State.GetCard(m.Card), m.LastController);
                QueueObservers(TriggerEvent.CreatureDiesOrExiled, State.GetCard(m.Card), m.LastController);
                foreach (var delayed in State.CreatureDiesThisTurnTriggers.Where(d => d.Turn == State.TurnNumber).ToList())
                    AddPending(delayed.Source, delayed.Ability, delayed.Controller, new TriggerInfo(m.Card, State.GetCard(m.Card).Version, m.LastController));
                break;
            case SpellCopied copied:
            {
                // "Whenever you … copy a spell": the player who made the copy controls it (rule 707.10c).
                var copy = State.GetCard(copied.Copy);
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == copied.Controller))
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouCastOrCopySpell))
                        if (Matches((ability.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, copy, copied.Controller, observer, observer.Controller))
                            for (int times = 1 + InstantSorceryExtraTriggers(observer, copy, copied.Controller); times > 0; times--)
                                AddPending(observer.Id, ability, observer.Controller, About(copy, copied.Controller, ManaValueOf(copy)));
                break;
            }
            case AttackerDeclared a:
            {
                var attacker = State.GetCard(a.Attacker);
                Queue(a.Attacker, TriggerEvent.Attacks, attacker.Controller, About(attacker, a.Defender) with { Defender = a.Defender });
                // Training (702.149a): it attacks with another creature with greater power.
                if (attacker.Has(Keyword.Training) && State.Combat is { } trainingCombat
                    && trainingCombat.Attacks.Any(x => x.Attacker != attacker.Id && State.GetCard(x.Attacker).Power > attacker.Power))
                    AddPending(attacker.Id, TrainingTrigger, attacker.Controller, About(attacker, a.Defender) with { Defender = a.Defender });
                if (attacker.Has(Keyword.Melee)) // melee (702.121a)
                    AddPending(attacker.Id, MeleeTrigger, attacker.Controller, About(attacker, a.Defender) with { Defender = a.Defender });
                QueueAttackKeywordTriggers(attacker, About(attacker, a.Defender) with { Defender = a.Defender });
                Queue(a.Attacker, TriggerEvent.AttacksOrBlocks, attacker.Controller, About(attacker, a.Defender) with { Defender = a.Defender });
                foreach (var gear in State.Battlefield.Select(State.GetCard).Where(c => c.AttachedTo == attacker.Id && (c.HasSubtype("Equipment") || c.HasSubtype("Aura"))).ToList())
                    Queue(gear.Id, TriggerEvent.EquippedAttacks, gear.Controller, About(attacker, a.Defender) with { Defender = a.Defender });
                QueueObservers(TriggerEvent.CreatureAttacks, attacker, attacker.Controller,
                    new TriggerInfo(attacker.Id, attacker.Version, attacker.Controller) { Defender = a.Defender });
                break;
            }
            case AttacksDeclared a:
                foreach (var card in State.PermanentsControlledBy(a.Player).Concat(State.Emblems.Select(State.GetCard).Where(e => e.Owner == a.Player)).ToList())
                    Queue(card.Id, TriggerEvent.YouAttack, a.Player, new TriggerInfo(Amount: a.Count));
                // "Whenever one or more Sphinxes you control attack": with that many.
                if (State.Combat is { } withCombat)
                    foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == a.Player))
                        foreach (var ability in abilities.Where(x => x.Trigger == TriggerEvent.YouAttackWithMatching))
                        {
                            int matching = withCombat.Attacks.Select(x => State.GetCard(x.Attacker))
                                .Count(c => c.Controller == a.Player && Matches(ability.Filter ?? ObjectFilter.YourCreatures, c, c.Controller, observer, observer.Controller));
                            if (matching > 0) AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(Player: a.Player, Amount: matching));
                        }
                // "Whenever you attack …" abilities that work while their card is in your graveyard.
                foreach (var card in State.GetPlayer(a.Player).Graveyard.Select(State.GetCard).ToList())
                    foreach (var ability in card.Definition.Abilities.OfType<TriggeredAbility>().Where(t => t.FromGraveyard && t.Trigger == TriggerEvent.YouAttack))
                        AddPending(card.Id, ability, a.Player, new TriggerInfo(Amount: a.Count));
                // "Whenever a player attacks [you]": once per declaration (amount: creatures attacking the observer's controller).
                if (State.Combat is { } declaredCombat)
                {
                    foreach (var (observer, abilities) in Observers())
                        foreach (var watching in abilities.Where(x => x.Trigger == TriggerEvent.PlayerAttacks))
                            AddPending(observer.Id, watching, observer.Controller,
                                new TriggerInfo(Player: a.Player, Amount: declaredCombat.Attacks.Count(x => x.Defender == observer.Controller && x.Planeswalker is null)));
                    // "Whenever one or more creatures that are enchanted by an Aura you control attack": whoever attacks.
                    foreach (var (observer, abilities) in Observers())
                        foreach (var watching in abilities.Where(x => x.Trigger == TriggerEvent.CreaturesAttackMatching))
                        {
                            int matching = declaredCombat.Attacks.Select(x => State.GetCard(x.Attacker))
                                .Count(c => Matches((watching.Filter ?? new ObjectFilter(CardType.Creature)) with { Controller = ControllerFilter.Any }, c, c.Controller, observer, observer.Controller));
                            if (matching > 0) AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(Player: a.Player, Amount: matching));
                        }
                    // "Whenever a player attacks one of your opponents": once for each opponent with creatures attacking them.
                    foreach (var attackedOpponent in declaredCombat.Attacks.Where(x => x.Planeswalker is null).Select(x => x.Defender).Distinct().ToList())
                        foreach (var (observer, abilities) in Observers().Where(o => State.OpponentsOf(o.Card.Controller).Contains(attackedOpponent)))
                            foreach (var watching in abilities.Where(x => x.Trigger == TriggerEvent.PlayerAttacksYourOpponent))
                                AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(Player: a.Player,
                                    Amount: declaredCombat.Attacks.Count(x => x.Defender == attackedOpponent && x.Planeswalker is null)) { Defender = attackedOpponent });
                    // "Whenever one or more [non-Toy] creatures you control attack a player": once per player attacked by matching creatures of the observer's controller.
                    foreach (var attacked in declaredCombat.Attacks.Where(x => x.Planeswalker is null).GroupBy(x => x.Defender).ToList())
                        foreach (var (observer, abilities) in Observers().Where(o => o.Card.Zone == Zone.Battlefield))
                            foreach (var watching in abilities.Where(x => x.Trigger == TriggerEvent.YourCreaturesAttackPlayer))
                                if (attacked.Any(x => State.GetCard(x.Attacker) is var att && att.Controller == observer.Controller
                                                      && Matches((watching.Filter ?? ObjectFilter.YourCreatures) with { Controller = ControllerFilter.Any }, att, att.Controller, observer, observer.Controller)))
                                    AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(Player: attacked.Key));
                    // "Whenever one or more creatures attack one of your opponents or a planeswalker they control": once per player or planeswalker attacked.
                    foreach (var group in declaredCombat.Attacks.GroupBy(x => (x.Defender, x.Planeswalker)).ToList())
                        foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller != group.Key.Defender))
                            foreach (var watching in abilities.Where(x => x.Trigger == TriggerEvent.CreaturesAttackOpponent && !(x.PlayersOnly && group.Key.Planeswalker is not null)))
                                AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(group.Key.Planeswalker, 0, group.Key.Defender, group.Count()));
                }
                // Exalted (702.83): a creature attacking alone gets +1/+1 for each instance among its controller's permanents.
                if (a.Count == 1 && State.Combat?.Attacks.FirstOrDefault(x => State.GetCard(x.Attacker).Controller == a.Player) is { } alone)
                    foreach (var card in State.PermanentsControlledBy(a.Player).Where(c => c.Has(Keyword.Exalted)).ToList())
                        AddPending(card.Id, ExaltedTrigger, a.Player, new TriggerInfo(alone.Attacker, State.GetCard(alone.Attacker).Version, a.Player));
                break;
            // "Whenever this creature blocks" triggers once even when it blocks several attackers (rule 509.3c): on its first block.
            case BlockerDeclared b when State.Combat is not { } blockCombat || blockCombat.Attacks.Count(a => a.Blockers.Contains(b.Blocker)) <= 1:
                QueueFlanking(b);
                Queue(b.Blocker, TriggerEvent.Blocks, State.GetCard(b.Blocker).Controller, About(State.GetCard(b.Attacker)));
                QueueObservers(TriggerEvent.CreatureBlocks, State.GetCard(b.Blocker), State.GetCard(b.Blocker).Controller);
                Queue(b.Blocker, TriggerEvent.AttacksOrBlocks, State.GetCard(b.Blocker).Controller);
                break;
            case BlockerDeclared later:
                QueueFlanking(later);
                break;
            case LifeChanged l when l.NewLife > l.OldLife:
                foreach (var card in State.PermanentsControlledBy(l.Player).ToList())
                {
                    Queue(card.Id, TriggerEvent.YouGainLife, l.Player, new TriggerInfo(Player: l.Player, Amount: l.NewLife - l.OldLife));
                    Queue(card.Id, TriggerEvent.YouGainOrLoseLife, l.Player, new TriggerInfo(Player: l.Player, Amount: l.NewLife - l.OldLife));
                }
                break;
            case MonarchChanged mc:
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
                    if (card.Controller != mc.Player) Queue(card.Id, TriggerEvent.OpponentBecomesMonarch, card.Controller, new TriggerInfo(Player: mc.Player));
                break;
            case LifeChanged l when l.NewLife < l.OldLife:
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
                {
                    if (card.Controller != l.Player) Queue(card.Id, TriggerEvent.OpponentLosesLife, card.Controller, new TriggerInfo(Player: l.Player, Amount: l.OldLife - l.NewLife));
                    Queue(card.Id, TriggerEvent.PlayerLosesLife, card.Controller, new TriggerInfo(Player: l.Player, Amount: l.OldLife - l.NewLife));
                    if (card.Controller == l.Player) Queue(card.Id, TriggerEvent.YouGainOrLoseLife, l.Player, new TriggerInfo(Player: l.Player, Amount: l.OldLife - l.NewLife));
                }
                break;
            case CardDrawn d:
                foreach (var card in State.PermanentsControlledBy(d.Player).ToList())
                    Queue(card.Id, TriggerEvent.YouDrawCard, d.Player, new TriggerInfo(Player: d.Player, Amount: 1));
                foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Controller != d.Player).ToList())
                    Queue(card.Id, TriggerEvent.OpponentDrawsCard, card.Controller, new TriggerInfo(Player: d.Player, Amount: 1));
                // "Whenever a player draws a card" (an emblem too).
                foreach (var watcher in State.ApnapOrder().SelectMany(p => PermanentsAndEmblemsOf(p).Select(c => (c, p))).ToList())
                    Queue(watcher.c.Id, TriggerEvent.AnyPlayerDraws, watcher.p, new TriggerInfo(Player: d.Player, Amount: 1));
                break;
            case StepBegan { Step: Step.Draw } s:
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
                    Queue(card.Id, TriggerEvent.EachDrawStep, card.Controller, new TriggerInfo(Player: s.ActivePlayer));
                break;
            case CountersPlaced { Kind: CounterKind.Lore } lore when State.GetCard(lore.Card).Zone == Zone.Battlefield:
            {
                // Chapter abilities trigger as lore counters reach their chapter numbers (rule 714.2b).
                var saga = State.GetCard(lore.Card);
                int now = saga.CounterCount(CounterKind.Lore), before = now - lore.Count;
                foreach (var ability in TriggerAbilitiesOf(saga).Where(a => a.Trigger == TriggerEvent.Chapter))
                    foreach (var chapter in ability.Chapters.Where(c => c > before && c <= now && c >= saga.ReadAheadChapter))
                        AddPending(saga.Id, ability, saga.Controller, new TriggerInfo(Amount: chapter));
                break;
            }
            case CountersPlaced cp when State.GetCard(cp.Card).Zone == Zone.Battlefield:
            {
                var target = State.GetCard(cp.Card);
                foreach (var observer in State.Battlefield.Select(State.GetCard).ToList())
                    foreach (var ability in observer.Abilities.OfType<TriggeredAbility>())
                    {
                        if (ability.Trigger != TriggerEvent.CountersPlaced || (ability.CounterKind != cp.Kind && !ability.AnyCounterKind)) continue;
                        if (ability.PlacedByYou && cp.By != observer.Controller) continue;
                        if (ability.FirstCountersThisTurn && target.CounterEventsThisTurn != 1) continue;
                        bool hit = ability.OnSelf ? observer.Id == target.Id
                            : Matches(ability.Filter ?? ObjectFilter.YourCreatures, target, target.Controller, observer, observer.Controller);
                        if (!hit) continue;
                        for (int times = ability.PerCounter ? cp.Count : 1; times > 0; times--)
                            AddPending(observer.Id, ability, observer.Controller, About(target, amount: ability.PerCounter ? 1 : cp.Count) with { CounterKinds = new() { cp.Kind } });
                    }
                break;
            }
            case PermanentSacrificed ps:
            {
                var gone = State.GetCard(ps.Card);
                var sacrificer = gone.LastKnownInfo?.Controller ?? gone.Owner;
                Queue(ps.Card, TriggerEvent.SelfSacrificed, sacrificer);
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == sacrificer))
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouSacrifice))
                        if (Matches(ability.Filter ?? ObjectFilter.Anything, gone, sacrificer, observer, observer.Controller, lastKnown: true))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(gone.Id, gone.Version, sacrificer));
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller != sacrificer))
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.OpponentSacrifices))
                        if (Matches((ability.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, gone, sacrificer, observer, observer.Controller, lastKnown: true))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(gone.Id, gone.Version, sacrificer));
                break;
            }
            case AbilityActivated aa:
            {
                var activated = State.GetCard(aa.Source);
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == aa.Player))
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.YouActivateAbility))
                        if (Matches((ability.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, activated, activated.Controller, observer, observer.Controller))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(activated.Id, activated.Version, aa.Player));
                break;
            }
            case CardDiscarded cd:
                foreach (var card in State.Battlefield.Select(State.GetCard).Where(c => c.Controller != cd.Player).ToList())
                    Queue(card.Id, TriggerEvent.OpponentDiscards, card.Controller, new TriggerInfo(cd.Card, State.GetCard(cd.Card).Version, cd.Player));
                break;
            case PermanentUntapped u when State.GetCard(u.Card).Zone == Zone.Battlefield:
            {
                var untapped = State.GetCard(u.Card);
                foreach (var ability in TriggerAbilitiesOf(untapped).Where(a => a.Trigger == TriggerEvent.BecomesUntapped && a.Filter is null))
                    AddPending(u.Card, ability, untapped.Controller);
                // "Whenever equipped creature becomes untapped": watchers with a filter.
                foreach (var (observer, abilities) in Observers())
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.BecomesUntapped && a.Filter is not null))
                        if (Matches(ability.Filter!, untapped, untapped.Controller, observer, observer.Controller))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(untapped.Id, untapped.Version, untapped.Controller));
                break;
            }
            case TappedForMana land when State.GetCard(land.Source).Is(CardType.Land):
            {
                // "Whenever a player taps a land for mana" (not a mana ability: it uses the stack, rule 605.1b).
                var tapped = State.GetCard(land.Source);
                foreach (var (observer, abilities) in Observers())
                    foreach (var watching in abilities.Where(a => a.Trigger == TriggerEvent.PlayerTapsLandForMana))
                        AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(tapped.Id, tapped.Version, land.Player));
                break;
            }
            case CountersRemoved removed:
            {
                // Once for each counter removed ("whenever a +1/+1 counter is removed from this creature").
                var from = State.GetCard(removed.Card);
                foreach (var ability in TriggerAbilitiesOf(from).Where(a => a.Trigger == TriggerEvent.CounterRemoved && (a.AnyCounterKind || a.CounterKind == removed.Kind)))
                    for (int n = 0; n < removed.Count; n++)
                        AddPending(from.Id, ability, from.Controller, new TriggerInfo(from.Id, from.Version, from.Controller, 1));
                break;
            }
            case TappedForMana tfm when State.GetCard(tfm.Source).Is(CardType.Artifact):
            {
                var artifact = State.GetCard(tfm.Source);
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller != tfm.Player))
                    foreach (var watching in abilities.Where(a => a.Trigger == TriggerEvent.OpponentTapsArtifactForMana))
                        AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(artifact.Id, artifact.Version, tfm.Player));
                break;
            }
            case LandPlayed played:
            {
                var land = State.GetCard(played.Card);
                foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == played.Player))
                    foreach (var watching in abilities.Where(a => a.Trigger == TriggerEvent.YouPlayLand
                                                                  && Matches((a.Filter ?? new ObjectFilter()) with { Controller = ControllerFilter.Any }, land, land.Controller, observer, observer.Controller)))
                        AddPending(observer.Id, watching, observer.Controller, new TriggerInfo(land.Id, land.Version, played.Player));
                break;
            }
            case PermanentTapped t when State.GetCard(t.Card).Zone == Zone.Battlefield:
                Queue(t.Card, TriggerEvent.BecomesTapped, State.GetCard(t.Card).Controller);
                break;
            case PermanentTransformed transformed:
                CollectTransformTriggers(transformed);
                break;
            case PlayerLost lost:
                foreach (var watcher in State.Battlefield.Select(State.GetCard).Where(w => w.ChosenPlayer == lost.Player).ToList())
                    Queue(watcher.Id, TriggerEvent.ChosenPlayerLoses, watcher.Controller, new TriggerInfo(Player: lost.Player));
                break;
            case ControlChanged changed when changed.Previous is { } previous:
                CollectControlChangeTriggers(changed, previous);
                break;
            case SpellCast c:
            {
                var spell = State.GetCard(c.Card);
                // "When you next cast an instant or sorcery spell this turn, copy that spell [twice]": each such delayed ability triggers.
                foreach (var next in State.CopyNextInstantOrSorcery.Where(x => x.Player == c.Player && x.Turn == State.TurnNumber
                             && (x.AnySpell || spell.Is(CardType.Instant) || spell.Is(CardType.Sorcery))).ToList())
                    {
                        State.CopyNextInstantOrSorcery.Remove(next);
                        _pendingTriggers.Add(new PendingTrigger(c.Card, next.Copies switch { 1 => CopyThatSpell, -1 => CopyThatSpellPerCommanderCast, _ => CopyThatSpellTwice }, c.Player, new TriggerInfo(spell.Id, spell.Version, c.Player)));
                    }
                // "When you next cast a spell with {X} in its mana cost this turn, …" (from a mana ability).
                foreach (var whenNext in State.WhenNextCast.Where(w => w.Trigger.Controller == c.Player && w.Turn == State.TurnNumber
                             && Matches(w.Spells with { Controller = ControllerFilter.Any }, spell, c.Player, State.GetCard(w.Trigger.Source), c.Player)).ToList())
                {
                    State.WhenNextCast.Remove(whenNext);
                    _pendingTriggers.Add(new PendingTrigger(whenNext.Trigger.Source, whenNext.Trigger.Ability, c.Player, new TriggerInfo(spell.Id, spell.Version, c.Player, ManaValueOf(spell))));
                }
                foreach (var observer in State.Battlefield.Concat(State.Emblems).Select(State.GetCard).ToList())
                {
                    bool mine = observer.Controller == c.Player;
                    if (mine && !spell.IsCreature && observer.Has(Keyword.Prowess))
                        for (int instances = 1 + (observer.HasTextAbilities ? observer.Definition.ExtraProwess : 0); instances > 0; instances--)
                            for (int copies = 1 + ExtraTriggersFor(observer) + InstantSorceryExtraTriggers(observer, spell, c.Player); copies > 0; copies--) _pendingTriggers.Add(new PendingTrigger(observer.Id, ProwessTrigger, c.Player));
                    if (mine && observer.Has(Keyword.Extort))
                        for (int copies = 1 + ExtraTriggersFor(observer) + InstantSorceryExtraTriggers(observer, spell, c.Player); copies > 0; copies--) _pendingTriggers.Add(new PendingTrigger(observer.Id, ExtortTrigger, c.Player));
                    foreach (var ability in observer.Abilities.OfType<TriggeredAbility>())
                    {
                        var filter = (ability.Filter ?? new ObjectFilter()) with { Controller = ControllerFilter.Any };
                        if (ability.TargetsSource && !(State.Stack.OfType<SpellOnStack>().FirstOrDefault(sp => sp.Card == c.Card)?.Targets.Any(t => t.Target.Card == observer.Id) ?? false))
                            continue;
                        var spellTargets = State.Stack.OfType<SpellOnStack>().FirstOrDefault(sp => sp.Card == c.Card)?.Targets;
                        bool aimedHit = spellTargets is not null
                            && (ability.SpellTargets is { } aimed && spellTargets.Any(t => t.Target.Card is { } tc && State.GetCard(tc) is var tcard && Matches(aimed, tcard, tcard.Controller, observer, observer.Controller))
                                || ability.SpellTargetsOpponent && spellTargets.Any(t => t.Target.Player is { } tp && State.OpponentsOf(observer.Controller).Contains(tp)));
                        if (ability.SpellTargetsOnlyOne && spellTargets?.Select(t => t.Target).Distinct().Count() != 1) aimedHit = false;
                        bool aims = ability.SpellTargets is not null || ability.SpellTargetsOpponent;
                        if (aims && !aimedHit && !ability.FilterOrSpellTargets) continue;
                        if ((ability.Trigger is TriggerEvent.YouCastSpell or TriggerEvent.YouCastOrCopySpell && mine || ability.Trigger == TriggerEvent.OpponentCastsSpell && !mine
                             || ability.Trigger == TriggerEvent.AnyPlayerCastsSpell
                             || ability.Trigger == TriggerEvent.EnchantedPlayerCastsSpell && observer.Zone == Zone.Battlefield && observer.AttachedPlayer == c.Player)
                            && (ability.FilterOrSpellTargets ? aimedHit || Matches(filter, spell, c.Player, observer, observer.Controller) : Matches(filter, spell, c.Player, observer, observer.Controller)))
                            for (int times = 1 + InstantSorceryExtraTriggers(observer, spell, c.Player); times > 0; times--)
                                AddPending(observer.Id, ability, observer.Controller, About(spell, c.Player, ManaValueOf(spell)) with { SpellManaSpent = spell.ManaSpent });
                    }
                }
                break;
            }
            case StepBegan { Step: Step.BeginCombat } s:
                foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourBeginCombat, s.ActivePlayer);
                foreach (var card in State.GetPlayer(s.ActivePlayer).Graveyard.Select(State.GetCard).ToList())
                    foreach (var ability in card.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromGraveyard && a.Trigger == TriggerEvent.YourBeginCombat))
                        AddPending(card.Id, ability, s.ActivePlayer);
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList()) Queue(card.Id, TriggerEvent.EachBeginCombat, card.Controller, new TriggerInfo(Player: s.ActivePlayer));
                break;
            case StepBegan { Step: Step.DeclareAttackers } declare:
                foreach (var (delayed, _) in State.AtNextDeclareAttackers.Where(d => d.Whose == declare.ActivePlayer).ToList())
                {
                    State.AtNextDeclareAttackers.RemoveAll(d => d.Trigger == delayed);
                    _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller,
                        new TriggerInfo(delayed.AboutCard?.Card, delayed.AboutCard?.Version ?? 0, delayed.About, delayed.Amount)));
                }
                break;
            case StepBegan { Step: Step.EndCombat } endCombat:
                foreach (var delayed in State.AtEndOfCombat.ToList())
                {
                    State.AtEndOfCombat.Remove(delayed);
                    _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller,
                        new TriggerInfo(delayed.AboutCard?.Card, delayed.AboutCard?.Version ?? 0, delayed.About, delayed.Amount)));
                }
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList()) Queue(card.Id, TriggerEvent.EndOfCombat, card.Controller, new TriggerInfo(Player: endCombat.ActivePlayer));
                break;
            case DamageDealt { IsCombat: false, TargetPlayer: { } burned } nd when State.GetCard(nd.Source).Controller != burned:
            {
                var source = State.GetCard(nd.Source);
                foreach (var card in State.PermanentsControlledBy(source.Controller).ToList())
                    Queue(card.Id, TriggerEvent.YourSourceDealsNoncombatDamageToOpponent, source.Controller, new TriggerInfo(source.Id, source.Version, burned, nd.Amount));
                break;
            }
            case DamageDealt { IsCombat: true, TargetCard: { } dealtTo } cd2:
            {
                var source = State.GetCard(cd2.Source);
                NoteCombatDamage(source, null, cd2.Amount);
                var damaged = State.GetCard(dealtTo);
                if (damaged.Is(CardType.Planeswalker))
                {
                    Queue(cd2.Source, TriggerEvent.DealsCombatDamageToPlayerOrPlaneswalker, source.Controller, About(source, damaged.Controller, cd2.Amount));
                    QueueObservers(TriggerEvent.CreatureDealsCombatDamageToPlayerOrPlaneswalker, source, source.Controller, About(source, damaged.Controller, cd2.Amount));
                }
                break;
            }
            case DamageDealt { IsCombat: true, TargetPlayer: { } hurt } d:
            {
                NoteCombatDamage(State.GetCard(d.Source), hurt, d.Amount);
                var source = State.GetCard(d.Source);
                QueueMonarchTaken(source, hurt);
                TriggerWhenDealsCombatDamage(source, hurt, d.Amount);
                Queue(d.Source, TriggerEvent.DealsCombatDamageToPlayer, source.Controller, About(source, hurt, d.Amount));
                Queue(d.Source, TriggerEvent.DealsCombatDamageToPlayerOrPlaneswalker, source.Controller, About(source, hurt, d.Amount));
                QueueObservers(TriggerEvent.CreatureDealsCombatDamageToPlayer, source, source.Controller, About(source, hurt, d.Amount));
                QueueObservers(TriggerEvent.CreatureDealsCombatDamageToPlayerOrPlaneswalker, source, source.Controller, About(source, hurt, d.Amount));
                foreach (var gear in State.Battlefield.Select(State.GetCard).Where(c => c.AttachedTo == source.Id && c.HasSubtype("Equipment")).ToList())
                    Queue(gear.Id, TriggerEvent.EquippedCombatDamageToPlayer, gear.Controller, About(source, hurt, d.Amount));
                break;
            }
            case StepBegan { Step: Step.PrecombatMain } s:
                CountMainPhase();
                ReleaseAtYourNextMainPhase(s.ActivePlayer);
                foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourPrecombatMain, s.ActivePlayer);
                foreach (var paradigm in State.Paradigms.Where(p => p.Controller == s.ActivePlayer).ToList())
                    _pendingTriggers.Add(new PendingTrigger(paradigm.Source, ParadigmAbility(paradigm.Spell), paradigm.Controller));
                foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourMainPhase, s.ActivePlayer);
                break;
            case StepBegan { Step: Step.PostcombatMain } s:
                ReleaseAtYourNextMainPhase(s.ActivePlayer);
                foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourPostcombatMain, s.ActivePlayer);
                foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourMainPhase, s.ActivePlayer);
                if (CountMainPhase() == 2)
                    foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourSecondMainPhase, s.ActivePlayer);
                break;
            case CardMoved { To: Zone.Graveyard } toGraveyard when toGraveyard.From != Zone.Battlefield:
            {
                var moved = State.GetCard(toGraveyard.Card);
                QueueYourGraveyard(moved, toGraveyard.From);
                foreach (var (observer, abilities) in Observers())
                    foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.CardPutIntoGraveyardNotFromBattlefield))
                        if (Matches((ability.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, moved, moved.Owner, observer, observer.Controller))
                            AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(moved.Id, moved.Version, moved.Owner));
                break;
            }
            case StepBegan { Step: Step.Upkeep } s:
                // Delayed abilities waiting for "the next upkeep" (rule 603.7).
                foreach (var delayed in State.AtNextUpkeep.Where(d => d.OnlyAtUpkeepOf is null || d.OnlyAtUpkeepOf == s.ActivePlayer).ToList())
                {
                    State.AtNextUpkeep.Remove(delayed);
                    _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller,
                        new TriggerInfo(delayed.AboutCard?.Card, delayed.AboutCard?.Version ?? 0, delayed.About, delayed.Amount))
                    {
                        SourceTransforms = delayed.SourceTransforms, SourceVersion = delayed.SourceVersion ?? -1,
                    });
                }
                foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourUpkeep, s.ActivePlayer);
                // Vanishing (702.63a): "at the beginning of your upkeep, if this permanent has a time counter on it, remove a time counter from it".
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).Where(c => c.HasTextAbilities && c.Definition.Vanishing > 0 && c.CounterCount(CounterKind.Time) > 0).ToList())
                    _pendingTriggers.Add(new PendingTrigger(card.Id, VanishingUpkeep, s.ActivePlayer) { SourceVersion = card.Version });
                // Suspend (702.62a): "at the beginning of your upkeep, if this card is suspended, remove a time counter from it".
                foreach (var card in State.GetPlayer(s.ActivePlayer).Exile.Select(State.GetCard).Where(c => c.Suspended && c.CounterCount(CounterKind.Time) > 0).ToList())
                    _pendingTriggers.Add(new PendingTrigger(card.Id, SuspendUpkeep, s.ActivePlayer));
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList())
                    Queue(card.Id, TriggerEvent.EachUpkeep, card.Controller, new TriggerInfo(Player: s.ActivePlayer));
                // "At the beginning of each upkeep, … return this card from your graveyard": the card's own ability, in its owner's graveyard.
                foreach (var card in State.Players.SelectMany(p => p.Graveyard).Select(State.GetCard).ToList())
                    foreach (var ability in card.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromGraveyard && a.Trigger == TriggerEvent.EachUpkeep))
                        AddPending(card.Id, ability, card.Owner, new TriggerInfo(Player: s.ActivePlayer));
                // "At the beginning of the upkeep of enchanted creature's controller."
                foreach (var aura in State.Battlefield.Select(State.GetCard).Where(c => c.AttachedTo is { } host && State.GetCard(host) is { Zone: Zone.Battlefield } h
                                                                                       && h.Controller == s.ActivePlayer).ToList())
                    Queue(aura.Id, TriggerEvent.EnchantedControllersUpkeep, aura.Controller, new TriggerInfo(Player: s.ActivePlayer));
                break;
            case StepBegan { Step: Step.End } s:
                // Impending (702.176a): "at the beginning of your end step, if this permanent's impending cost was paid and it has a time counter on it, remove a time counter from it".
                foreach (var card in State.PermanentsControlledBy(s.ActivePlayer).Where(c => c.HasTextAbilities && c.Impended && c.CounterCount(CounterKind.Time) > 0).ToList())
                    _pendingTriggers.Add(new PendingTrigger(card.Id, ImpendingEndStep, s.ActivePlayer) { SourceVersion = card.Version });
                // "You may play those cards until your next end step": the permission ends as that end step begins.
                State.PlayableFromExile.RemoveAll(p => p.UntilNextEndStepOf == s.ActivePlayer && (p.MadeOnTurn < State.TurnNumber || !p.MadeDuringEndStep));
                foreach (var delayed in State.AtNextEndStepTriggers.ToList())
                {
                    State.AtNextEndStepTriggers.Remove(delayed);
                    _pendingTriggers.Add(new PendingTrigger(delayed.Source, delayed.Ability, delayed.Controller, new TriggerInfo(Player: delayed.About, Amount: delayed.Amount))
                    {
                        SourceTransforms = delayed.SourceTransforms, SourceVersion = delayed.SourceVersion ?? -1,
                    });
                }
                foreach (var later in State.AtNextEndStepEffects.ToList())
                {
                    State.AtNextEndStepEffects.Remove(later);
                    var ability = new TriggeredAbility
                    {
                        Trigger = TriggerEvent.NextUpkeep, Text = "At the beginning of the next end step",
                        Effects = later.Unless is { } unless ? new Effect[] { new IfThen(new Not(unless), later.Effects, null) } : later.Effects,
                    };
                    _pendingTriggers.Add(new PendingTrigger(later.Source, ability, later.Controller, new TriggerInfo(later.Token, later.TokenVersion)) { SourceTransforms = later.SourceTransforms });
                }
                // "At the beginning of that player's next end step" (a later end step than the one it was made in).
                foreach (var waiting in State.AtPlayersNextEndStep.Where(w => w.Whose == s.ActivePlayer && (w.MadeOnTurn < State.TurnNumber || !w.MadeDuringEndStep)).ToList())
                {
                    State.AtPlayersNextEndStep.Remove(waiting);
                    _pendingTriggers.Add(new PendingTrigger(waiting.Trigger.Source, waiting.Trigger.Ability, waiting.Trigger.Controller,
                        new TriggerInfo(waiting.Trigger.AboutCard?.Card, waiting.Trigger.AboutCard?.Version ?? 0, waiting.Whose))
                    {
                        SourceTransforms = waiting.Trigger.SourceTransforms, SourceVersion = waiting.Trigger.SourceVersion ?? -1,
                    });
                }
                foreach (var delayed in State.AtNextEndStep.ToList())
                {
                    State.AtNextEndStep.Remove(delayed);
                    var delayedAbility = !delayed.Return ? SacrificeAtEndStep
                        : delayed.Counters is { } c ? ReturnAtEndStep with { Effects = new Effect[] { new PutOntoBattlefield(Subject.Triggered) { Counters = c.Count, CounterKind = c.Kind } } }
                        : ReturnAtEndStep;
                    _pendingTriggers.Add(new PendingTrigger(delayed.Card, delayedAbility, delayed.Controller, new TriggerInfo(delayed.Card, delayed.Version)));
                }
                foreach (var card in PermanentsAndEmblemsOf(s.ActivePlayer)) Queue(card.Id, TriggerEvent.YourEndStep, s.ActivePlayer);
                foreach (var card in State.Battlefield.Select(State.GetCard).ToList()) Queue(card.Id, TriggerEvent.EachEndStep, card.Controller, new TriggerInfo(Player: s.ActivePlayer));
                QueueMonarchEndStep(s.ActivePlayer);
                break;
        }
    }

    /// <summary>A player's permanents and emblems: what "at the beginning of your upkeep / end step" abilities belong to.</summary>
    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        for (int d = 2; d * d <= n; d++) if (n % d == 0) return false;
        return true;
    }

    /// <summary>The turn <see cref="_mainPhases"/> counts for, and how many main phases it has begun so far.</summary>
    private int _mainPhasesTurn = -1, _mainPhases;

    /// <summary>A main phase begins: which one of the turn it is (the first, the second, …; rule 505.1).</summary>
    private int CountMainPhase()
    {
        if (_mainPhasesTurn != State.TurnNumber) { _mainPhasesTurn = State.TurnNumber; _mainPhases = 0; }
        return ++_mainPhases;
    }

    private List<Card> PermanentsAndEmblemsOf(PlayerId player) =>
        State.PermanentsControlledBy(player).Concat(State.Emblems.Select(State.GetCard).Where(e => e.Owner == player)).ToList();

    /// <summary>Triggered abilities of a card: its current ones, or what it had on the battlefield if it just left ("when this dies").</summary>
    private static IEnumerable<TriggeredAbility> TriggerAbilitiesOf(Card card) =>
        (card.Zone == Zone.Battlefield || card.LastKnownInfo is null ? card.Abilities : card.LastKnownInfo.Abilities).OfType<TriggeredAbility>()
        .Where(a => !a.FromGraveyard);

    /// <summary>Permanents (and cards in graveyards with abilities that work from there) watching for events.</summary>
    private IEnumerable<(Card Card, IEnumerable<TriggeredAbility> Abilities)> Observers() =>
        State.Battlefield.Concat(State.Emblems).Select(State.GetCard).Select(c => (c, c.Abilities.OfType<TriggeredAbility>().Where(a => !a.FromGraveyard)))
            .Concat(State.Players.SelectMany(p => p.Command).Select(State.GetCard)
                .Where(c => c.Definition.Abilities.OfType<TriggeredAbility>().Any(a => a.FromCommandZone))
                .Select(c => (c, c.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromCommandZone))))
            .Concat(State.Players.SelectMany(p => p.Graveyard).Select(State.GetCard)
                .Where(c => c.Definition.Abilities.OfType<TriggeredAbility>().Any(a => a.FromGraveyard))
                .Select(c => (c, c.Definition.Abilities.OfType<TriggeredAbility>().Where(a => a.FromGraveyard))))
            .ToList();

    /// <summary>"Whenever one or more [cards] are put into your graveyard from anywhere": the owner's observers.</summary>
    private void QueueYourGraveyard(Card moved, Zone from)
    {
        if (moved.Zone != Zone.Graveyard) return;
        foreach (var (observer, abilities) in Observers().Where(o => o.Card.Controller == moved.Owner).ToList())
            foreach (var ability in abilities.Where(a => a.Trigger == TriggerEvent.CardPutIntoYourGraveyard && (a.FromZone is not { } fromZone || fromZone == from)))
                if (Matches((ability.Filter ?? ObjectFilter.Anything) with { Controller = ControllerFilter.Any }, moved, moved.Owner, observer, observer.Controller))
                    AddPending(observer.Id, ability, observer.Controller, new TriggerInfo(moved.Id, moved.Version, moved.Owner));
    }

    private void Queue(CardId source, TriggerEvent trigger, PlayerId controller, TriggerInfo? info = null)
    {
        foreach (var ability in TriggerAbilitiesOf(State.GetCard(source)))
            if (ability.Trigger == trigger) AddPending(source, ability, controller, info);
    }

    /// <summary>
    /// Permanents on the battlefield when a group of simultaneous events began (several creatures dying at once):
    /// leaves-the-battlefield triggers look back in time, so those that left in the same event still see each other (rule 603.10a).
    /// </summary>
    private List<CardId>? _lookBack;

    /// <summary>Nesting of simultaneous groups: an effect inside a larger event ("a counter and a lifelink counter on each") stays part of it.</summary>
    private int _simultaneousDepth;

    private void BeginSimultaneous()
    {
        _lookBack ??= State.Battlefield.ToList();
        _simultaneousDepth++;
    }

    private void EndSimultaneous()
    {
        if (--_simultaneousDepth > 0) return;
        _simultaneousDepth = 0;
        _lookBack = null;
        _batchTriggered.Clear();
    }

    /// <summary>
    /// "Whenever a creature card leaves a graveyard" looks back at the card as it was there (rule 603.10a): a card in a
    /// graveyard is its owner's, face up, with its printed front face, whatever it became where it went.
    /// </summary>
    private static void AsInGraveyard(Card card, Action<Card> look)
    {
        var saved = (card.Zone, card.FaceDown, card.Transformed, card.TypesOverride, card.GrantedTypes, card.CopiedDefinition, card.EffectCopy, card.Controller);
        try
        {
            (card.Zone, card.FaceDown, card.Transformed, card.TypesOverride, card.GrantedTypes, card.CopiedDefinition, card.EffectCopy, card.Controller) =
                (Zone.Graveyard, false, false, null, 0, null, null, card.Owner);
            look(card);
        }
        finally
        {
            (card.Zone, card.FaceDown, card.Transformed, card.TypesOverride, card.GrantedTypes, card.CopiedDefinition, card.EffectCopy, card.Controller) = saved;
        }
    }

    /// <summary>Triggers of permanents watching for an event that happened to <paramref name="subject"/> ("whenever another creature you control enters").</summary>
    private void QueueObservers(TriggerEvent trigger, Card subject, PlayerId subjectController, TriggerInfo? info = null)
    {
        info ??= new TriggerInfo(subject.Id, subject.Version, subjectController);
        // A creature that died is judged as it last existed on the battlefield (rule 603.10a).
        bool lastKnown = trigger is TriggerEvent.CreatureDies or TriggerEvent.PermanentDies or TriggerEvent.PermanentLeaves or TriggerEvent.CreatureDiesOrExiled;
        foreach (var (observer, abilities) in Observers())
        {
            // "While this card is in your graveyard": a card that got there in this same event (the dying creature itself, or one leaving the
            // battlefield with it) wasn't in the graveyard when it happened (rule 603.10a looks back in time).
            if (lastKnown && observer.Zone == Zone.Graveyard && (observer.Id == subject.Id || _lookBack?.Contains(observer.Id) == true)) continue;
            foreach (var ability in abilities)
                if (ability.Trigger == trigger && Matches(ability.Filter ?? DefaultFilter(trigger), subject, subjectController, observer, observer.Controller, lastKnown))
                    AddPending(observer.Id, ability, observer.Controller, info);
        }
        // Permanents that left the battlefield at the same time still see this one leave (look back in time).
        if (lastKnown && _lookBack is not null)
            foreach (var id in _lookBack.Where(id => id != subject.Id))
            {
                var gone = State.GetCard(id);
                if (gone.Zone == Zone.Battlefield || gone.LastKnownInfo is not { } lki) continue;
                foreach (var ability in lki.Abilities.OfType<TriggeredAbility>().Where(a => a.Trigger == trigger && !a.FromGraveyard))
                    if (Matches(ability.Filter ?? DefaultFilter(trigger), subject, subjectController, gone, lki.Controller, lastKnown))
                        AddPending(id, ability, lki.Controller, info);
            }
        // A creature watching for deaths sees its own death too ("whenever this or another creature you control dies").
        if (lastKnown && subject.Zone == Zone.Graveyard)
            foreach (var ability in (subject.LastKnownInfo?.Abilities ?? subject.Definition.Abilities).OfType<TriggeredAbility>())
                if (ability.Trigger == trigger && ability.Filter is { Other: false } f && Matches(f, subject, subjectController, null, subjectController, lastKnown))
                    AddPending(subject.Id, ability, subjectController, info);
    }

    /// <summary>
    /// A permanent's controller; once it has left the battlefield, its controller as it last existed there (rule 608.2h),
    /// so "its controller creates a token" after destroying a stolen permanent is the player who controlled it.
    /// </summary>
    private static PlayerId ControllerOrLastKnown(Card card, int? chosenVersion) =>
        chosenVersion is { } v && v != card.Version && card.Zone != Zone.Battlefield && card.LastKnownInfo is { } lki ? lki.Controller : card.Controller;

    /// <summary>Spells the ability's controller cast this turn before the spell that triggered it.</summary>
    private IEnumerable<Card> SpellsCastBefore(EffectContext ctx)
    {
        var cast = State.GetPlayer(ctx.Controller).SpellsCastThisTurn;
        int index = ctx.Trigger?.Subject is { } spell ? cast.LastIndexOf(spell) : -1;
        return (index < 0 ? cast : cast.Take(index)).Select(State.GetCard);
    }

    /// <summary>A card that left the battlefield was a creature there (an animated land dies; a creature turned into a land doesn't).</summary>
    private static bool WasCreature(Card card) => card.LastKnownInfo is { } lki ? (lki.Types & CardType.Creature) != 0 : card.IsCreature;

    /// <summary>What an observing trigger watches when its script gives no filter: lands for landfall, otherwise creatures you control.</summary>
    private static ObjectFilter DefaultFilter(TriggerEvent trigger) => trigger switch
    {
        TriggerEvent.LandEnters => new ObjectFilter(CardType.Land),
        TriggerEvent.PermanentEnters or TriggerEvent.LeavesGraveyard or TriggerEvent.ExiledFromLibraryOrGraveyard or TriggerEvent.PermanentDies or TriggerEvent.PermanentLeaves or TriggerEvent.YourPermanentTurnedFaceUp => new ObjectFilter(),
        _ => ObjectFilter.YourCreatures,
    };

    /// <summary>
    /// A player's simultaneous triggered abilities go on the stack in the order that player chooses (rule 603.3b); the
    /// last one put on the stack resolves first. Identical abilities of the same source need no choice.
    /// </summary>
    private async Task<List<PendingTrigger>> OrderTriggersAsync(PlayerId player, List<PendingTrigger> triggers)
    {
        var ordered = new List<PendingTrigger>();
        var left = triggers.ToList();
        while (left.Select(t => (t.Source, t.Ability)).Distinct().Count() > 1)
        {
            var labels = left.Select(t => $"{State.GetCard(t.Source).Name}: {t.Ability.Text}").ToList();
            int pick = await ControllerOf(player).ChooseOptionAsync(ViewFor(player),
                new OptionRequest("Put which triggered ability on the stack next? (the last one put there resolves first)", left[0].Source, labels, OptionKind.Other));
            var chosen = left[Math.Clamp(pick, 0, left.Count - 1)];
            ordered.Add(chosen);
            left.Remove(chosen);
        }
        ordered.AddRange(left);
        return ordered;
    }

    /// <summary>An ability with an intervening "if" clause triggers only if the condition holds now (rule 603.4).</summary>
    private void AddPending(CardId source, TriggeredAbility ability, PlayerId controller, TriggerInfo? info = null)
    {
        if (ability.Condition is { } condition && !HoldsAtTrigger(condition, controller, State.GetCard(source), info)) return;
        if (ability.TriggerCondition is { } when && !HoldsAtTrigger(when, controller, State.GetCard(source), info)) return;
        if (ability.NthOfTurn is { } nth && NthOfTurnFor(ability, State.GetCard(source), controller, info) != nth) return;
        if (ability.ExceptFirstInDrawStep && info?.Player is { } drawer && State.Step == Step.Draw && State.ActivePlayer == drawer
            && State.GetPlayer(drawer).CardsDrawnThisTurn == 1) return; // the first card drawn in their own draw step
        if (ability.Batched && _lookBack is not null && !_batchTriggered.Add((source, ability, ability.BatchedPerPlayer ? info?.Player : null)))
        {
            // "One or more": once per simultaneous event; it still learns every kind of counter put on ("those kinds").
            if (info?.CounterKinds is { } kinds && _pendingTriggers.LastOrDefault(p => p.Source == source && p.Ability == ability)?.Info?.CounterKinds is { } seen)
                seen.AddRange(kinds.Except(seen));
            if (info?.Subject is { } another && _pendingTriggers.LastOrDefault(p => p.Source == source && p.Ability == ability)?.Info?.Batch is { } batch)
                batch.Add((another, info.SubjectVersion));
            if (ability.SumBatchAmounts && info is not null && _pendingTriggers.FindLastIndex(p => p.Source == source && p.Ability == ability) is var at and >= 0
                && _pendingTriggers[at].Info is { } merged)
                _pendingTriggers[at] = _pendingTriggers[at] with { Info = merged with { Amount = merged.Amount + info.Amount } };
            return;
        }
        if (ability.Batched && info?.Subject is { } firstOfBatch) info = info with { Batch = new() { (firstOfBatch, info.SubjectVersion) } };
        if (ability.OncePerTurn && !State.GetCard(source).TriggeredThisTurn.Add(ability)) return;
        if (ability.OnlyOnce && !State.GetCard(source).TriggeredEver.Add(ability)) return;
        var pending = new PendingTrigger(source, ability, controller, info) { SourceVersion = State.GetCard(source).Version };
        _pendingTriggers.Add(pending);
        // "If a triggered ability of [this] triggers, that ability triggers an additional time."
        for (int i = ExtraTriggersFor(State.GetCard(source)) + ExtraTriggersFromMoving(State.GetCard(source), ability, controller, info)
                     + ExtraTriggersFromLandsAndBirds(State.GetCard(source), ability, info) + ExtraTriggersFromDying(State.GetCard(source), ability, info)
                     + ExtraTriggersFromAttack(State.GetCard(source), ability, info); i > 0; i--)
            _pendingTriggers.Add(pending);
    }

    /// <summary>Abilities that trigger an additional time for each static ability saying so that affects their source.</summary>
    private readonly HashSet<(CardId, TriggeredAbility, PlayerId?)> _batchTriggered = new();

    /// <summary>Whether a "everything is the chosen color too" effect applied at the last recompute (to clear it once it ends).</summary>
    private bool _everythingColored;

    /// <summary>
    /// "If a legendary permanent or an artifact entering or leaving the battlefield causes a triggered ability of a permanent
    /// you control to trigger, that ability triggers an additional time."
    /// </summary>
    private int ExtraTriggersFromMoving(Card source, TriggeredAbility ability, PlayerId controller, TriggerInfo? info)
    {
        bool selfEvent = ability.Trigger is TriggerEvent.EntersBattlefield or TriggerEvent.LeavesBattlefield or TriggerEvent.LeavesWithoutDying or TriggerEvent.Dies or TriggerEvent.PutIntoGraveyard or TriggerEvent.DiesOrExiled;
        bool observedEvent = ability.Trigger is TriggerEvent.CreatureEnters or TriggerEvent.PermanentEnters or TriggerEvent.LandEnters or TriggerEvent.CreatureDies;
        if (!selfEvent && !observedEvent) return 0;
        var mover = selfEvent ? source : info?.Subject is { } subject ? State.GetCard(subject) : null;
        if (mover is null) return 0;
        bool left = mover.Zone != Zone.Battlefield;
        var types = left && mover.LastKnownInfo is { } lk ? lk.Types : mover.Types;
        var supertypes = left && mover.LastKnownInfo is { } lk2 ? lk2.Supertypes : mover.Supertypes;
        if ((supertypes & Supertype.Legendary) == 0 && (types & CardType.Artifact) == 0) return 0;
        // "A permanent you control": the source (as it last existed, if this made it leave).
        var owner = source.Zone == Zone.Battlefield ? source.Controller : source.LastKnownInfo?.Controller ?? controller;
        return State.PermanentsControlledBy(owner).Count(c => (c.Replaces & Replacements.ExtraTriggersFromLegendariesAndArtifactsMoving) != 0)
               + (source.Zone != Zone.Battlefield && (source.Definition.Replaces & Replacements.ExtraTriggersFromLegendariesAndArtifactsMoving) != 0
                  && source.LastKnownInfo is { HasTextAbilities: true } gone && gone.Controller == owner ? 1 : 0);
    }

    /// <summary>Additional times a permanent's ability triggers when its controller casts or copies an instant or sorcery spell (one per such permanent).</summary>
    private int InstantSorceryExtraTriggers(Card observer, Card spell, PlayerId caster) =>
        observer.Zone == Zone.Battlefield && observer.Controller == caster && (spell.Is(CardType.Instant) || spell.Is(CardType.Sorcery))
            ? State.PermanentsControlledBy(caster).Count(p => p.HasTextAbilities && p.Definition.InstantSorceryTriggersTwice) : 0;

    private int ExtraTriggersFor(Card source)
    {
        if (source.Zone != Zone.Battlefield) return 0;
        var battlefield = State.Battlefield.Select(State.GetCard).ToList();
        int extra = 0;
        foreach (var permanent in battlefield)
            foreach (var st in permanent.Abilities.OfType<StaticAbility>().Where(a => a.ExtraTriggers))
                if ((st.While is not { } cond || Holds(cond, permanent.Controller, permanent))
                    && (Affected(permanent, st.Affects, battlefield).Any(c => c.Id == source.Id
                        && (st.Filter is not { } f || Matches(f with { Controller = ControllerFilter.Any }, c, c.Controller, permanent, permanent.Controller)))
                        || (st.ExtraTriggersAttached && source.AttachedTo == permanent.Id && source.HasSubtype("Equipment"))))
                    extra++;
        return extra;
    }

    private int NthOfTurn(TriggerEvent trigger, PlayerId player) => trigger switch
    {
        TriggerEvent.YouDrawCard => State.GetPlayer(player).CardsDrawnThisTurn,
        TriggerEvent.YouGainLife => State.GetPlayer(player).LifeGainsThisTurn,
        _ => 1,
    };

    private int NthOfTurnFor(TriggeredAbility ability, Card source, PlayerId controller, TriggerInfo? info) => ability.Trigger switch
    {
        TriggerEvent.Attacks => source.AttacksThisTurn,
        // Valiant: "for the first time each turn" counts the times this object became the target of that controller's spells and abilities.
        TriggerEvent.BecomesTargetOfYours when info is { Subject: { } aimedAt, Player: { } by }
            => State.TargetedThisTurn.Count(t => t.Card == aimedAt && t.Version == info.SubjectVersion && t.By == by && t.Turn == State.TurnNumber),
        // "Whenever an opponent draws their second card each turn".
        TriggerEvent.OpponentDrawsCard when info?.Player is { } drawer => State.GetPlayer(drawer).CardsDrawnThisTurn,
        // "Whenever an opponent casts their first noncreature spell each turn": that player's matching spells this turn, this one included.
        TriggerEvent.OpponentCastsSpell or TriggerEvent.YouCastSpell or TriggerEvent.AnyPlayerCastsSpell when info?.Player is { } caster
            => State.GetPlayer(caster).SpellsCastThisTurn.Select(State.GetCard)
                .Count(c => Matches((ability.Filter ?? new ObjectFilter()) with { Controller = ControllerFilter.Any }, c, caster, source, controller)),
        _ => NthOfTurn(ability.Trigger, controller),
    };

    /// <summary>
    /// Permanents entering the battlefield at the same time (several tokens, "put any number onto the battlefield"):
    /// their enter triggers wait until all of them are there, so they see each other enter (rule 603.6a).
    /// </summary>
    private List<CardId>? _enteringTogether;
    private int _enteringTogetherDepth;

    private void BeginEnteringTogether()
    {
        _enteringTogether ??= new List<CardId>();
        _enteringTogetherDepth++;
    }

    private void EndEnteringTogether()
    {
        if (--_enteringTogetherDepth > 0) return;
        var entered = _enteringTogether;
        _enteringTogether = null;
        foreach (var id in entered ?? new List<CardId>())
            if (State.GetCard(id).Zone == Zone.Battlefield) QueueEnterTriggers(id);
    }

    /// <summary>The mana types of the colors in a player's commanders' color identity.</summary>
    private List<ManaType> CommanderIdentityTypes(PlayerId player) =>
        State.Cards.Values.Where(c => c.IsCommander && c.Owner == player)
            .SelectMany(c => c.Definition.ColorIdentity.Count > 0 ? c.Definition.ColorIdentity : c.Definition.ColorList).Distinct()
            .Select(c => Mana.ManaTypeExtensions.TryParse(c[0], out var t) ? t : (ManaType?)null).OfType<ManaType>().OrderBy(t => t).ToList();

    private void QueueEnterTriggers(CardId id)
    {
        var entering = State.GetCard(id);
        // "Artifacts and creatures entering the battlefield don't cause abilities to trigger" (no enter trigger of any object).
        if ((entering.IsCreature || entering.Is(CardType.Artifact))
            && State.Battlefield.Select(State.GetCard).Any(c => c.HasTextAbilities && c.Definition.EnteringArtifactsAndCreaturesDontTrigger)) return;
        // A permanent spell's promised gift: "when it enters, [they get the gift]" (rule 702.174).
        if (entering.GiftPromised && entering.GiftTo is not null) _pendingTriggers.Add(new PendingTrigger(id, GiftOnEnter, entering.Controller));
        Queue(id, TriggerEvent.EntersBattlefield, entering.Controller);
        // Evoke gained as it was cast: its sacrifice trigger, which the card itself doesn't have.
        if (entering.Evoked && entering.Definition.Evoke is null) AddPending(id, GrantedEvokeSacrifice, entering.Controller);
        if (entering.IsCreature) QueueObservers(TriggerEvent.CreatureEnters, entering, entering.Controller);
        if (entering.IsCreature) QueueEvolve(entering);
        QueueObservers(TriggerEvent.PermanentEnters, entering, entering.Controller);
        if (entering.Is(CardType.Land)) QueueObservers(TriggerEvent.LandEnters, entering, entering.Controller);
    }

    /// <summary>
    /// Combat damage dealt in the current damage step, by source: "whenever this deals combat damage" triggers once per
    /// creature per step, with the total it dealt (rule 510.2: all combat damage is dealt at the same time).
    /// </summary>
    private Dictionary<CardId, (int Amount, PlayerId? Player)>? _combatDamageDealt;

    /// <summary>Players dealt combat damage in the current damage step ("whenever one or more creatures deal combat damage to you").</summary>
    private readonly HashSet<PlayerId> _hurtThisStep = new();

    /// <summary>The creatures that dealt combat damage to each player in the current damage step ("those creatures").</summary>
    private readonly Dictionary<PlayerId, List<(CardId Card, int Version)>> _combatDamagersThisStep = new();

    private void NoteCombatDamage(Card source, PlayerId? player, int amount)
    {
        if (player is { } hurt)
        {
            if (!_combatDamagersThisStep.TryGetValue(hurt, out var damagers)) _combatDamagersThisStep[hurt] = damagers = new();
            if (!damagers.Contains((source.Id, source.Version))) damagers.Add((source.Id, source.Version));
            if (_combatDamageDealt is null) { QueueOpponentsDealtCombatDamage(new[] { hurt }); QueueCombatDamageToYou(hurt); _combatDamagersThisStep.Remove(hurt); }
            else _hurtThisStep.Add(hurt);
            // The Ring, level 4: "Whenever your Ring-bearer deals combat damage to a player, each opponent loses 3 life."
            if (IsRingBearer(source, 4)) _pendingTriggers.Add(new PendingTrigger(source.Id, RingDrain, source.Controller));
        }
        if (_combatDamageDealt is null)
        {
            QueueCombatDamageTriggers(source, player, amount);
            return;
        }
        var (total, firstPlayer) = _combatDamageDealt.GetValueOrDefault(source.Id);
        _combatDamageDealt[source.Id] = (total + amount, firstPlayer ?? player);
    }

    private void BeginCombatDamage() => _combatDamageDealt = new();

    private void QueueCombatDamageToYou(PlayerId hurt)
    {
        // "Those creatures" (subject "batch"): the ones that dealt the damage.
        var damagers = _combatDamagersThisStep.GetValueOrDefault(hurt)?.ToList() ?? new();
        foreach (var card in State.PermanentsControlledBy(hurt).ToList())
            Queue(card.Id, TriggerEvent.CombatDamageToYou, hurt, new TriggerInfo(Player: hurt) { Batch = damagers.ToList() });
    }

    /// <summary>"When one or more of your opponents are dealt combat damage during your turn": the active player's permanents, once.</summary>
    private void QueueOpponentsDealtCombatDamage(IEnumerable<PlayerId> hurt)
    {
        // "Whenever your opponents are dealt combat damage": each player with a hurt opponent, the batch being who hurt them.
        foreach (var watcher in State.Players.Where(p => !p.HasLost).Select(p => p.Id).ToList())
        {
            var hurtOpponents = hurt.Where(State.OpponentsOf(watcher).Contains).ToList();
            if (hurtOpponents.Count == 0) continue;
            var damagers = hurtOpponents.SelectMany(h => _combatDamagersThisStep.GetValueOrDefault(h) ?? new()).Distinct().ToList();
            foreach (var card in State.PermanentsControlledBy(watcher).ToList())
                Queue(card.Id, TriggerEvent.OpponentsDealtCombatDamage, watcher, new TriggerInfo(Player: watcher) { Batch = damagers.ToList() });
        }
        if (!hurt.Any(State.OpponentsOf(State.ActivePlayer).Contains)) return;
        foreach (var card in State.PermanentsControlledBy(State.ActivePlayer).ToList()) Queue(card.Id, TriggerEvent.OpponentsDealtCombatDamageDuringYourTurn, State.ActivePlayer);
    }

    private void EndCombatDamage()
    {
        QueueOpponentsDealtCombatDamage(_hurtThisStep);
        foreach (var hurt in _hurtThisStep) QueueCombatDamageToYou(hurt);
        _hurtThisStep.Clear();
        _combatDamagersThisStep.Clear();
        var dealt = _combatDamageDealt;
        _combatDamageDealt = null;
        if (dealt is null) return;
        foreach (var (source, (amount, player)) in dealt) QueueCombatDamageTriggers(State.GetCard(source), player, amount);
    }

    private void QueueCombatDamageTriggers(Card source, PlayerId? player, int amount)
    {
        var info = new TriggerInfo(source.Id, source.Version, player, amount);
        Queue(source.Id, TriggerEvent.DealsCombatDamage, source.Controller, info);
        QueueObservers(TriggerEvent.CreatureDealsCombatDamage, source, source.Controller, info);
    }

    /// <summary>A "copy that spell" trigger of <paramref name="source"/> for a spell just cast.</summary>
    private void QueueCopyThatSpell(CardId source, PlayerId controller, Card spell) =>
        _pendingTriggers.Add(new PendingTrigger(source, CopyThatSpell, controller, new TriggerInfo(spell.Id, spell.Version, controller)));

    private static readonly TriggeredAbility CopyThatSpell = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new CopySpell(Subject.Triggered, 1) },
        Text = "Copy that spell. You may choose new targets for the copy.",
    };

    private static readonly TriggeredAbility CopyThatSpellPerCommanderCast = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new CopySpell(Subject.Triggered, new Quantity(0, QuantityKind.CommanderCasts)) },
        Text = "Copy that spell for each time you've cast your commander from the command zone this game. You may choose new targets for the copies.",
    };

    private static readonly TriggeredAbility CopyThatSpellTwice = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new CopySpell(Subject.Triggered, 2) },
        Text = "Copy that spell twice. You may choose new targets for the copies.",
    };

    private static readonly TriggeredAbility ReturnAtEndStep = new()
    {
        Trigger = TriggerEvent.EachEndStep,
        Effects = new Effect[] { new PutOntoBattlefield(Subject.Triggered) },
        Text = "Return it to the battlefield.",
    };

    private static readonly TriggeredAbility SacrificeAtEndStep = new()
    {
        Trigger = TriggerEvent.EachEndStep,
        Effects = new Effect[] { new SacrificeIt(Subject.Triggered) },
        Text = "Sacrifice it at the beginning of the end step.",
    };

    private static readonly TriggeredAbility SacrificeThisAtEndStep = new()
    {
        Trigger = TriggerEvent.EachEndStep,
        Effects = new Effect[] { new SacrificeIt(Subject.Self) },
        Text = "At the beginning of the end step, sacrifice this token.",
    };

    private static readonly TriggeredAbility RingLoot = new()
    {
        Trigger = TriggerEvent.Attacks,
        Effects = new Effect[] { new DrawCards(1, Subject.You), new Discard(1, Subject.You) },
        Text = "The Ring: your Ring-bearer attacks — draw a card, then discard a card.",
    };

    private static readonly TriggeredAbility GiftOnEnter = new()
    {
        Trigger = TriggerEvent.EntersBattlefield,
        Effects = new Effect[] { new GivePromisedGift() },
        Text = "Gift: the opponent who was promised the gift gets it.",
    };

    /// <summary>The promised gift (rule 702.174): a card to draw or a token, for an opponent still in the game.</summary>
    private async Task GiveGiftAsync(PlayerId giftee, Card source)
    {
        if (State.GetPlayer(giftee).HasLost) return;
        if (source.Definition.GiftCard) await DrawAsync(giftee);
        else if (source.Definition.GiftExtraTurn) State.ExtraTurns.Add(giftee); // "takes an extra turn after this one" (rule 500.7)
        else if (source.Definition.Gift is { } gift) await CreateTokenEventAsync(giftee, gift, 1, source.Definition.GiftTapped, source);
        Emit(new GiftGiven(source.Controller, giftee, source.Id));
    }

    private static readonly TriggeredAbility RingDrain = new()
    {
        Trigger = TriggerEvent.DealsCombatDamageToPlayer,
        Effects = new Effect[] { new LoseLife(3, Subject.EachOpponent) },
        Text = "The Ring: your Ring-bearer dealt combat damage to a player — each opponent loses 3 life.",
    };

    private static readonly TriggeredAbility CascadeTrigger = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new CascadeEffect() },
        Text = "Cascade",
    };

    private static readonly TriggeredAbility MeleeTrigger = new()
    {
        Trigger = TriggerEvent.Attacks,
        Effects = new Effect[]
        {
            new PumpUntilEndOfTurn(new Quantity(0, QuantityKind.OpponentsAttackedThisCombat), new Quantity(0, QuantityKind.OpponentsAttackedThisCombat), Subject.Self),
        },
        Text = "Melee: this creature gets +1/+1 until end of turn for each opponent you attacked with a creature this combat.",
    };

    /// <summary>Paradigm's delayed ability (rule 702.192a), for the spell it copies.</summary>
    private static TriggeredAbility ParadigmAbility(CardDefinition spell) => new()
    {
        Trigger = TriggerEvent.YourPrecombatMain,
        Effects = new Effect[] { new CastParadigmCopy(spell) },
        Text = $"Paradigm — At the beginning of your precombat main phase, create a copy of {spell.Name} in exile. You may cast the copy without paying its mana cost.",
    };

    private static readonly TriggeredAbility ExtortTrigger = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[]
        {
            new MayPay("Pay {W/B} to extort (each opponent loses 1 life and you gain that much)?", Mana.ManaCost.Parse("{W/B}"), null,
                new Effect[] { new LoseLife(1, new Subject(SubjectKind.EachOpponent)), new GainLife(new Quantity(0, QuantityKind.LifeLostThisWay), new Subject(SubjectKind.You)) }),
        },
        Text = "Extort: you may pay {W/B}. If you do, each opponent loses 1 life and you gain life equal to the total life lost this way.",
    };

    private static readonly TriggeredAbility ExaltedTrigger = new()
    {
        Trigger = TriggerEvent.YouAttack,
        Effects = new Effect[] { new PumpUntilEndOfTurn(1, 1, Subject.Triggered) },
        Text = "Exalted: the creature attacking alone gets +1/+1 until end of turn.",
    };

    private static readonly TriggeredAbility VanishingUpkeep = new()
    {
        Trigger = TriggerEvent.YourUpkeep,
        Effects = new Effect[] { new RemoveCounters(1, Subject.Self, CounterKind.Time) },
        Text = "Vanishing: at the beginning of your upkeep, remove a time counter from this permanent.",
    };

    private static readonly TriggeredAbility ImpendingEndStep = new()
    {
        Trigger = TriggerEvent.YourEndStep,
        Effects = new Effect[] { new RemoveCounters(1, Subject.Self, CounterKind.Time) },
        Text = "Impending: at the beginning of your end step, remove a time counter from this permanent.",
    };

    private static readonly TriggeredAbility VanishingSacrifice = new()
    {
        Trigger = TriggerEvent.YourUpkeep,
        Effects = new Effect[] { new SacrificeIt(Subject.Self) },
        Text = "Vanishing: the last time counter was removed — sacrifice this permanent.",
    };

    private static readonly TriggeredAbility SuspendUpkeep = new()
    {
        Trigger = TriggerEvent.YourUpkeep,
        Effects = new Effect[] { new SuspendTick() },
        Text = "Suspend: at the beginning of your upkeep, remove a time counter from this card.",
    };

    private static readonly TriggeredAbility SuspendPlay = new()
    {
        Trigger = TriggerEvent.YourUpkeep,
        Effects = new Effect[] { new PlaySuspendedFree() },
        Text = "Suspend: the last time counter was removed — you may play it without paying its mana cost.",
    };

    private static readonly TriggeredAbility ProwessTrigger = new()
    {
        Trigger = TriggerEvent.YouCastSpell,
        Effects = new Effect[] { new PumpUntilEndOfTurn(1, 1, Subject.Self) },
        Text = "Prowess",
    };

    /// <summary>
    /// Puts waiting triggered abilities on the stack in APNAP order (603.3b), choosing their targets. A trigger
    /// with no legal targets is removed (603.3d). Returns true if anything was put on the stack.
    /// </summary>
    private async Task<bool> PutPendingTriggersOnStackAsync()
    {
        // State triggers (rule 603.8) trigger as soon as their condition is true, and not again while one waits or is on the stack.
        foreach (var watcher in State.Battlefield.Select(State.GetCard).ToList())
            foreach (var ability in TriggerAbilitiesOf(watcher).Where(a => a.Trigger == TriggerEvent.StateTrigger && a.TriggerCondition is not null))
                if (!_pendingTriggers.Any(p => p.Source == watcher.Id && p.Ability.Text == ability.Text)
                    && !State.Stack.OfType<AbilityOnStack>().Any(s => s.Source == watcher.Id && s.Ability is TriggeredAbility { Trigger: TriggerEvent.StateTrigger } t && t.Text == ability.Text))
                    AddPending(watcher.Id, ability, watcher.Controller);
        if (_pendingTriggers.Count == 0) return false;
        var pending = _pendingTriggers.ToList();
        _pendingTriggers.Clear();
        bool any = false;
        foreach (var player in State.ApnapOrder().ToList())
        {
            foreach (var trigger in await OrderTriggersAsync(player, pending.Where(t => t.Controller == player).ToList()))
            {
                _triggeredPlayer = trigger.Info?.Player;
                _triggeredAmount = trigger.Info?.Amount ?? 0;
                _triggeredSubject = trigger.Info?.Subject;
                _triggeredSubjectVersion = trigger.Info?.SubjectVersion ?? 0;
                _triggeredDefender = trigger.Info?.Defender;
                _triggeredFound = trigger.Info?.Found;
                if (!HasLegalTargets(trigger.Ability, player, trigger.Source))
                {
                    Emit(new TriggerRemovedNoTargets(player, trigger.Source, trigger.Ability.Text));
                    continue;
                }
                // Miracle: revealing the card is optional; the ability triggers only if it's revealed (702.94a).
                if (ReferenceEquals(trigger.Ability, MiracleTrigger))
                {
                    var miracle = State.GetCard(trigger.Source);
                    if (miracle.Zone != Zone.Hand || miracle.Version != trigger.Info?.SubjectVersion
                        || !await ControllerOf(player).ChooseYesNoAsync(ViewFor(player), new YesNoRequest($"Reveal {miracle.Name} for its miracle cost?", miracle.Id))) continue;
                    Emit(new CardsRevealed(player, new[] { miracle.Id }));
                }
                var ability = await ChooseModesAsync(player, trigger.Ability, trigger.Source, canCancel: false);
                if (ability is null) continue;
                // "When this enters, tap X target creatures": X is the one paid as it was cast.
                ability = WithRepeatedTargets(ability, player, State.GetCard(trigger.Source), trigger.Info,
                    trigger.Ability.Trigger == TriggerEvent.EntersBattlefield ? State.GetCard(trigger.Source).CastX : 0);
                _triggeredPlayer = trigger.Info?.Player;
                var targets = (await ChooseTargetsAsync(player, ability, trigger.Source, ability.Text, canCancel: false))!;
                var division = await ChooseDivisionAsync(player, ability, trigger.Source, targets); // rule 603.3d
                Emit(new AbilityTriggered(player, trigger.Source, ability.Text));
                any = true;
                PushStack(new AbilityOnStack(trigger.Source, ability, player, targets)
                {
                    Trigger = trigger.Info, SourceVersion = trigger.SourceVersion >= 0 ? trigger.SourceVersion : State.GetCard(trigger.Source).Version,
                    Division = division, SourceTransforms = trigger.SourceTransforms ?? State.GetCard(trigger.Source).TransformCount,
                });
            }
        }
        return any;
    }
}
