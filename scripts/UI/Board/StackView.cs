// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Views;
using Godot;

namespace Arcanum.UI.Board;

/// <summary>Spells waiting to resolve, cascaded with the top of the stack in front.</summary>
public partial class StackView : Control
{
    private const float Cascade = 34;
    private readonly Label _title = BoardStyle.MakeLabel("Stack", 13);
    private readonly List<CardNode> _cards = new();
    private readonly List<Label> _labels = new();

    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    public StackView()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _title.Position = new Vector2(0, -22);
        AddChild(_title);
    }

    /// <summary>Card node showing the i-th stack item (0 = bottom), for arrows to its targets.</summary>
    public CardNode? NodeAt(int index) => index < _cards.Count && _cards[index].Visible ? _cards[index] : null;

    public void Refresh(IReadOnlyList<StackItemView> stack)
    {
        Visible = stack.Count > 0;
        _title.Text = $"Stack ({stack.Count})";
        while (_cards.Count < stack.Count)
        {
            var node = new CardNode { Size = BoardStyle.BattlefieldCardSize };
            node.HoverStarted += c => CardHoverStarted?.Invoke(c);
            node.HoverEnded += c => CardHoverEnded?.Invoke(c);
            AddChild(node);
            _cards.Add(node);
            var label = BoardStyle.MakeLabel("", 11, BoardStyle.Text);
            label.AddThemeConstantOverride("outline_size", 6);
            label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.Size = new Vector2(BoardStyle.BattlefieldCardSize.X + 80, 0);
            AddChild(label);
            _labels.Add(label);
        }
        for (int i = 0; i < _cards.Count; i++)
        {
            bool used = i < stack.Count;
            _cards[i].Visible = used;
            _labels[i].Visible = used && stack[i].AbilityText is not null;
            if (!used) continue;
            // Abilities show their text next to the source card, so they read differently from spells.
            _labels[i].Text = stack[i].AbilityText ?? "";
            _labels[i].Position = new Vector2(BoardStyle.BattlefieldCardSize.X + 6, i * Cascade + 4);
            _labels[i].ZIndex = i;
            _cards[i].Setup(stack[i].Card, showCostPips: false); // stack[^1] is the top: drawn last, in front
            _cards[i].Position = new Vector2(0, i * Cascade);
            _cards[i].ZIndex = i;
        }
    }
}
