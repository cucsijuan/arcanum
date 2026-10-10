// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Events;
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Combat damage, one blow at a time: each card dealing combat damage rises, lunges at what it damages (a creature, a
/// planeswalker or a player's life), lands with a burst of light and the damage dealt, and goes back to its place,
/// so the player taking it can follow what each card does. A copy of the card makes the trip (the card itself is
/// hidden meanwhile), so a creature that dies of the damage still strikes.
/// </summary>
public partial class GameBoard
{
    /// <summary>A blow to play: where the card stood when the damage was dealt and what it hit.</summary>
    private sealed record Strike(CardId SourceId, CardNode? Source, CardView? View, Vector2 From, Vector2 Size, float Rotation, CardId? TargetCard, PlayerId? TargetPlayer, int Amount);

    private readonly List<Strike> _strikes = new();

    /// <summary>Seconds between the blows of one creature and the next one's.</summary>
    private const double BetweenCreatures = 0.5;
    private bool _strikesQueued, _striking;

    /// <summary>The blows of a combat damage step arrive together; they are played once all are in, in order.</summary>
    private void QueueStrike(EventView ev, DamageDealt damage)
    {
        var source = FindCard(damage.Source);
        _strikes.Add(new Strike(damage.Source, source, source?.View ?? ViewOf(ev, damage.Source),
            source?.GlobalPosition ?? Vector2.Zero, source?.Size ?? Vector2.Zero, source?.Rotation ?? 0,
            damage.TargetCard, damage.TargetPlayer, damage.Amount));
        if (_strikesQueued) return;
        _strikesQueued = true;
        Callable.From(PlayStrikes).CallDeferred();
    }

    private async void PlayStrikes()
    {
        _strikesQueued = false;
        if (_striking) return; // the running sequence takes the new ones too
        _striking = true;
        CardId? previous = null;
        while (_strikes.Count > 0 && IsInstanceValid(this))
        {
            var strike = _strikes[0];
            _strikes.RemoveAt(0);
            // A pause before the next creature's blows, so each card's action is seen on its own.
            if (previous is { } before && before != strike.SourceId)
                await ToSignal(GetTree().CreateTimer(BetweenCreatures * BoardStyle.AnimationScale), SceneTreeTimer.SignalName.Timeout);
            previous = strike.SourceId;
            await StrikeAsync(strike);
        }
        Hold(0.6); // the last numbers are seen before the game moves on
        _striking = false;
    }

    /// <summary>Where a blow lands: the middle of the creature or planeswalker hit, or the player's life counter.</summary>
    private Vector2? ImpactPoint(Strike strike) =>
        strike.TargetPlayer is { } player ? AreaOf(player).LifeGlobalCenter
        : strike.TargetCard is { } card && FindCard(card) is { } node ? node.GetGlobalTransform() * (node.Size / 2)
        : null;

    private async Task StrikeAsync(Strike strike)
    {
        if (ImpactPoint(strike) is not { } impact) return;
        float k = BoardStyle.AnimationScale;
        if (strike.View is not { } view || strike.Size == Vector2.Zero || k <= 0)
        {
            Land(strike, impact);
            return;
        }

        var ghost = new CardNode { Size = strike.Size, MouseFilter = MouseFilterEnum.Ignore, ZIndex = BoardStyle.Z.Floaters - 1 };
        AddChild(ghost);
        ghost.Setup(view, showCostPips: false);
        ghost.PivotOffset = strike.Size / 2;
        ghost.Rotation = strike.Rotation;
        ghost.GlobalPosition = strike.From;
        var hidden = strike.Source is { } source && IsInstanceValid(source) ? source : null;
        var modulate = hidden?.Modulate ?? Colors.White;
        if (hidden is not null) hidden.Modulate = Colors.Transparent;

        // It stops as it touches what it hits, leaning a little towards it.
        var center = strike.From + strike.Size / 2;
        var direction = (impact - center).Normalized();
        var hit = impact - direction * Mathf.Min(strike.Size.X, strike.Size.Y) * 0.35f - strike.Size / 2;
        var tween = ghost.CreateTween();
        tween.TweenProperty(ghost, "scale", new Vector2(1.12f, 1.12f), 0.14 * k).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.Parallel().TweenProperty(ghost, "rotation", direction.X * 0.15f, 0.14 * k);
        tween.TweenProperty(ghost, "global_position", hit, 0.16 * k).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tween.TweenCallback(Callable.From(() => Land(strike, impact)));
        tween.TweenInterval(0.08 * k);
        tween.TweenProperty(ghost, "global_position", strike.From, 0.26 * k).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.Parallel().TweenProperty(ghost, "scale", Vector2.One, 0.26 * k);
        tween.Parallel().TweenProperty(ghost, "rotation", strike.Rotation, 0.26 * k);
        await ToSignal(tween, Tween.SignalName.Finished);
        ghost.QueueFree();
        if (hidden is not null && IsInstanceValid(hidden)) hidden.Modulate = modulate;
    }

    /// <summary>The blow lands: a burst of light, the damage dealt and, on a player, their life counter flashing; a creature hit shakes.</summary>
    private void Land(Strike strike, Vector2 impact)
    {
        Burst(impact);
        if (strike.TargetPlayer is { } player)
        {
            var area = AreaOf(player);
            SpawnFloatingText($"-{strike.Amount}", area.LifeGlobalCenter + new Vector2(0, 62), BoardStyle.Attacking, rise: 30);
            area.FlashLife(BoardStyle.Attacking);
        }
        else
        {
            SpawnFloatingText($"-{strike.Amount}", impact, BoardStyle.Attacking);
            if (strike.TargetCard is { } card && FindCard(card) is { } node)
            {
                var shake = node.CreateTween();
                foreach (float dx in new[] { 7f, -6f, 4f, -2f, 0f }) shake.TweenProperty(node, "position:x", node.Position.X + dx, 0.035);
            }
        }
    }

    private static GradientTexture2D? _burstTexture;

    /// <summary>A flash of warm light spreading from where a blow lands.</summary>
    private void Burst(Vector2 at)
    {
        _burstTexture ??= new GradientTexture2D
        {
            Width = 256, Height = 256, Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
            Gradient = new Gradient
            {
                Offsets = new[] { 0f, 0.25f, 0.6f, 1f },
                Colors = new[] { new Color(1, 1, 0.92f, 1), new Color(1, 0.86f, 0.45f, 0.85f), new Color(1, 0.6f, 0.2f, 0.3f), new Color(1, 0.5f, 0.1f, 0) },
            },
        };
        var flash = new TextureRect
        {
            Texture = _burstTexture, Size = new Vector2(240, 240), MouseFilter = MouseFilterEnum.Ignore, ZIndex = BoardStyle.Z.Floaters - 1,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        AddChild(flash);
        flash.PivotOffset = flash.Size / 2;
        flash.GlobalPosition = at - flash.Size / 2;
        flash.Scale = new Vector2(0.2f, 0.2f);
        float k = BoardStyle.AnimationScale;
        var tween = flash.CreateTween().SetParallel();
        tween.TweenProperty(flash, "scale", new Vector2(1.3f, 1.3f), 0.32 * k).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(flash, "modulate:a", 0f, 0.32 * k).SetDelay(0.08 * k);
        tween.Chain().TweenCallback(Callable.From(flash.QueueFree));
    }
}
