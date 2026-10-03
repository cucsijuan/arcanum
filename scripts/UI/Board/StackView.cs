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

    public event Action<CardNode>? CardHoverStarted;
    public event Action<CardNode>? CardHoverEnded;

    public StackView()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _title.Position = new Vector2(0, -22);
        AddChild(_title);
    }

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
        }
        for (int i = 0; i < _cards.Count; i++)
        {
            bool used = i < stack.Count;
            _cards[i].Visible = used;
            if (!used) continue;
            _cards[i].Setup(stack[i].Card, showCostPips: false); // stack[^1] is the top: drawn last, in front
            _cards[i].Position = new Vector2(0, i * Cascade);
            _cards[i].ZIndex = i;
        }
    }
}
