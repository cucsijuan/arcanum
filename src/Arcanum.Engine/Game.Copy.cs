// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Arcanum.Engine.Core;
using Arcanum.Engine.Players;
using Arcanum.Engine.State;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>Where a copied game picks up when it runs (see <see cref="Copy"/>).</summary>
    private enum ResumePoint { None, Priority, Attackers, Blockers }

    private ResumePoint _resume;

    // The turn's position, kept in fields (not in the turn loop's local variables) so a copy can resume from it.
    private bool _firstTurn;
    private PlayerId _regularPlayer; // whose turn it is in the normal turn order (extra turns aside)
    private bool _skipDraw;
    private List<Step> _turnOrder = new(); // this turn's steps, with any additional combat phases inserted
    private int _stepIndex;
    private int _passesInARow; // players who passed priority in succession (rule 117.4)

    /// <summary>
    /// What a player is being asked at a copy point: their action with priority at the top of the game loop, their
    /// attackers as the declare attackers step begins, or their blockers (when blocking costs nothing); None anywhere else.
    /// </summary>
    private ResumePoint _waiting;

    /// <summary>
    /// Whether the game can be copied now: a player is being asked what to do with priority (not in the middle of casting
    /// or resolving), which creatures attack or which block, outside the combat damage and cleanup steps, in a game without a game mode;
    /// or a copy that hasn't run yet.
    /// </summary>
    public bool CanCopy => (_waiting != ResumePoint.None || _resume != ResumePoint.None) && _mode is null && !State.IsGameOver
                           && State.Step is not (Step.CombatDamage or Step.Cleanup or Step.Untap);

    /// <summary>
    /// A copy of the game at this point, played by <paramref name="controllers"/> (one per seat), that goes on independently
    /// of this one when run (<see cref="RunAsync"/>): it asks the player being asked here again (for their action with
    /// priority, their attackers or their blockers),
    /// with everything else (cards, effects, the turn's steps still to come, the random numbers to come) as it is here.
    /// Its log starts as a copy of this game's. Only when <see cref="CanCopy"/>.
    /// </summary>
    public Game Copy(IReadOnlyList<IPlayerController> controllers)
    {
        if (!CanCopy) throw new InvalidOperationException("The game can only be copied while a player is being asked for an action with priority, for attackers or for blockers.");
        if (controllers.Count != _controllers.Count) throw new ArgumentException("One controller per seat.", nameof(controllers));
        var copy = new DeepCopier(CopyRule, Copiers).Copy(this);
        copy._controllers = controllers;
        if (copy._resume == ResumePoint.None) copy._resume = _waiting;
        copy._waiting = ResumePoint.None;
        return copy;
    }

    /// <summary>The copiers written ahead of time for the game's objects (Game.Copiers.g.cs), by type.</summary>
    private static readonly IReadOnlyDictionary<Type, Func<object, DeepCopier, object>> Copiers = MakeCopiers();

    private static Dictionary<Type, Func<object, DeepCopier, object>> MakeCopiers()
    {
        var copiers = new Dictionary<Type, Func<object, DeepCopier, object>>();
        RegisterCopiers(copiers);
        return copiers;
    }

    /// <summary>
    /// Adds the generated copiers (Game.Copiers.g.cs, written by the engine tests' CopierGenerator). Without that file the
    /// game is copied through reflection alone: delete it when a field it names is gone, then write it again.
    /// </summary>
    static partial void RegisterCopiers(Dictionary<Type, Func<object, DeepCopier, object>> copiers);

    /// <summary>What a copy of the game does with each of the game's own fields: the wiring stays out of it.</summary>
    private static FieldRule CopyRule(FieldInfo field)
    {
        if (field.DeclaringType != typeof(Game)) return FieldRule.Copy;
        return field.Name switch
        {
            nameof(_controllers) or nameof(_ct) or nameof(EventRaised) or nameof(KnowledgeLost) or nameof(_replayView) or nameof(_modeHost) or nameof(_mode)
                => FieldRule.Clear,
            _ when field.Name == $"<{nameof(Config)}>k__BackingField" => FieldRule.Keep,
            _ => FieldRule.Copy,
        };
    }
}
