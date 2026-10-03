// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Data.CardData;

public enum CardSupport
{
    /// <summary>Everything on the card is understood by the engine.</summary>
    Full,
    /// <summary>The card has rules the engine can't run yet (no script, unsupported keyword or layout).</summary>
    Unsupported,
}

/// <summary>Turns imported <see cref="CardRecord"/>s into engine <see cref="CardDefinition"/>s.</summary>
public static partial class CardFactory
{
    /// <summary>Land types with an intrinsic mana ability (rule 305.6).</summary>
    private static readonly Dictionary<string, ManaType> BasicLandTypes = new(StringComparer.Ordinal)
    {
        ["Plains"] = ManaType.White, ["Island"] = ManaType.Blue, ["Swamp"] = ManaType.Black,
        ["Mountain"] = ManaType.Red, ["Forest"] = ManaType.Green,
    };

    private static readonly HashSet<string> SingleFaceLayouts = new(StringComparer.Ordinal) { "normal", "token" };

    /// <summary>Keywords the engine implements. Grows as keyword support lands.</summary>
    public static ISet<string> SupportedKeywords { get; } = new HashSet<string>(Engine.Cards.Keywords.SupportedNames, StringComparer.OrdinalIgnoreCase);

    public static (CardDefinition Definition, CardSupport Support) Create(CardRecord record)
    {
        var (supertypes, types, subtypes) = TypeLine.Parse(record.TypeLine);
        var tapForMana = subtypes.Where(BasicLandTypes.ContainsKey).Select(t => BasicLandTypes[t]).Distinct().ToList();
        if (record.Name == "Wastes" || (types.HasFlag(CardType.Land) && tapForMana.Count == 0 && IsOnlyColorlessManaText(record.OracleText)))
            tapForMana.Add(ManaType.Colorless);

        bool costOk = TryParseCost(record.ManaCost, out var cost);
        int? power = ParseStat(record.Power, out bool powerOk);
        int? toughness = ParseStat(record.Toughness, out bool toughnessOk);

        var definition = new CardDefinition
        {
            Name = record.Name,
            OracleId = record.OracleId,
            ManaCost = cost,
            Types = types,
            Supertypes = supertypes,
            Subtypes = subtypes,
            Power = power,
            Toughness = toughness,
            OracleText = record.OracleText,
            Keywords = record.Keywords,
            TapForMana = tapForMana,
        };

        bool supported = costOk && powerOk && toughnessOk
                         && SingleFaceLayouts.Contains(record.Layout)
                         && record.Keywords.All(SupportedKeywords.Contains)
                         && RulesTextIsCovered(record, tapForMana.Count > 0);
        return (definition, supported ? CardSupport.Full : CardSupport.Unsupported);
    }

    /// <summary>
    /// True when the rules text adds nothing beyond what the engine derives from card data: empty, reminder text,
    /// keyword lines, or a basic land type's mana ability.
    /// </summary>
    private static bool RulesTextIsCovered(CardRecord record, bool hasIntrinsicMana)
    {
        var text = ReminderText().Replace(record.OracleText, "");
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('.');
            if (line.Length == 0) continue;
            if (hasIntrinsicMana && TapForManaLine().IsMatch(line)) continue;
            // A keyword line: "Flying" or "Flying, trample".
            if (line.Split(',').Select(k => k.Trim()).All(k => record.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase))) continue;
            return false;
        }
        return true;
    }

    private static bool IsOnlyColorlessManaText(string oracleText) =>
        ReminderText().Replace(oracleText, "").Trim().TrimEnd('.') == "{T}: Add {C}";

    private static bool TryParseCost(string text, out ManaCost cost)
    {
        try
        {
            cost = ManaCost.Parse(text);
            return true;
        }
        catch (FormatException)
        {
            cost = ManaCost.Zero; // hybrid, Phyrexian, X... arrive with the ability system
            return false;
        }
    }

    private static int? ParseStat(string? text, out bool ok)
    {
        ok = true;
        if (text is null) return null;
        if (int.TryParse(text, out int value)) return value;
        ok = false; // "*", "1+*" and friends need characteristic-defining abilities
        return null;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex ReminderText();

    [GeneratedRegex(@"^\{T\}: Add \{[WUBRGC]\}( or \{[WUBRGC]\})*$")]
    private static partial Regex TapForManaLine();
}
