// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.State;

public enum Phase
{
    Beginning,
    PrecombatMain,
    Combat,
    PostcombatMain,
    Ending,
}

public enum Step
{
    Untap,
    Upkeep,
    Draw,
    PrecombatMain,
    BeginCombat,
    DeclareAttackers,
    DeclareBlockers,
    CombatDamage,
    EndCombat,
    PostcombatMain,
    End,
    Cleanup,
}

public static class StepExtensions
{
    public static IReadOnlyList<Step> TurnOrder { get; } = Enum.GetValues<Step>();

    public static Phase GetPhase(this Step step) => step switch
    {
        Step.Untap or Step.Upkeep or Step.Draw => Phase.Beginning,
        Step.PrecombatMain => Phase.PrecombatMain,
        Step.BeginCombat or Step.DeclareAttackers or Step.DeclareBlockers
            or Step.CombatDamage or Step.EndCombat => Phase.Combat,
        Step.PostcombatMain => Phase.PostcombatMain,
        _ => Phase.Ending,
    };

    public static bool IsMain(this Step step) => step is Step.PrecombatMain or Step.PostcombatMain;
}
