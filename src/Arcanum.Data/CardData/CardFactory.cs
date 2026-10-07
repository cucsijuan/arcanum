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

    private static readonly HashSet<string> SingleFaceLayouts = new(StringComparer.Ordinal) { "normal", "token", "saga" };

    /// <summary>
    /// Keyword actions and ability words the card source lists as keywords but that only label rules text a card
    /// script spells out (scry, raid...). A card with them is supported once it has a script.
    /// </summary>
    private static readonly string[] ScriptedKeywords =
    {
        "Scry", "Surveil", "Fight", "Mill", "Treasure", "Food", "Investigate",
        "Raid", "Landfall", "Morbid", "Threshold", "Ferocious", "Hexproof from", "Affinity", "Double", "Formidable", "Alliance", "Crew", "Protection", "Vivid",
        "Amass", "Recruit", "Gift", "Behold", "Landwalk", "Goad",
        "Revolt", "Delirium", "Battalion", "Fateful hour", "Spell mastery", "Addendum", "Regenerate", "Triple", "Populate", "Exert",
        "Secret council", "Will of the council", "Council's dilemma", "Tempting offer", "Transform",
        // Keywords a script turns on with a card-wide rule ("multikicker": "{2}", "storm": true …).
        "Persist", "Undying", "Dethrone", "Hideaway", "Heal", "Aftermath",
        "Devour", "Multikicker", "Replicate", "Squad", "Dash", "Splice", "Miracle", "Storm", "Undaunted", "Delve", "Conspire",
        // Pairing rules for two commanders (deck construction); "Partner with" also has a trigger derived below.
        "Partner", "Partner with", "Friends forever", "Choose a background", "Doctor's companion",
    };

    /// <summary>Keywords the engine implements. Grows as keyword support lands.</summary>
    /// <remarks>Includes keywords whose rules this factory derives from rules text (Equip, Enchant).</remarks>
    public static ISet<string> SupportedKeywords { get; } =
        new HashSet<string>(Engine.Cards.Keywords.SupportedNames.Concat(new[] { "Equip", "Enchant", "Kicker", "Flashback", "Ward" }).Concat(ScriptedKeywords),
            StringComparer.OrdinalIgnoreCase);

    /// <param name="script">The card's ability script from the content module, if it has one.</param>
    public static (CardDefinition Definition, CardSupport Support) Create(CardRecord record, Scripts.CardScript? script = null)
    {
        if (record.Layout == "adventure" && record.Faces.Count == 2) return CreateAdventurer(record, script);
        if (record.Layout == "split" && record.Faces.Count == 2) return CreateSplit(record, script);
        if (record.Layout == "transform" && record.Faces.Count == 2) return CreateTransforming(record, script);
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
            Loyalty = int.TryParse(record.Loyalty, out int loyalty) ? loyalty : null,
            OracleText = record.OracleText,
            ColorIdentity = record.ColorIdentity,
            Keywords = PrintedKeywords(record),
            TapForMana = tapForMana,
            Spell = WithTokenImages(script?.Spell, record.RelatedTokens),
            Abilities = derivedAbilities.Concat((script?.Abilities ?? Array.Empty<Engine.Abilities.AbilityDefinition>()).Select(a => WithTokenImages(a, record.RelatedTokens)!)).ToList(),
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

        // "*" power/toughness is fine when the script defines it (characteristic-defining ability).
        powerOk |= script?.PowerFrom is not null;
        toughnessOk |= script?.ToughnessFrom is not null;
        bool supported = costOk && powerOk && toughnessOk
                         && SingleFaceLayouts.Contains(record.Layout)
                         && record.Keywords.All(k => SupportedKeywords.Contains(k) || IsCycling(k))
                         && (script is not null || RulesTextIsCovered(record));
        return (definition, supported ? CardSupport.Full : CardSupport.Unsupported);
    }

    /// <summary>
    /// An adventurer card (rule 715): the card is its first face; its second face is the Adventure, an instant or sorcery
    /// described by the script's "adventure" part. Both show the card's picture.
    /// </summary>
    private static (CardDefinition Definition, CardSupport Support) CreateAdventurer(CardRecord record, Scripts.CardScript? script)
    {
        CardRecord FaceRecord(CardFaceRecord face) => record with
        {
            Layout = "normal", Name = face.Name, ManaCost = face.ManaCost, TypeLine = face.TypeLine, OracleText = face.OracleText,
            Power = face.Power, Toughness = face.Toughness, Faces = Array.Empty<CardFaceRecord>(),
            // The card source lists the keywords of both faces together: each face keeps those its own text uses.
            Keywords = record.Keywords.Where(k => face.OracleText.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
        var (card, cardSupport) = Create(FaceRecord(record.Faces[0]), script);
        var (adventure, adventureSupport) = Create(FaceRecord(record.Faces[1]), script?.Adventure);
        var face = record.Faces[1];
        var definition = card with
        {
            OracleText = $"{card.OracleText}\n\n{face.Name} {face.ManaCost} ({face.TypeLine})\n{face.OracleText}".Trim(),
            ImageKey = record.DefaultPrintingId,
            Adventure = adventure with { ImageKey = record.DefaultPrintingId },
        };
        bool supported = cardSupport == CardSupport.Full && adventureSupport == CardSupport.Full;
        return (definition, supported ? CardSupport.Full : CardSupport.Unsupported);
    }

    /// <summary>
    /// A nonmodal double-faced card (rule 712.2): the card is its front face, with its back face beside it. The script describes
    /// the front face as usual and the back face under <c>"back": { … }</c> (a script of its own). The back face has no mana
    /// cost; its colors are those of its color indicator. Supported only when both faces are.
    /// </summary>
    private static (CardDefinition Definition, CardSupport Support) CreateTransforming(CardRecord record, Scripts.CardScript? script)
    {
        CardRecord FaceRecord(CardFaceRecord face) => record with
        {
            Layout = "normal", Name = face.Name, ManaCost = face.ManaCost, TypeLine = face.TypeLine, OracleText = face.OracleText,
            Power = face.Power, Toughness = face.Toughness, Loyalty = face.Loyalty, Faces = Array.Empty<CardFaceRecord>(),
            // The card source lists the keywords of both faces together: each face keeps those its own text uses.
            Keywords = record.Keywords.Where(k => face.OracleText.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
        var (front, frontSupport) = Create(FaceRecord(record.Faces[0]), script);
        var (back, backSupport) = Create(FaceRecord(record.Faces[1]), script?.Back);
        // A face without a mana cost has the colors of its color indicator (rule 202.2e); with one, its cost's.
        if (string.IsNullOrEmpty(record.Faces[1].ManaCost)) back = back with { Colors = record.Faces[1].Colors };
        var definition = front with { BackFace = back };
        bool supported = frontSupport == CardSupport.Full && backSupport == CardSupport.Full;
        return (definition, supported ? CardSupport.Full : CardSupport.Unsupported);
    }

    /// <summary>
    /// A split card (rule 709): two halves, each castable on its own; everywhere but the stack the card has both halves'
    /// characteristics combined. An aftermath half (702.127) can be cast only from a graveyard, and is exiled afterwards.
    /// The script describes the halves under "split": [ { … }, { … } ].
    /// </summary>
    private static (CardDefinition Definition, CardSupport Support) CreateSplit(CardRecord record, Scripts.CardScript? script)
    {
        var halves = new List<CardDefinition>();
        bool supported = script?.Split is { Count: 2 };
        for (int i = 0; i < 2; i++)
        {
            var face = record.Faces[i];
            var faceRecord = record with
            {
                Layout = "normal", Name = face.Name, ManaCost = face.ManaCost, TypeLine = face.TypeLine, OracleText = face.OracleText,
                Power = face.Power, Toughness = face.Toughness, Faces = Array.Empty<CardFaceRecord>(),
                Keywords = record.Keywords.Where(k => face.OracleText.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList(),
            };
            var (half, halfSupport) = Create(faceRecord, script?.Split is { Count: 2 } split ? split[i] : null);
            supported &= halfSupport == CardSupport.Full;
            halves.Add(half with { ImageKey = record.DefaultPrintingId, Aftermath = face.OracleText.StartsWith("Aftermath", StringComparison.Ordinal) });
        }
        var (supertypes, types, subtypes) = TypeLine.Parse(record.TypeLine);
        var definition = new CardDefinition
        {
            Name = record.Name,
            OracleId = record.OracleId,
            ManaCost = halves[0].ManaCost.Plus(halves[1].ManaCost),
            Types = types,
            Supertypes = supertypes,
            Subtypes = subtypes,
            ColorIdentity = record.ColorIdentity,
            OracleText = $"{halves[0].Name} {record.Faces[0].ManaCost}\n{record.Faces[0].OracleText}\n//\n{halves[1].Name} {record.Faces[1].ManaCost}\n{record.Faces[1].OracleText}",
            Keywords = PrintedKeywords(record),
            SplitHalves = halves,
            ImageKey = record.DefaultPrintingId,
        };
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
            if (CyclingLine().IsMatch(line)) continue;
            if (CommanderPairingLine().IsMatch(line)) continue;
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
    private static T? WithTokenImages<T>(T? ability, IReadOnlyList<RelatedToken> tokens, bool replace = false) where T : Engine.Abilities.AbilityDefinition
    {
        if (ability is null || tokens.Count == 0) return ability;
        CardDefinition Pictured(CardDefinition token)
        {
            if (token.ImageKey is not null && !replace) return token;
            var candidates = tokens.Where(t => t.Name.Equals(token.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var match = candidates.Count <= 1 ? candidates.FirstOrDefault() : candidates.FirstOrDefault(t => MatchesStats(t, token)) ?? candidates[0];
            return match is null ? token : token with { ImageKey = match.Id };
        }
        return (T)Engine.Abilities.EffectTree.Map(ability, effect => effect switch
        {
            Engine.Abilities.CreateTokens create => create with { Token = Pictured(create.Token) },
            Engine.Abilities.Amass amass => amass with { Token = Pictured(amass.Token) },
            _ => effect,
        });
    }

    /// <summary>
    /// The card as printed in <paramref name="printing"/>: same rules, with that printing's picture and the tokens
    /// printed alongside it.
    /// </summary>
    public static CardDefinition ForPrinting(CardDefinition definition, Printing printing) => definition with
    {
        ImageKey = printing.Id,
        Spell = WithTokenImages(definition.Spell, printing.Tokens, replace: true),
        Abilities = definition.Abilities.Select(a => WithTokenImages(a, printing.Tokens, replace: true)!).ToList(),
        Adventure = definition.Adventure is { } adventure ? ForPrinting(adventure, printing) : null,
        BackFace = definition.BackFace is { } back ? ForPrinting(back, printing) : null,
    };

    /// <summary>
    /// Keywords the card really has. The card source lists "Hexproof" next to "Hexproof from" even when the card
    /// only has the "hexproof from …" form; that card doesn't have plain hexproof.
    /// </summary>
    private static IReadOnlyList<string> PrintedKeywords(CardRecord record)
    {
        bool hexproofFrom = record.Keywords.Contains("Hexproof from", StringComparer.OrdinalIgnoreCase);
        if (!hexproofFrom || PlainHexproof().IsMatch(ReminderText().Replace(record.OracleText, ""))) return record.Keywords;
        return record.Keywords.Where(k => !k.Equals("Hexproof", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>"Hexproof" on its own (a keyword line or list), not followed by "from".</summary>
    [GeneratedRegex(@"(^|, )[Hh]exproof(?! from)\s*(,|$)", RegexOptions.Multiline)]
    private static partial Regex PlainHexproof();

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
                    IsEquip = true,
                });
            }
            if (CyclingAbility(line) is { } cycling) abilities.Add(cycling);
            if (PartnerWithAbility(line) is { } partnerWith) abilities.Add(partnerWith);
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

    /// <summary>Cycling and typecycling keywords ("Cycling", "Mountaincycling", "Landcycling"): derived from their rules text.</summary>
    private static bool IsCycling(string keyword) => keyword.EndsWith("cycling", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "Cycling {2}" ({2}, discard this card: draw a card) and "[Type]cycling {2}" ({2}, discard this card: search your
    /// library for a [type] card, reveal it, put it into your hand, then shuffle), activated from the hand (rule 702.29).
    /// </summary>
    private static Engine.Abilities.ActivatedAbility? CyclingAbility(string line)
    {
        var m = CyclingLine().Match(line);
        if (!m.Success) return null;
        var cost = new Engine.Abilities.AbilityCost(ManaCost.Parse(m.Groups["cost"].Value)) { FromHand = true };
        var type = m.Groups["type"].Value.Trim();
        Engine.Abilities.Effect effect;
        if (type.Length == 0) effect = new Engine.Abilities.DrawCards(1, Engine.Abilities.Subject.You);
        else
        {
            var filter = type.Equals("basic land", StringComparison.OrdinalIgnoreCase)
                ? new Engine.Abilities.ObjectFilter(CardType.Land, Supertype: Supertype.Basic, Controller: Engine.Abilities.ControllerFilter.Any)
                : new Engine.Abilities.ObjectFilter(Subtype: char.ToUpperInvariant(type[0]) + type[1..], Controller: Engine.Abilities.ControllerFilter.Any);
            effect = new Engine.Abilities.SearchLibrary(filter, 1, Engine.State.Zone.Hand) { Reveal = true };
        }
        return new Engine.Abilities.ActivatedAbility { Cost = cost, Effects = new[] { effect }, Text = line };
    }

    /// <summary>
    /// "Partner with [name]": when this creature enters, target player may put [name] into their hand from their
    /// library, then shuffle (rule 702.124j).
    /// </summary>
    private static Engine.Abilities.TriggeredAbility? PartnerWithAbility(string line)
    {
        if (!line.StartsWith("Partner with ", StringComparison.Ordinal)) return null;
        var name = line[13..].Trim();
        return new Engine.Abilities.TriggeredAbility
        {
            Trigger = Engine.Abilities.TriggerEvent.EntersBattlefield,
            Targets = new[] { new Engine.Abilities.TargetSpec(Engine.Abilities.TargetKind.Player, Engine.Abilities.ControllerFilter.Any) },
            Effects = new Engine.Abilities.Effect[]
            {
                new Engine.Abilities.SearchLibrary(new Engine.Abilities.ObjectFilter(Name: name, Controller: Engine.Abilities.ControllerFilter.Any), 1, Engine.State.Zone.Hand)
                {
                    Who = Engine.Abilities.Subject.TargetAt(0), Optional = true,
                },
            },
            Text = $"{line} (When this creature enters, target player may put {name} into their hand from their library, then shuffle.)",
        };
    }

    /// <summary>Lines that only matter for having two commanders: partner (with a name or a variant), friends forever, Backgrounds, Doctors.</summary>
    [GeneratedRegex(@"^(Partner|Partner with .+|Partner\u2014.+|Friends forever|Choose a Background|Doctor's companion)$")]
    private static partial Regex CommanderPairingLine();

    [GeneratedRegex(@"^(?<type>[A-Za-z ]*?)[Cc]ycling (?<cost>(\{[0-9WUBRGCX]+\})+)$")]
    private static partial Regex CyclingLine();

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
