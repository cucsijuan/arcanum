// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;

namespace Arcanum.UI.Board;

/// <summary>Human-readable lines for the game log. Returns null for events too noisy to show.</summary>
public static class EventLogFormatter
{
    public static string? Format(Game game, GameEvent e, bool revealAll)
    {
        string P(Arcanum.Engine.Core.PlayerId id) => game.State.GetPlayer(id).Name;
        string C(Arcanum.Engine.Core.CardId id) => game.State.GetCard(id).Name;

        return e switch
        {
            GameStarted s => $"{P(s.StartingPlayer)} goes first.",
            MulliganTaken m => $"{P(m.Player)} mulligans ({m.Count}).",
            HandKept k => $"{P(k.Player)} keeps {k.HandSize} cards.",
            TurnBegan t => $"— Turn {t.TurnNumber}: {P(t.ActivePlayer)} —",
            CardDrawn d => revealAll ? $"{P(d.Player)} draws {C(d.Card)}." : $"{P(d.Player)} draws a card.",
            LandPlayed l => $"{P(l.Player)} plays {C(l.Card)}.",
            SpellCast c => $"{P(c.Player)} casts {C(c.Card)}.",
            SpellResolved r => $"{C(r.Card)} resolves.",
            AttackerDeclared a => $"{C(a.Attacker)} attacks {P(a.Defender)}.",
            BlockerDeclared b => $"{C(b.Blocker)} blocks {C(b.Attacker)}.",
            DamageDealt { TargetPlayer: { } p } d => $"{C(d.Source)} deals {d.Amount} to {P(p)}.",
            DamageDealt { TargetCard: { } c } d => $"{C(d.Source)} deals {d.Amount} to {C(c)}.",
            CreatureDied d => $"{C(d.Card)} dies.",
            AbilityActivated a => $"{P(a.Player)} activates {C(a.Source)}: {a.Text}",
            AbilityTriggered t => $"{C(t.Source)} triggers: {t.Text}",
            FizzledOnResolution f => $"{C(f.Source)} does nothing (no legal targets).",
            SpellCountered c => $"{C(c.Card)} is countered.",
            TokenCreated t => $"{P(t.Controller)} creates a {C(t.Card)} token.",
            PermanentDestroyed d when !game.State.GetCard(d.Card).IsCreature => $"{C(d.Card)} is destroyed.",
            LifeChanged { NewLife: var now, OldLife: var before } l when now > before => $"{P(l.Player)} gains {now - before} life.",
            PermanentSacrificed sac when !game.State.GetCard(sac.Card).IsCreature => $"{C(sac.Card)} is sacrificed.",
            ControlChanged cc => $"{P(cc.NewController)} gains control of {C(cc.Card)}.",
            ChoiceMade cm => $"{C(cm.Card)}: {cm.Choice} chosen.",
            CardDiscarded d => $"{P(d.Player)} discards {C(d.Card)}.",
            LookedAtTop { Scry: true } l => $"{P(l.Player)} scries {l.Looked} ({l.Moved} to the bottom).",
            LookedAtTop l => $"{P(l.Player)} surveils {l.Looked} ({l.Moved} to the graveyard).",
            PlayerLost l => $"{P(l.Player)} loses ({l.Reason}).",
            GameEnded { Winner: { } w } => $"{P(w)} wins the game!",
            GameEnded => "The game is a draw.",
            _ => null,
        };
    }

    public static string StepName(Step step) => step switch
    {
        Step.Untap => "Untap",
        Step.Upkeep => "Upkeep",
        Step.Draw => "Draw",
        Step.PrecombatMain => "Main 1",
        Step.BeginCombat => "Beginning of Combat",
        Step.DeclareAttackers => "Declare Attackers",
        Step.DeclareBlockers => "Declare Blockers",
        Step.CombatDamage => "Combat Damage",
        Step.EndCombat => "End of Combat",
        Step.PostcombatMain => "Main 2",
        Step.End => "End Step",
        _ => "Cleanup",
    };
}
