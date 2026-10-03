// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Mana;

/// <summary>A single type of mana. Colorless (C) is a type of mana, not a color.</summary>
public enum ManaType
{
    White,
    Blue,
    Black,
    Red,
    Green,
    Colorless,
}

public static class ManaTypeExtensions
{
    public static char ToSymbol(this ManaType type) => type switch
    {
        ManaType.White => 'W',
        ManaType.Blue => 'U',
        ManaType.Black => 'B',
        ManaType.Red => 'R',
        ManaType.Green => 'G',
        ManaType.Colorless => 'C',
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParse(char symbol, out ManaType type)
    {
        switch (char.ToUpperInvariant(symbol))
        {
            case 'W': type = ManaType.White; return true;
            case 'U': type = ManaType.Blue; return true;
            case 'B': type = ManaType.Black; return true;
            case 'R': type = ManaType.Red; return true;
            case 'G': type = ManaType.Green; return true;
            case 'C': type = ManaType.Colorless; return true;
            default: type = default; return false;
        }
    }
}
