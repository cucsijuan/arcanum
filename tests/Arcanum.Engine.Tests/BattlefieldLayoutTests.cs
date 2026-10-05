// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Cards;
using Arcanum.Engine.Core;
using Arcanum.Engine.State;
using Arcanum.Engine.Views;

namespace Arcanum.Engine.Tests;

/// <summary>Which row a permanent stands in, and which tokens are shown as one stack.</summary>
public class BattlefieldLayoutTests
{
    private static int _next;

    private static CardView Card(string name, CardType types, bool token = false, Action<CardViewBuilder>? tweak = null)
    {
        var b = new CardViewBuilder { Name = name, Types = types, IsToken = token };
        tweak?.Invoke(b);
        return new CardView
        {
            Id = new CardId(++_next), Owner = b.Controller, Controller = b.Controller, Zone = Zone.Battlefield, IsHidden = false,
            Name = b.Name, Types = b.Types, IsToken = b.IsToken, Tapped = b.Tapped, SummoningSick = b.SummoningSick, Damage = b.Damage,
            PlusOneCounters = b.PlusOne, Power = 1, Toughness = 1, AttachedTo = b.AttachedTo, Subtypes = b.Subtypes,
            OtherCounters = b.Other ?? new Dictionary<string, int>(),
        };
    }

    private sealed class CardViewBuilder
    {
        public string Name = "";
        public CardType Types;
        public bool IsToken, Tapped, SummoningSick;
        public int Damage, PlusOne;
        public Core.PlayerId Controller = new(0);
        public CardId? AttachedTo;
        public IReadOnlyList<string> Subtypes = Array.Empty<string>();
        public IReadOnlyDictionary<string, int>? Other;
    }

    private static string? Key(CardView c, bool? tapped = null, bool attachments = false) =>
        BattlefieldLayout.StackKey(c, tapped ?? c.Tapped, attachments);

    [Theory]
    [InlineData(CardType.Land, BattlefieldRow.Lands)]
    [InlineData(CardType.Creature, BattlefieldRow.Creatures)]
    [InlineData(CardType.Artifact, BattlefieldRow.Other)]
    [InlineData(CardType.Enchantment, BattlefieldRow.Other)]
    [InlineData(CardType.Planeswalker, BattlefieldRow.Other)]
    [InlineData(CardType.Artifact | CardType.Creature, BattlefieldRow.Creatures)]
    [InlineData(CardType.Land | CardType.Creature, BattlefieldRow.Creatures)]
    [InlineData(CardType.Artifact | CardType.Land, BattlefieldRow.Lands)]
    public void PermanentsGoToTheRowOfTheirType(CardType types, BattlefieldRow row) =>
        Assert.Equal(row, BattlefieldLayout.RowOf(Card("X", types)));

    [Fact]
    public void IdenticalTokensShareAKey()
    {
        Assert.Equal(Key(Card("Gem", CardType.Artifact, token: true)), Key(Card("Gem", CardType.Artifact, token: true)));
    }

    [Fact]
    public void OnlyTokensStack()
    {
        Assert.Null(Key(Card("Gem", CardType.Artifact)));
        Assert.Null(Key(Card("Gem", CardType.Artifact, token: true), attachments: true));
        Assert.Null(Key(Card("Aura", CardType.Enchantment, token: true, b => b.AttachedTo = new CardId(1))));
    }

    [Fact]
    public void AnyDifferenceInStateSplitsTheStack()
    {
        var plain = Key(Card("Gem", CardType.Artifact, token: true));
        Assert.NotEqual(plain, Key(Card("Gem", CardType.Artifact, token: true, b => b.Tapped = true)));
        Assert.NotEqual(plain, Key(Card("Gem", CardType.Artifact, token: true), tapped: true)); // picked in a payment, drawn tapped
        Assert.NotEqual(plain, Key(Card("Gem", CardType.Artifact, token: true, b => b.SummoningSick = true)));
        Assert.NotEqual(plain, Key(Card("Gem", CardType.Artifact, token: true, b => b.Damage = 1)));
        Assert.NotEqual(plain, Key(Card("Gem", CardType.Artifact, token: true, b => b.PlusOne = 1)));
        Assert.NotEqual(plain, Key(Card("Gem", CardType.Artifact, token: true, b => b.Other = new Dictionary<string, int> { ["stun"] = 1 })));
        Assert.NotEqual(plain, Key(Card("Gem", CardType.Artifact, token: true, b => b.Controller = new Core.PlayerId(1))));
        Assert.NotEqual(plain, Key(Card("Jewel", CardType.Artifact, token: true)));
    }

    [Fact]
    public void StacksFormInBattlefieldOrderAndSplitOffSiblingsStayNext()
    {
        var a1 = Card("Gem", CardType.Artifact, token: true);
        var other = Card("Statue", CardType.Artifact);
        var a2 = Card("Gem", CardType.Artifact, token: true);
        var tapped = Card("Gem", CardType.Artifact, token: true, b => b.Tapped = true);
        var a3 = Card("Gem", CardType.Artifact, token: true);
        var stacks = BattlefieldLayout.Stack(new[] { a1, other, a2, tapped, a3 }, c => Key(c));

        Assert.Equal(3, stacks.Count);
        Assert.Equal(new[] { a1.Id, a2.Id, a3.Id }, stacks[0].Cards.Select(c => c.Id));
        Assert.Equal(a1.Id, stacks[0].Top.Id);
        Assert.Equal(3, stacks[0].Count);
        Assert.Equal(tapped.Id, stacks[1].Top.Id); // the tapped one follows the other Gems
        Assert.Equal(other.Id, stacks[2].Top.Id);
    }

    [Fact]
    public void LandsOfOneNameStayTogetherWithoutStacking()
    {
        var f1 = Card("Forest", CardType.Land);
        var m1 = Card("Mountain", CardType.Land);
        var f2 = Card("Forest", CardType.Land);
        var stacks = BattlefieldLayout.Stack(new[] { f1, m1, f2 }, c => Key(c));
        Assert.Equal(new[] { f1.Id, f2.Id, m1.Id }, stacks.Select(s => s.Top.Id));
        Assert.All(stacks, s => Assert.Equal(1, s.Count));
    }

    [Fact]
    public void PickingHowManyKeepsTheChosenOnesFirst()
    {
        var ids = Enumerable.Range(1, 5).Select(i => new CardId(i)).ToList();
        var chosen = new HashSet<CardId> { ids[3], ids[4] };

        var (add, remove) = BattlefieldLayout.Pick(ids, chosen.Contains, 3);
        Assert.Equal(new[] { ids[0] }, add);
        Assert.Empty(remove);

        (add, remove) = BattlefieldLayout.Pick(ids, chosen.Contains, 1);
        Assert.Empty(add);
        Assert.Equal(new[] { ids[4] }, remove.Where(r => r == ids[4]));
        Assert.Single(remove);

        (add, remove) = BattlefieldLayout.Pick(ids, chosen.Contains, 0);
        Assert.Empty(add);
        Assert.Equal(2, remove.Count);

        (add, remove) = BattlefieldLayout.Pick(ids, _ => false, 9);
        Assert.Equal(ids, add);
    }
}
