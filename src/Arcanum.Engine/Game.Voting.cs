// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine;

/// <summary>Voting (rule 701.38): will of the council, council's dilemma and secret council.</summary>
public sealed partial class Game
{
    /// <summary>
    /// Each player votes, starting with the ability's controller and going in turn order. Open votes are announced as they are
    /// made; secret votes are revealed together once everyone has voted (rule 701.38c).
    /// </summary>
    private async Task VoteAsync(Vote vote, EffectContext ctx)
    {
        var voters = new List<PlayerId>();
        for (int i = 0; i < State.Players.Count; i++)
        {
            var p = State.Players[(ctx.Controller.Value + i) % State.Players.Count];
            if (!p.HasLost) voters.Add(p.Id);
        }
        var votes = new List<(PlayerId Voter, string Choice)>();
        var labels = new List<string>();
        foreach (var voter in voters)
        {
            string? choice = null;
            string label = "";
            switch (vote.For)
            {
                case VoteFor.Options:
                {
                    int pick = await ControllerOf(voter).ChooseOptionAsync(ViewFor(voter),
                        new OptionRequest($"{ctx.Source.Name}: vote{(vote.Secret ? " (secretly)" : "")}", ctx.Source.Id, vote.Options, OptionKind.Other));
                    Require(pick >= 0 && pick < vote.Options.Count, "Vote for one of the options.");
                    choice = pick.ToString();
                    label = vote.Options[pick];
                    break;
                }
                case VoteFor.Player:
                {
                    var players = State.LivingPlayers.Select(p => p.Id).ToList();
                    int pick = await ControllerOf(voter).ChooseOptionAsync(ViewFor(voter),
                        new OptionRequest($"{ctx.Source.Name}: vote for a player{(vote.Secret ? " (secretly)" : "")}", ctx.Source.Id,
                            players.Select(p => State.GetPlayer(p).Name).ToList(), OptionKind.Other));
                    Require(pick >= 0 && pick < players.Count, "Vote for one of the players.");
                    choice = $"p{players[pick].Value}";
                    label = State.GetPlayer(players[pick]).Name;
                    break;
                }
                case VoteFor.Creature:
                {
                    var filter = (vote.CreatureFilter ?? new ObjectFilter(CardType.Creature, Controller: ControllerFilter.Any));
                    var options = State.Battlefield.Select(State.GetCard).Where(c => c.IsCreature && Matches(filter, c, c.Controller, ctx.Source, ctx.Controller)).ToList();
                    if (options.Count == 0) break;
                    var pick = await ControllerOf(voter).ChooseCardsAsync(ViewFor(voter), new CardChoiceRequest($"{ctx.Source.Name}: vote for a creature{(vote.Secret ? " (secretly)" : "")}",
                        ctx.Source.Id, options.Select(c => ViewBuilder.Card(State, c.Id, voter)).ToList(), 1, 1, CardChoicePurpose.Keep));
                    Require(pick.Count == 1 && options.Any(c => c.Id == pick[0]), "Vote for one of the creatures.");
                    choice = $"c{pick[0].Value}";
                    label = State.GetCard(pick[0]).Name;
                    break;
                }
            }
            if (choice is null) continue;
            votes.Add((voter, choice));
            labels.Add($"{State.GetPlayer(voter).Name} votes for {label}");
            if (!vote.Secret) Emit(new ChoiceMade(ctx.Source.Id, labels[^1]));
        }
        if (vote.Secret) foreach (var line in labels) Emit(new ChoiceMade(ctx.Source.Id, line));
        ctx.Results.Votes.Clear();
        ctx.Results.Votes.AddRange(votes);
        // "Whenever players finish voting".
        var info = new TriggerInfo(Player: ctx.Controller) { Votes = votes };
        foreach (var card in State.Battlefield.Select(State.GetCard).ToList()) Queue(card.Id, TriggerEvent.PlayersFinishVoting, card.Controller, info);
    }

    /// <summary>The votes an effect looks at: those of the vote this spell or ability held, or those its trigger is about.</summary>
    private static IReadOnlyList<(PlayerId Voter, string Choice)> VotesIn(EffectContext ctx) =>
        ctx.Results.Votes.Count > 0 ? ctx.Results.Votes : ctx.Trigger?.Votes ?? Array.Empty<(PlayerId, string)>();

    private static int VotesFor(EffectContext ctx, int option) => VotesIn(ctx).Count(v => v.Choice == option.ToString());

    /// <summary>Opponents who voted for a choice the controller voted for (true) or for a choice the controller didn't vote for (false).</summary>
    private IEnumerable<PlayerId> OpponentsByVote(EffectContext ctx, bool agreeing)
    {
        var votes = VotesIn(ctx);
        var mine = votes.Where(v => v.Voter == ctx.Controller).Select(v => v.Choice).ToHashSet();
        return State.OpponentsOf(ctx.Controller)
            .Where(o => agreeing ? votes.Any(v => v.Voter == o && mine.Contains(v.Choice)) : votes.Any(v => v.Voter == o && !mine.Contains(v.Choice)))
            .ToList();
    }

    private async Task StunVotedCreaturesAsync(EffectContext ctx)
    {
        foreach (var group in VotesIn(ctx).Where(v => v.Choice.StartsWith('c')).GroupBy(v => v.Choice))
        {
            var card = State.GetCard(new CardId(int.Parse(group.Key[1..])));
            if (card.Zone != Zone.Battlefield) continue;
            PutCounters(card, CounterKind.Stun, group.Count(), ctx.Controller);
            await ResolvePendingCountersAsync();
            if (!card.Tapped)
            {
                card.Tapped = true;
                Emit(new PermanentTapped(card.Id));
            }
        }
    }

    private async Task VotersGiveCreaturesAsync(VotersGiveCreatures give, EffectContext ctx)
    {
        var chosen = new List<Card>();
        foreach (var (voter, _) in VotesIn(ctx).Where(v => v.Choice == give.Option.ToString()).ToList())
        {
            var options = State.PermanentsControlledBy(voter).Where(c => c.IsCreature).ToList();
            if (options.Count == 0) continue;
            var pick = await ControllerOf(voter).ChooseCardsAsync(ViewFor(voter), new CardChoiceRequest($"{ctx.Source.Name}: choose a creature you control", ctx.Source.Id,
                options.Select(c => ViewBuilder.Card(State, c.Id, voter)).ToList(), 1, 1, CardChoicePurpose.Keep));
            Require(pick.Count == 1 && options.Any(c => c.Id == pick[0]), "Choose one of your creatures.");
            chosen.Add(State.GetCard(pick[0]));
        }
        long stamp = NewTimestamp();
        foreach (var card in chosen.Distinct())
        {
            State.ControlEffects.Add(new ControlEffect(card.Id, card.Version, ctx.Controller, stamp) { MadeOnTurn = State.TurnNumber });
            State.LastingEffects.Add(new UntilEndOfTurnEffect(card.Id, card.Version, 0, 0, Array.Empty<Keyword>()) { CantAttackOwner = true, Timestamp = stamp });
        }
        RecomputeContinuousEffects();
    }
}
