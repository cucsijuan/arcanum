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

    /// <summary>
    /// Keyword actions and ability words the card source lists as keywords but that only label rules text a card
    /// script spells out (scry, raid...). A card with them is supported once it has a script.
    /// </summary>
    private static readonly string[] ScriptedKeywords =
    {
        "Scry", "Surveil", "Fight", "Mill", "Treasure", "Food", "Investigate",
        "Raid", "Landfall", "Morbid", "Threshold", "Ferocious", "Hexproof from",
    };

    /// <summary>Keywords the engine implements. Grows as keyword support lands.</summary>
    /// <remarks>Includes keywords whose rules this factory derives from rules text (Equip, Enchant).</remarks>
    public static ISet<string> SupportedKeywords { get; } =
        new HashSet<string>(Engine.Cards.Keywords.SupportedNames.Concat(new[] { "Equip", "Enchant", "Kicker", "Flashback", "Ward" }).Concat(ScriptedKeywords),
            StringComparer.OrdinalIgnoreCase);

    /// <param name="script">The card's ability script from the content module, if it has one.</param>
    public static (CardDefinition Definition, CardSupport Support) Create(CardRecord record, Scripts.CardScript? script = null)
    {
        var (supertypes, types, subtypes) = TypeLine.Parse(record.TypeLine);
        var tapForMana = subtypes.Where(BasicLandTypes.ContainsKey).Select(t => BasicLandTypes[t]).ToList();
        tapForMana.AddRange(ManaAbilityTypes(record.OracleText));
        tapForMana = tapForMana.Distinct().ToList();

        bool costOk = TryParseCost(record.ManaCost, out var cost);
        var (derivedAbilities, enchant, entersTapped) = DeriveFromText(record.OracleText);
        var derived = DeriveCosts(record.OracleText);
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
            Spell = WithTokenImages(script?.Spell, record),
            Abilities = derivedAbilities.Concat((script?.Abilities ?? Array.Empty<Engine.Abilities.AbilityDefinition>()).Select(a => WithTokenImages(a, record)!)).ToList(),
            EnchantTarget = script?.Aura ?? enchant,
            EntersTapped = entersTapped || (script?.EntersTapped ?? false),
            EntersWithCounters = script?.EntersWithCounters ?? 0,
            Kicker = derived.Kicker,
            Flashback = derived.Flashback,
            WardMana = derived.WardMana,
            WardLife = derived.WardLife,
            CantBeCountered = derived.CantBeCountered,
        };
        if (script is not null) definition = script.ApplyTo(definition);

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
            if (KickerLine().IsMatch(line) || FlashbackLine().IsMatch(line) || WardLine().IsMatch(line) || line == "This spell can't be countered") continue;
            // A keyword line: "Flying" or "Flying, trample".
            if (line.Split(',').Select(k => k.Trim()).All(k => record.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase) || WardLine().IsMatch(k))) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Points each token an ability creates at the exact token printing the card source lists for this card
    /// (matched by name, and by power/toughness when several share a name), so the client shows the right picture.
    /// </summary>
    private static T? WithTokenImages<T>(T? ability, CardRecord record) where T : Engine.Abilities.AbilityDefinition
    {
        if (ability is null || record.RelatedTokens.Count == 0 || !ability.Effects.Any(e => e is Engine.Abilities.CreateTokens)) return ability;
        var effects = ability.Effects.Select(effect =>
        {
            if (effect is not Engine.Abilities.CreateTokens create || create.Token.ImageKey is not null) return effect;
            var candidates = record.RelatedTokens.Where(t => t.Name.Equals(create.Token.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var match = candidates.Count <= 1 ? candidates.FirstOrDefault() : candidates.FirstOrDefault(t => MatchesStats(t, create.Token)) ?? candidates[0];
            return match is null ? effect : create with { Token = create.Token with { ImageKey = match.Id } };
        }).ToList();
        return ability with { Effects = effects };
    }

    /// <summary>Related tokens carry no stats in the source's listing; their type line can still tell colors apart.</summary>
    private static bool MatchesStats(RelatedToken token, CardDefinition definition) =>
        definition.Colors.Count == 0 || definition.Colors.Any(c => token.TypeLine.Contains(ColorWord(c), StringComparison.OrdinalIgnoreCase));

    private static string ColorWord(string color) => color switch
    {
        "W" => "White", "U" => "Blue", "B" => "Black", "R" => "Red", "G" => "Green", _ => color,
    };

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

    /// <summary>"Kicker {2}", "Flashback {1}{R}", "Ward {2}" / "Ward—Pay 2 life" / "Ward—{3}, Pay 3 life", "This spell can't be countered".</summary>
    private static (ManaCost? Kicker, ManaCost? Flashback, ManaCost? WardMana, int WardLife, bool CantBeCountered) DeriveCosts(string oracleText)
    {
        ManaCost? kicker = null, flashback = null, ward = null;
        int wardLife = 0;
        bool uncounterable = false;
        foreach (var rawLine in ReminderText().Replace(oracleText, "").Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('.');
            if (KickerLine().Match(line) is { Success: true } k) kicker = ManaCost.Parse(k.Groups[1].Value);
            if (FlashbackLine().Match(line) is { Success: true } f) flashback = ManaCost.Parse(f.Groups[1].Value);
            // Ward can share a line with other keywords ("Trample, ward {4}").
            foreach (var part in line.Split(", "))
            {
                var w = WardLine().Match(part.Trim());
                if (!w.Success) continue;
                if (w.Groups["mana"].Success) ward = ManaCost.Parse(w.Groups["mana"].Value);
                if (w.Groups["life"].Success) wardLife = int.Parse(w.Groups["life"].Value);
            }
            if (line == "This spell can't be countered") uncounterable = true;
        }
        return (kicker, flashback, ward, wardLife, uncounterable);
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

    [GeneratedRegex(@"^Kicker ((\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex KickerLine();

    [GeneratedRegex(@"^Flashback ((\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex FlashbackLine();

    [GeneratedRegex(@"^[Ww]ard(?: (?<mana>(\{[0-9WUBRGC]+\})+)|—(?:(?<mana>(\{[0-9WUBRGC]+\})+), )?[Pp]ay (?<life>\d+) life)$")]
    private static partial Regex WardLine();

    [GeneratedRegex(@"^(This land|This creature|This artifact|This permanent) enters tapped$")]
    private static partial Regex EntersTappedLine();
}
