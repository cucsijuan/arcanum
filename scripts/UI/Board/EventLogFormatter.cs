// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.UI.Board;

/// <summary>Human-readable lines for the game log. Returns null for events too noisy to show.</summary>
public static class EventLogFormatter
{
    /// <param name="ev">The event with the cards it mentions, as this screen may see them.</param>
    public static string? Format(EventView ev, Func<PlayerId, string> playerName, bool revealAll)
    {
        string P(PlayerId id) => playerName(id);
        string C(CardId id) => ev.Name(id);
        bool IsCreature(CardId id) => ev.Card(id) is { } c && (c.Types & Arcanum.Engine.Cards.CardType.Creature) != 0;

        return ev.Event switch
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
            PermanentDestroyed d when !IsCreature(d.Card) => $"{C(d.Card)} is destroyed.",
            LifeChanged { NewLife: var now, OldLife: var before } l when now > before => $"{P(l.Player)} gains {now - before} life.",
            PermanentSacrificed sac when !IsCreature(sac.Card) => $"{C(sac.Card)} is sacrificed.",
            ControlChanged cc => $"{P(cc.NewController)} gains control of {C(cc.Card)}.",
            ChoiceMade cm => $"{C(cm.Card)}: {cm.Choice} chosen.",
            EnduringStoryGained es => $"{P(es.Player)} has an enduring story.",
            CitysBlessingGained cb => $"{P(cb.Player)} gets the city's blessing.",
            MonarchChanged mc => $"{P(mc.Player)} becomes the monarch.",
            RingTempted rt => $"The Ring tempts {P(rt.Player)} ({rt.Level}){(rt.Bearer is { } bearer ? $": {C(bearer)} is the Ring-bearer" : "")}.",
            PhasedOut po => $"{C(po.Card)} phases out.",
            PhasedIn pi => $"{C(pi.Card)} phases in.",
            CardsRevealed cr => $"{P(cr.Player)} reveals {string.Join(", ", cr.Cards.Select(C))}.",
            HandRevealed { Chooser: { } chooser } hr => $"{P(hr.Player)} reveals their hand; {P(chooser)} chooses from it.",
            HandRevealed hr => $"{P(hr.Player)} reveals their hand.",
            HandLookedAt hl => $"{P(hl.Looker)} looks at {P(hl.Player)}'s hand.",
            ChosenFromHand ch => $"{P(ch.Chooser)} chooses {string.Join(", ", ch.Cards.Select(C))} from {P(ch.Player)}'s hand.",
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
