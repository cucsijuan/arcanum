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

    /// <param name="script">The card's ability script from the content module, if it has one.</param>
    public static (CardDefinition Definition, CardSupport Support) Create(CardRecord record, Scripts.CardScript? script = null)
    {
        var (supertypes, types, subtypes) = TypeLine.Parse(record.TypeLine);
        var tapForMana = subtypes.Where(BasicLandTypes.ContainsKey).Select(t => BasicLandTypes[t]).ToList();
        tapForMana.AddRange(ManaAbilityTypes(record.OracleText));
        tapForMana = tapForMana.Distinct().ToList();

        bool costOk = TryParseCost(record.ManaCost, out var cost);
        var (derivedAbilities, enchant, entersTapped) = DeriveFromText(record.OracleText);
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
            Spell = script?.Spell,
            Abilities = derivedAbilities.Concat(script?.Abilities ?? Array.Empty<Engine.Abilities.AbilityDefinition>()).ToList(),
            EnchantTarget = script?.Aura ?? enchant,
            EntersTapped = entersTapped || (script?.EntersTapped ?? false),
            EntersWithCounters = script?.EntersWithCounters ?? 0,
        };

        bool supported = costOk && powerOk && toughnessOk
                         && SingleFaceLayouts.Contains(record.Layout)
                         && record.Keywords.All(SupportedKeywords.Contains)
                         && (script is not null || RulesTextIsCovered(record));
        return (definition, supported ? CardSupport.Full : CardSupport.Unsupported);
    }

    /// <summary>
    /// True when the rules text adds nothing beyond what the engine derives from card data: empty, reminder text,
    /// keyword lines, or a basic land type's mana ability.
    /// </summary>
    private static bool RulesTextIsCovered(CardRecord record)
    {
        var text = ReminderText().Replace(record.OracleText, "");
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('.');
            if (line.Length == 0) continue;
            if (TapForManaLine().IsMatch(line) || AnyColorManaLine().IsMatch(line)) continue;
            if (EntersTappedLine().IsMatch(line)) continue;
            // A keyword line: "Flying" or "Flying, trample".
            if (line.Split(',').Select(k => k.Trim()).All(k => record.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase))) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Abilities written in a fixed form the engine understands without a script: "Equip {N}" (sorcery-speed
    /// attach to a creature you control), "Enchant creature/land/..." (an Aura's target) and "enters tapped".
    /// </summary>
    private static (List<Engine.Abilities.AbilityDefinition> Abilities, Engine.Abilities.TargetSpec? Enchant, bool EntersTapped) DeriveFromText(string oracleText)
    {
        var abilities = new List<Engine.Abilities.AbilityDefinition>();
        Engine.Abilities.TargetSpec? enchant = null;
        bool entersTapped = false;
        foreach (var rawLine in ReminderText().Replace(oracleText, "").Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('.');
            var equip = EquipLine().Match(line);
            if (equip.Success)
            {
                abilities.Add(new Engine.Abilities.ActivatedAbility
                {
                    Cost = new Engine.Abilities.AbilityCost(ManaCost.Parse(equip.Groups[1].Value)),
                    SorcerySpeed = true,
                    Targets = new[] { new Engine.Abilities.TargetSpec(Engine.Abilities.TargetKind.Creature, Engine.Abilities.ControllerFilter.You) },
                    Effects = new Engine.Abilities.Effect[] { new Engine.Abilities.AttachSelf(Engine.Abilities.Subject.TargetAt(0)) },
                    Text = line,
                });
            }
            var enchantMatch = EnchantLine().Match(line);
            if (enchantMatch.Success)
            {
                var kind = Enum.Parse<Engine.Abilities.TargetKind>(enchantMatch.Groups[1].Value, ignoreCase: true);
                var controller = enchantMatch.Groups[2].Success ? Engine.Abilities.ControllerFilter.You : Engine.Abilities.ControllerFilter.Any;
                enchant = new Engine.Abilities.TargetSpec(kind, controller);
            }
            if (EntersTappedLine().IsMatch(line)) entersTapped = true;
        }
        return (abilities, enchant, entersTapped);
    }

    /// <summary>Mana from "{T}: Add {G}", "{T}: Add {R} or {G}" and "{T}: Add one mana of any color" lines.</summary>
    private static IEnumerable<ManaType> ManaAbilityTypes(string oracleText)
    {
        foreach (var rawLine in ReminderText().Replace(oracleText, "").Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('.');
            if (AnyColorManaLine().IsMatch(line))
            {
                foreach (var t in new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }) yield return t;
            }
            else if (TapForManaLine().IsMatch(line))
            {
                foreach (System.Text.RegularExpressions.Match m in ManaSymbol().Matches(line[line.IndexOf("Add", StringComparison.Ordinal)..]))
                    if (ManaTypeExtensions.TryParse(m.Groups[1].Value[0], out var type)) yield return type;
            }
        }
    }

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

    [GeneratedRegex(@"^\{T\}: Add \{[WUBRGC]\}((, | or |, or )\{[WUBRGC]\})*$")]
    private static partial Regex TapForManaLine();

    [GeneratedRegex(@"^\{T\}: Add one mana of any color$")]
    private static partial Regex AnyColorManaLine();

    [GeneratedRegex(@"\{([WUBRGC])\}")]
    private static partial Regex ManaSymbol();

    [GeneratedRegex(@"^Equip ((\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex EquipLine();

    [GeneratedRegex(@"^Enchant (creature|land|artifact|enchantment|permanent)( you control)?$")]
    private static partial Regex EnchantLine();

    [GeneratedRegex(@"^(This land|This creature|This artifact|This permanent) enters tapped$")]
    private static partial Regex EntersTappedLine();
}
