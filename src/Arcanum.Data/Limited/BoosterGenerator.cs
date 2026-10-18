// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Data.CardData;
using Arcanum.Data.Decks;
using Arcanum.Engine.Cards;

namespace Arcanum.Data.Limited;

/// <summary>A card in a booster or a pool: the card and the printing it came as (a traditional or a surge foil, or not).</summary>
public sealed record PoolCard(string Name, string Set, string Number, string Rarity, bool Foil = false, bool Surge = false)
{
    /// <summary>A deck line for this card (with its printing when it has one).</summary>
    public DeckEntry ToEntry(int count = 1) => Set.Length == 0 ? new(count, Name) : new(count, Name, Set.ToUpperInvariant(), Number, Foil || Surge) { Surge = Surge };

    public static PoolCard Of(CardEntry card, Printing printing) => new(card.Name, printing.Set, printing.CollectorNumber, printing.Rarity);
}

/// <summary>Where a limited event's boosters come from: a set's boosters or a cube.</summary>
public interface IBoosterSource
{
    /// <summary>Shown to players ("Alphabet Play Booster", "My Cube").</summary>
    string Name { get; }

    /// <summary>Opens one booster.</summary>
    List<PoolCard> Open(Random random);
}

/// <summary>Opens boosters of a set following its <see cref="BoosterSpec"/>.</summary>
public sealed class BoosterGenerator : IBoosterSource
{
    public string Name => $"{Set.Name} {_spec.Name}";

    private readonly BoosterSpec _spec;
    // Each sheet entry is one card with every printing it has on that sheet (alternate arts share the card's chance).
    private readonly Dictionary<string, List<PoolCard[]>> _sheets = new();

    public SetDefinition Set { get; }

    /// <param name="spec">Which of the set's boosters (its play or draft booster when null).</param>
    public BoosterGenerator(SetDefinition set, CardDatabase cards, BoosterSpec? spec = null)
    {
        Set = set;
        _spec = spec ?? set.Booster ?? throw new InvalidOperationException($"Set '{set.Code}' has no booster definition.");
        foreach (var (name, sheet) in _spec.Sheets)
        {
            var code = sheet.Set ?? set.Code;
            var printings = cards.PrintingsIn(code).Where(x => Fits(sheet, x.Card, x.Printing)).ToList();
            _sheets[name] = printings.GroupBy(x => x.Card.Name).Select(g => g.Select(x => PoolCard.Of(x.Card, x.Printing)).ToArray()).ToList();
        }
    }

    /// <summary>Cards on a sheet (for checks and tests).</summary>
    public IReadOnlyList<PoolCard> Sheet(string name) => _sheets[name].Select(variants => variants[0]).ToList();

    /// <summary>Sheets that matched no card (the card data doesn't know the set, or the module's sheet is wrong).</summary>
    public IEnumerable<string> EmptySheets => _sheets.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key);

    private static bool Fits(SheetSpec sheet, CardEntry card, Printing printing)
    {
        if (sheet.Rarity is { } rarity && !printing.Rarity.Equals(rarity, StringComparison.OrdinalIgnoreCase)) return false;
        if (sheet.Booster is { } booster && printing.Booster != booster) return false;
        if (sheet.Basic is { } basic && ((card.Definition.Supertypes & Supertype.Basic) != 0) != basic) return false;
        if (sheet.Names is { } names && !names.Contains(card.Name, StringComparer.OrdinalIgnoreCase)) return false;
        if (sheet.Numbers is { } numbers && !numbers.Contains(printing.CollectorNumber, StringComparer.OrdinalIgnoreCase)) return false;
        if (sheet.Exclude.Contains(card.Name, StringComparer.OrdinalIgnoreCase)) return false;
        if (sheet.Foil && !printing.Foil) return false;
        if (!sheet.Foil && printing.FoilOnly) return false; // a non-foil slot never gives a printing made only in foil
        return true;
    }

    /// <summary>One booster, in slot order (of one of its variants, as often as their weights say).</summary>
    public List<PoolCard> Open(Random random)
    {
        var pack = new List<PoolCard>();
        var slots = _spec.Slots;
        if (_spec.Variants.Count > 1)
        {
            int roll = random.Next(_spec.Variants.Sum(v => v.Weight));
            foreach (var variant in _spec.Variants)
            {
                if (roll < variant.Weight) { slots = variant.Slots; break; }
                roll -= variant.Weight;
            }
        }
        foreach (var slot in slots)
        {
            for (int i = 0; i < slot.Count; i++)
            {
                var sheetName = PickSheet(slot, random);
                var sheet = _sheets[sheetName];
                if (sheet.Count == 0) continue;
                var sheetSpec = _spec.Sheets[sheetName];
                if (slot.Pair)
                {
                    // Both cards of one pair on the sheet, neither already in the booster.
                    PoolCard[]? Entry(string name) => sheet.FirstOrDefault(c => c[0].Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var pairs = sheetSpec.Pairs.Where(p => Entry(p.First) is not null && Entry(p.Second) is not null
                                                           && !pack.Any(c => c.Name == p.First || c.Name == p.Second)).ToList();
                    if (pairs.Count == 0) continue;
                    var (first, second) = pairs[random.Next(pairs.Count)];
                    foreach (var half in new[] { Entry(first)!, Entry(second)! })
                    {
                        var printed = half[random.Next(half.Length)];
                        pack.Add(sheetSpec.Surge ? printed with { Foil = true, Surge = true } : sheetSpec.Foil ? printed with { Foil = true } : printed);
                    }
                    continue;
                }
                // Outside wildcard slots (and sheets that may repeat, like foils) a booster never repeats a card.
                var choices = slot.Wildcard || slot.RepeatSheets.Contains(sheetName) ? sheet : sheet.Where(c => !pack.Any(p => p.Name == c[0].Name)).ToList();
                if (choices.Count == 0) choices = sheet;
                var variants = choices[random.Next(choices.Count)];
                var card = variants[random.Next(variants.Length)];
                pack.Add(sheetSpec.Surge ? card with { Foil = true, Surge = true } : sheetSpec.Foil ? card with { Foil = true } : card);
            }
        }
        return pack;
    }

    private static string PickSheet(SlotSpec slot, Random random)
    {
        int total = slot.Sheets.Values.Sum();
        int roll = random.Next(total);
        foreach (var (sheet, weight) in slot.Sheets)
        {
            if (roll < weight) return sheet;
            roll -= weight;
        }
        return slot.Sheets.Keys.Last();
    }
}
