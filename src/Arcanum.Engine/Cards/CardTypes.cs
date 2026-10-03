// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Cards;

[Flags]
public enum CardType
{
    None = 0,
    Land = 1 << 0,
    Creature = 1 << 1,
    Artifact = 1 << 2,
    Enchantment = 1 << 3,
    Planeswalker = 1 << 4,
    Instant = 1 << 5,
    Sorcery = 1 << 6,
    Battle = 1 << 7,
}

[Flags]
public enum Supertype
{
    None = 0,
    Basic = 1 << 0,
    Legendary = 1 << 1,
    Snow = 1 << 2,
    World = 1 << 3,
}

public static class CardTypeExtensions
{
    /// <summary>Permanent card types end up on the battlefield when they resolve (rule 110.4).</summary>
    public static bool IsPermanent(this CardType types) =>
        (types & (CardType.Land | CardType.Creature | CardType.Artifact | CardType.Enchantment
                  | CardType.Planeswalker | CardType.Battle)) != 0;
}
