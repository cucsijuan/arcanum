// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>
/// Shows what another player just did, one announcement at a time: a banner with the card involved, a sentence,
/// and arrows to whatever it affects. While announcements are queued the game waits (see the board's
/// presentation gate), so nothing an opponent does happens faster than the player can follow.
/// </summary>
public partial class Announcer : Control
{
    public sealed record Announcement(string Text, CardView? Card, double Seconds, Func<Control, IEnumerable<ArrowLayer.Arrow>>? Arrows = null);

    private readonly Queue<Announcement> _queue = new();
    private readonly PanelContainer _banner = new();
    private readonly CardNode _card = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Label _text = BoardStyle.MakeLabel("", 20, BoardStyle.Text, bold: true);
    private double _remaining;

    /// <summary>Arrows of the current announcement changed (from the banner card to its targets).</summary>
    public event Action<IReadOnlyList<ArrowLayer.Arrow>>? ArrowsChanged;

    public bool Busy => _remaining > 0 || _queue.Count > 0;

    public Announcer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorLeft = 0.5f; AnchorRight = 0.5f; AnchorTop = 0.5f; AnchorBottom = 0.5f;
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        OffsetTop = -250;
        var box = BoardStyle.Box(new Color(0.05f, 0.05f, 0.07f, 0.94f), 12, BoardStyle.ActiveBorder, 2);
        box.SetContentMarginAll(14);
        _banner.AddThemeStyleboxOverride("panel", box);
        _banner.MouseFilter = MouseFilterEnum.Ignore;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 16);
        var holder = new Control { CustomMinimumSize = new Vector2(150, 209), MouseFilter = MouseFilterEnum.Ignore };
        _card.Size = new Vector2(150, 209);
        holder.AddChild(_card);
        row.AddChild(holder);
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _text.CustomMinimumSize = new Vector2(380, 0);
        _text.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_text);
        _banner.AddChild(row);
        _banner.Visible = false;
        AddChild(_banner);
    }

    public void Enqueue(Announcement announcement)
    {
        _queue.Enqueue(announcement);
        if (_remaining <= 0) ShowNext();
    }

    public void Clear()
    {
        _queue.Clear();
        _remaining = 0;
        _banner.Visible = false;
        ArrowsChanged?.Invoke(Array.Empty<ArrowLayer.Arrow>());
    }

    public override void _Process(double delta)
    {
        if (_remaining <= 0) return;
        _remaining -= delta;
        if (_remaining <= 0) ShowNext();
    }

    private void ShowNext()
    {
        if (!_queue.TryDequeue(out var next))
        {
            _remaining = 0;
            _banner.Visible = false;
            ArrowsChanged?.Invoke(Array.Empty<ArrowLayer.Arrow>());
            return;
        }
        _remaining = next.Seconds * BoardStyle.AnimationScale;
        _text.Text = next.Text;
        _card.GetParent<Control>().Visible = next.Card is not null;
        if (next.Card is not null) _card.Setup(next.Card, showCostPips: false);
        _banner.Visible = true;
        _banner.ResetSize();
        _banner.Position = -_banner.Size / 2;
        ArrowsChanged?.Invoke(next.Arrows?.Invoke(next.Card is not null ? _card : _banner).ToList() ?? new List<ArrowLayer.Arrow>());
    }
}
