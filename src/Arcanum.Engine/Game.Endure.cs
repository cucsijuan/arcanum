// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

/// <summary>Endure (rule 701.63), mobilize (rule 702.181) and fabricate (rule 702.123).</summary>
public sealed partial class Game
{
    /// <summary>The N/N white Spirit creature token of endure (rule 701.63a).</summary>
    private static CardDefinition EndureSpirit(int size) => new()
    {
        Name = "Spirit",
        Types = CardType.Creature,
        Subtypes = new[] { "Spirit" },
        Colors = new[] { "W" },
        Power = size,
        Toughness = size,
        IsToken = true,
    };

    /// <summary>The 1/1 red Warrior creature token of mobilize (rule 702.181a).</summary>
    private static readonly CardDefinition MobilizeWarrior = new()
    {
        Name = "Warrior",
        Types = CardType.Creature,
        Subtypes = new[] { "Warrior" },
        Colors = new[] { "R" },
        Power = 1,
        Toughness = 1,
        IsToken = true,
    };

    /// <summary>
    /// Each permanent endures N: its controller chooses counters or the token. Counters are offered only when they can be put on it;
    /// a permanent that has left the battlefield gives its controller (the ability's controller, who controlled it) the token.
    /// </summary>
    private async Task EndureAsync(Abilities.Endure endure, EffectContext ctx)
    {
        int n = Eval(endure.Count, ctx);
        if (n <= 0) return; // 701.63b
        var enduring = CardsFor(endure.What, ctx).Where(c => c.Zone == Zone.Battlefield).ToList();
        if (enduring.Count == 0 && endure.What.Kind is SubjectKind.Self or SubjectKind.Triggered)
        {
            ctx.Results.Created.AddRange((await CreateTokenEventAsync(ctx.Controller, EndureSpirit(n), 1, false, ctx.Source)).Originals);
            return;
        }
        foreach (var card in enduring)
        {
            var player = card.Controller;
            bool canHaveCounters = !CountersBannedOn(card, null) && !card.Has(Keyword.CantHaveCounters);
            if (canHaveCounters && await ControllerOf(player).ChooseYesNoAsync(ViewFor(player),
                    new YesNoRequest($"{card.Name} endures {n}: put {n} +1/+1 counter{(n == 1 ? "" : "s")} on it? (No: create a {n}/{n} Spirit token.)", card.Id)))
                PutCounters(card, CounterKind.PlusOnePlusOne, n, player);
            else
                ctx.Results.Created.AddRange((await CreateTokenEventAsync(player, EndureSpirit(n), 1, false, ctx.Source)).Originals);
        }
    }

    /// <summary>The 1/1 colorless Servo artifact creature token of fabricate (rule 702.123a).</summary>
    private static readonly CardDefinition FabricateServo = new()
    {
        Name = "Servo",
        Types = CardType.Artifact | CardType.Creature,
        Subtypes = new[] { "Servo" },
        Power = 1,
        Toughness = 1,
        IsToken = true,
    };

    /// <summary>
    /// Fabricate N: the controller chooses the counters or the Servos. Counters are offered only while the permanent is on the battlefield
    /// and can have them; otherwise the Servos are made.
    /// </summary>
    private async Task FabricateAsync(Abilities.Fabricate fabricate, EffectContext ctx)
    {
        int n = fabricate.Count;
        if (n <= 0) return;
        var card = ctx.Source;
        bool canHaveCounters = SourceOnBattlefield(ctx) && !CountersBannedOn(card, null) && !card.Has(Keyword.CantHaveCounters);
        if (canHaveCounters && await ControllerOf(ctx.Controller).ChooseYesNoAsync(ViewFor(ctx.Controller),
                new YesNoRequest($"Fabricate {n}: put {n} +1/+1 counter{(n == 1 ? "" : "s")} on {card.Name}? (No: create {n} 1/1 Servo token{(n == 1 ? "" : "s")}.)", card.Id)))
            PutCounters(card, CounterKind.PlusOnePlusOne, n, ctx.Controller);
        else
            ctx.Results.Created.AddRange((await CreateTokenEventAsync(ctx.Controller, FabricateServo, n, false, ctx.Source)).Originals);
    }

    /// <summary>The Warrior tokens enter tapped and attacking (the controller chooses what each attacks, rule 508.4) and are sacrificed at the next end step.</summary>
    private async Task MobilizeAsync(Abilities.Mobilize mobilize, EffectContext ctx)
    {
        int before = ctx.Results.Created.Count;
        await ApplyAsync(new CreateTokens(MobilizeWarrior, mobilize.Count, Subject.You, Tapped: true) { Attacking = true, AttackingChosenDefender = true }, ctx);
        foreach (var token in ctx.Results.Created.Skip(before).Select(State.GetCard).Where(c => c.Zone == Zone.Battlefield).ToList())
            State.AtNextEndStepEffects.Add((token.Id, ctx.Controller, token.Id, token.Version, new Effect[] { new SacrificeIt(Subject.Triggered) }, null, null));
    }
}
