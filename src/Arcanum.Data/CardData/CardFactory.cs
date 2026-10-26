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

    // A meld card is a card with one face; the combined back face is a record of its own (linked by CardDatabase).
    private static readonly HashSet<string> SingleFaceLayouts = new(StringComparer.Ordinal) { "normal", "token", "saga", "meld", "class" };

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
        "Lieutenant", "Enrage", "Celebration", "Pack tactics", "Imprint", "Living weapon", "Proliferate", "Discover", "Read ahead",
        "Tiered", // the script's modes carry their additional costs (rule 702.183)
        "Meld", // the script's "meld" effect, with "meldsWith" and "meldInto"
        "Boast", "Spectacle", "Overload", "Revolt", "Eminence", "Commander ninjutsu", "Ninjutsu", // scripted (activation limits, alternative costs)
        "Renown", // its trigger and the renowned designation come from the script (rule 702.112)
        "Support", "Adapt", "Monstrosity", "Explore", "Connive", "Incubate", "Forage", // keyword actions: the script's effects (targets for support)
        "Prepared", "Empower Jace", // the script's "entersPrepared", "becomePrepared" and "empowerJace"
        "Exhaust", // an activated ability the script marks "onlyOnce" (rule 702.177: activate each exhaust ability only once)
        "Companion", // the script's "companion" deck condition (revealed before the game, put into the hand for {3})
        "Manifest", // the script's "manifest" effect (face-down 2/2; a creature card turns face up for its mana cost)
        "Clash", // the script's "clash" effect with "ifWin" / "else"
        "Champion", // the script's "champion" enters trigger, its "returnLinkedExiled" leaves trigger and "championed" triggers
        "Blight", // the script's "blight" cost or effect (put N -1/-1 counters on a creature you control)
        "Blitz", // the script's "blitz" cost (with "blitzExtra", "blitzFromGraveyard")
        // Keywords a script turns on with a card-wide rule ("multikicker": "{2}", "storm": true …).
        "Dethrone", "Hideaway", "Heal", "Aftermath",
        "Devour", "Multikicker", "Replicate", "Escalate", "Improvise", "Myriad", "Squad", "Dash", "Splice", "Miracle", "Storm", "Undaunted", "Delve", "Conspire",
        // Pairing rules for two commanders (deck construction); "Partner with" also has a trigger derived below.
        "Partner", "Partner with", "Friends forever", "Choose a background", "Doctor's companion",
    };

    /// <summary>Keywords the engine implements. Grows as keyword support lands.</summary>
    /// <remarks>Includes keywords whose rules this factory derives from rules text (Equip, Enchant).</remarks>
    public static ISet<string> SupportedKeywords { get; } =
        new HashSet<string>(Engine.Cards.Keywords.SupportedNames.Concat(new[] { "Equip", "Enchant", "Kicker", "Flashback", "Ward", "Job select", "Convoke", "Unearth", "Rebound", "Foretell", "Morph", "Impending", "Offspring", "Evoke", "Encore", "Compleated", "Escape", "Toxic", "Vanishing", "Ravenous" }).Concat(ScriptedKeywords),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Why <see cref="Create"/> finds the card unsupported (for tools): each failed requirement, by face.</summary>
    public static IReadOnlyList<string> Explain(CardRecord record, Scripts.CardScript? script)
    {
        var reasons = new List<string>();
        _reasons = reasons;
        try { Create(record, script); }
        finally { _reasons = null; }
        return reasons;
    }

    [ThreadStatic] private static List<string>? _reasons;

    private static bool Note(bool ok, CardRecord record, string what)
    {
        if (!ok) _reasons?.Add($"{record.Name}: {what}");
        return ok;
    }

    /// <param name="script">The card's ability script from the content module, if it has one.</param>
    public static (CardDefinition Definition, CardSupport Support) Create(CardRecord record, Scripts.CardScript? script = null)
    {
        if (record.Layout == "adventure" && record.Faces.Count == 2) return CreateAdventurer(record, script);
        if (record.Layout == "prepare" && record.Faces.Count == 2) return CreatePreparationCard(record, script);
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
            Foretell = ForetellCost(record.OracleText),
            Morph = KeywordLineCost(record.OracleText, MorphLine()),
            Impending = KeywordLineCost(record.OracleText, ImpendingLine()),
            Offspring = KeywordLineCost(record.OracleText, OffspringLine()),
            Evoke = KeywordLineCost(record.OracleText, EvokeLine()) ?? (EvokeExileColor(record.OracleText) is not null ? ManaCost.Zero : null),
            EvokeExtra = EvokeExileColor(record.OracleText) is { } evokeColor
                ? new Engine.Abilities.ExtraCost { ExileFromHand = new Engine.Abilities.ObjectFilter(Colors: new[] { evokeColor }, Controller: Engine.Abilities.ControllerFilter.Any) } : null,
            ImpendingCount = ImpendingCount(record.OracleText),
            Escape = KeywordLineCost(record.OracleText, EscapeLine()),
            EscapeExtra = KeywordLineNumber(record.OracleText, EscapeLine()) is int escapeExiles and > 0 ? new Engine.Abilities.ExtraCost { ExileFromGraveyard = escapeExiles } : null,
            EscapesWithCounters = KeywordLineNumber(record.OracleText.Replace(record.Name, "~"), EscapesWithLine()) ?? 0,
            Toxic = KeywordLineNumber(record.OracleText, ToxicLine()) ?? 0,
            Vanishing = KeywordLineNumber(record.OracleText, VanishingLine()) ?? 0,
            Cascade = KeywordInstances(record.OracleText, "Cascade"),
            EntersWithXCounters = record.Keywords.Contains("Ravenous", StringComparer.OrdinalIgnoreCase), // 702.156a
            WardMana = derived.WardMana,
            WardLife = derived.WardLife,
            CantBeCountered = derived.CantBeCountered,
        };
        if (script is not null) definition = script.ApplyTo(definition);
        if (definition.Offspring is not null) definition = definition with { Abilities = definition.Abilities.Append(OffspringTrigger).ToList() };
        if (definition.Evoke is not null) definition = definition with { Abilities = definition.Abilities.Append(EvokeTrigger).ToList() };
        if (record.Keywords.Contains("Ravenous", StringComparer.OrdinalIgnoreCase)) definition = definition with { Abilities = definition.Abilities.Append(RavenousTrigger).ToList() };
        definition = definition with { Abilities = Engine.Abilities.AbilityDefinition.InClassLevelOrder(definition.Abilities) };

        // "*" power/toughness is fine when the script defines it (characteristic-defining ability).
        powerOk |= script?.PowerFrom is not null;
        toughnessOk |= script?.ToughnessFrom is not null;
        bool supported = Note(costOk, record, $"mana cost {record.ManaCost}") & Note(powerOk && toughnessOk, record, "power/toughness")
                         & Note(SingleFaceLayouts.Contains(record.Layout), record, $"layout {record.Layout}")
                         & record.Keywords.Aggregate(true, (ok, k) => Note(SupportedKeywords.Contains(k) || IsCycling(k) || IsLabel(record.OracleText.Replace(record.Name, "~"), k), record, $"keyword {k}") && ok)
                         & Note(script is not null || RulesTextIsCovered(record), record, "needs a script")
                         & Note(!MissesItsOwnProtection(record.OracleText, definition), record, "protection missing from the script")
                         & Note(!MissesItsRenown(record, definition), record, "renown missing from the script")
                         // A land with morph is cast face down from the hand, which a land card otherwise never is: not supported yet.
                         & Note(!(definition.Morph is not null && definition.Types.HasFlag(CardType.Land)), record, "morph on a land");
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
    /// A preparation card: the card is its first face, a creature in every zone; its second face is its prepare spell, described by the
    /// script's "prepare" part, which is only ever cast as a copy while the creature is prepared. Both show the card's picture.
    /// </summary>
    private static (CardDefinition Definition, CardSupport Support) CreatePreparationCard(CardRecord record, Scripts.CardScript? script)
    {
        CardRecord FaceRecord(CardFaceRecord face) => record with
        {
            Layout = "normal", Name = face.Name, ManaCost = face.ManaCost, TypeLine = face.TypeLine, OracleText = face.OracleText,
            Power = face.Power, Toughness = face.Toughness, Faces = Array.Empty<CardFaceRecord>(),
            // The card source lists the keywords of both faces together: each face keeps those its own text uses.
            Keywords = record.Keywords.Where(k => face.OracleText.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
        var (card, cardSupport) = Create(FaceRecord(record.Faces[0]), script);
        var (spell, spellSupport) = Create(FaceRecord(record.Faces[1]), script?.Prepare);
        var face = record.Faces[1];
        var definition = card with
        {
            OracleText = $"{card.OracleText}\n\n{face.Name} {face.ManaCost} ({face.TypeLine})\n{face.OracleText}".Trim(),
            ImageKey = record.DefaultPrintingId,
            PrepareSpell = spell with { ImageKey = record.DefaultPrintingId },
        };
        bool supported = cardSupport == CardSupport.Full && spellSupport == CardSupport.Full;
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
    /// <summary>A keyword line of the card's own ("Protection from blue", "Flying, protection from red").</summary>
    private static readonly System.Text.RegularExpressions.Regex OwnProtectionLine =
        new(@"^(?:[A-Za-z ]+, )*protection from ", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// The card data names only "Protection" as a keyword, not what from: a script must give it. A card whose rules text gives
    /// itself protection but whose script doesn't isn't fully supported (it would play without protection).
    /// </summary>
    private static bool MissesItsOwnProtection(string? oracleText, CardDefinition definition)
    {
        if (oracleText is null || !OwnProtectionLine.IsMatch(oracleText)) return false;
        bool IsProtection(Engine.Cards.Keyword k) => k.ToString().StartsWith("Protection", StringComparison.OrdinalIgnoreCase);
        return !(definition.KeywordAbilities.Any(IsProtection)
                 || definition.ProtectionFromSubtypes.Count > 0
                 || definition.Abilities.OfType<Engine.Abilities.StaticAbility>().Any(a =>
                        a.Affects.Scope == Engine.Abilities.AffectedScope.Self && (a.ProtectionFromRingBearers || (a.Keywords ?? Array.Empty<Engine.Cards.Keyword>()).Any(IsProtection))));
    }

    /// <summary>
    /// Renown is a keyword its script implements (a trigger that makes it renowned): a card with Renown whose script has no such
    /// trigger isn't fully supported.
    /// </summary>
    private static bool MissesItsRenown(CardRecord record, CardDefinition definition) =>
        record.Keywords.Contains("Renown", StringComparer.OrdinalIgnoreCase)
        && !definition.Abilities.OfType<Engine.Abilities.TriggeredAbility>().Any(a => a.Effects.OfType<Engine.Abilities.BecomeRenowned>().Any());

    private static bool RulesTextIsCovered(CardRecord record)
    {
        var text = ReminderText().Replace(record.OracleText, "");
        foreach (var rawLine in text.Split('\n'))
        {
            var line = WithoutLabel(rawLine.Trim().TrimEnd('.'));
            if (line.Length == 0) continue;
            if (line == "Job select") continue;
            if (TapForManaLine().IsMatch(line) || AnyColorManaLine().IsMatch(line)) continue;
            if (EntersTappedLine().IsMatch(line)) continue;
            if (KickerLine().IsMatch(line) || FlashbackLine().IsMatch(line) || WardLine().IsMatch(line) || line == "This spell can't be countered") continue;
            if (CyclingLine().IsMatch(line)) continue;
            if (UnearthLine().IsMatch(line) || ForetellLine().IsMatch(line) || MorphLine().IsMatch(line) || ImpendingLine().IsMatch(line) || OffspringLine().IsMatch(line)) continue;
            if (EvokeLine().IsMatch(line) || EvokeExileLine().IsMatch(line) || EncoreLine().IsMatch(line)) continue;
            if (EscapeLine().IsMatch(line) || EscapesWithLine().IsMatch(line.Replace(record.Name, "~")) || ToxicLine().IsMatch(line) || VanishingLine().IsMatch(line)) continue;
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
            // A double-faced token is listed by both faces' names ("Incubator // Phyrexian").
            var candidates = tokens.Where(t => t.Name.Equals(token.Name, StringComparison.OrdinalIgnoreCase)
                                               || t.Name.StartsWith(token.Name + " // ", StringComparison.OrdinalIgnoreCase)).ToList();
            var match = candidates.Count <= 1 ? candidates.FirstOrDefault() : candidates.FirstOrDefault(t => MatchesStats(t, token)) ?? candidates[0];
            return match is null ? token : token with { ImageKey = match.Id };
        }
        return (T)Engine.Abilities.EffectTree.Map(ability, effect => effect switch
        {
            Engine.Abilities.CreateTokens create => create with { Token = Pictured(create.Token) },
            Engine.Abilities.Amass amass => amass with { Token = Pictured(amass.Token) },
            Engine.Abilities.Recruit recruit => recruit with { Token = Pictured(recruit.Token) },
            Engine.Abilities.Incubate incubate => incubate with { Token = Pictured(incubate.Token) },
            _ => effect,
        });
    }

    /// <summary>
    /// The card as printed in <paramref name="printing"/>: same rules, with that printing's picture and the tokens
    /// printed alongside it.
    /// </summary>
    /// <param name="picture">The printing whose picture to show (the printing itself when null).</param>
    public static CardDefinition ForPrinting(CardDefinition definition, Printing printing, Printing? picture = null) => definition with
    {
        ImageKey = (picture ?? printing).Id,
        Spell = WithTokenImages(definition.Spell, printing.Tokens, replace: true),
        Abilities = definition.Abilities.Select(a => WithTokenImages(a, printing.Tokens, replace: true)!).ToList(),
        Adventure = definition.Adventure is { } adventure ? ForPrinting(adventure, printing, picture) : null,
        BackFace = definition.BackFace is { } back ? ForPrinting(back, printing, picture) : null,
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
            var labelled = rawLine.Trim().TrimEnd('.');
            var line = WithoutLabel(labelled);
            if (line == "Job select") abilities.Add(JobSelect());
            var equip = EquipLine().Match(line);
            if (equip.Success)
            {
                abilities.Add(new Engine.Abilities.ActivatedAbility
                {
                    Cost = new Engine.Abilities.AbilityCost(ManaCost.Parse(equip.Groups[1].Value)),
                    SorcerySpeed = true,
                    Targets = new[] { new Engine.Abilities.TargetSpec(Engine.Abilities.TargetKind.Creature, Engine.Abilities.ControllerFilter.You) },
                    Effects = new Engine.Abilities.Effect[] { new Engine.Abilities.AttachSelf(Engine.Abilities.Subject.TargetAt(0)) },
                    Text = labelled,
                    IsEquip = true,
                });
            }
            if (CyclingAbility(line) is { } cycling) abilities.Add(cycling);
            if (UnearthAbility(line) is { } unearth) abilities.Add(unearth);
            if (EncoreAbility(line) is { } encore) abilities.Add(encore);
            if (ForetellLine().Match(line) is { Success: true }) abilities.Add(ForetellAbility);
            if (PartnerWithAbility(line) is { } partnerWith) abilities.Add(partnerWith);
            var enchantMatch = EnchantLine().Match(line);
            if (enchantMatch.Success)
            {
                bool opponent = enchantMatch.Groups[1].Value == "opponent";
                var kind = opponent ? Engine.Abilities.TargetKind.Player : Enum.Parse<Engine.Abilities.TargetKind>(enchantMatch.Groups[1].Value, ignoreCase: true);
                var controller = opponent ? Engine.Abilities.ControllerFilter.Opponent
                    : enchantMatch.Groups[2].Success ? Engine.Abilities.ControllerFilter.You : Engine.Abilities.ControllerFilter.Any;
                enchant = new Engine.Abilities.TargetSpec(kind, controller);
            }
            if (EntersTappedLine().IsMatch(line)) entersTapped = true;
        }
        return (abilities, enchant, entersTapped);
    }

    /// <summary>
    /// Job select (rule 702.182): "When this Equipment enters, create a 1/1 colorless Hero creature token, then attach this Equipment to it."
    /// </summary>
    private static Engine.Abilities.TriggeredAbility JobSelect() => new()
    {
        Trigger = Engine.Abilities.TriggerEvent.EntersBattlefield,
        Effects = new Engine.Abilities.Effect[]
        {
            new Engine.Abilities.CreateTokens(HeroToken, 1, Engine.Abilities.Subject.You, false),
            new Engine.Abilities.AttachSelf(new Engine.Abilities.Subject(Engine.Abilities.SubjectKind.Created)),
        },
        Text = "Job select (When this Equipment enters, create a 1/1 colorless Hero creature token, then attach this to it.)",
    };

    /// <summary>The 1/1 colorless Hero creature token job select creates.</summary>
    public static CardDefinition HeroToken { get; } = new()
    {
        Name = "Hero", Types = CardType.Creature, Subtypes = new[] { "Hero" }, Power = 1, Toughness = 1, IsToken = true,
    };

    /// <summary>
    /// A flavor word or ability word the card source lists as a keyword ("Gae Bolg — Equip {4}", "Wings of Light — Put …"): it only
    /// names the ability it starts (rules 207.2c-d) and has no rules of its own. True when every line using it starts with it, before
    /// " — ", directly or after a Saga's chapter numbers.
    /// </summary>
    private static bool IsLabel(string oracleText, string keyword)
    {
        var word = new Regex($@"(?<![\w']){Regex.Escape(keyword)}(?![\w'])");
        var lines = ReminderText().Replace(oracleText, "").Split('\n').Where(l => word.IsMatch(l)).ToList();
        return lines.Count > 0 && lines.All(l => LabelOf(l.Trim()) is { } label && label == keyword);
    }

    /// <summary>The flavor or ability word a line starts with ("Gae Bolg — Equip {4}", "I, II — Wings of Light — Put …"), if any.</summary>
    private static string? LabelOf(string line)
    {
        var m = LabelledLine().Match(line);
        return m.Success ? m.Groups["label"].Value : null;
    }

    /// <summary>The line without a leading flavor or ability word ("Gae Bolg — Equip {4}" is "Equip {4}"); chapter lines stay as they are.</summary>
    private static string WithoutLabel(string line)
    {
        var m = LabelledLine().Match(line);
        return m.Success && !m.Groups["chapters"].Success && !line.StartsWith('•') ? line[m.Length..] : line;
    }

    [GeneratedRegex(@"^(?<chapters>[IVX]+(?:, [IVX]+)* — )?(?:• )?(?<label>[^—•]+?) — (?=\S)")]
    private static partial Regex LabelledLine();

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

    /// <summary>"Foretell [cost]": the cost of casting the card from exile on a later turn after foretelling it.</summary>
    /// <summary>The cost on a keyword line such as "Morph {3}{R}{R}{R}" or "Impending 4—{2}{W}{W}" (the cost is the line's last group).</summary>
    private static ManaCost? KeywordLineCost(string oracleText, Regex line) =>
        ReminderText().Replace(oracleText, "").Split('\n').Select(l => line.Match(WithoutLabel(l.Trim().TrimEnd('.'))))
            .FirstOrDefault(m => m.Success) is { } m ? ManaCost.Parse(m.Groups["cost"].Value) : null;

    /// <summary>The number on a keyword line such as "Toxic 2" or "Escape—{3}{B}{B}, Exile four other cards from your graveyard" (its "n" group).</summary>
    private static int? KeywordLineNumber(string oracleText, Regex line) =>
        ReminderText().Replace(oracleText, "").Split('\n').Select(l => line.Match(WithoutLabel(l.Trim().TrimEnd('.'))))
            .FirstOrDefault(m => m.Success) is { } m ? m.Groups["n"].Value is not { Length: > 0 } n ? 1 // "with a +1/+1 counter"
            : Array.IndexOf(NumberWords, n.ToLowerInvariant()) is int i and >= 0 ? i : int.Parse(n) : null;

    /// <summary>Instances of a keyword on its keyword lines: "Cascade, cascade" is two (rule 702.85c), "Spells you cast have cascade" none.</summary>
    private static int KeywordInstances(string oracleText, string keyword) =>
        ReminderText().Replace(oracleText, "").Split('\n')
            .Sum(l => l.Trim().TrimEnd('.').Split(',').Count(part => part.Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase)));

    private static readonly string[] NumberWords ={ "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

    /// <summary>N of "Impending N—[cost]".</summary>
    private static int ImpendingCount(string oracleText) =>
        ReminderText().Replace(oracleText, "").Split('\n').Select(l => ImpendingLine().Match(l.Trim()))
            .FirstOrDefault(m => m.Success) is { } m ? int.Parse(m.Groups["count"].Value) : 0;

    private static ManaCost? ForetellCost(string oracleText) =>
        ReminderText().Replace(oracleText, "").Split('\n').Select(l => ForetellLine().Match(WithoutLabel(l.Trim().TrimEnd('.'))))
            .FirstOrDefault(m => m.Success) is { } m ? ManaCost.Parse(m.Groups[1].Value) : null;

    /// <summary>
    /// Foretelling (rule 702.143a): "During your turn, you may pay {2} and exile this card from your hand face down" — a special action,
    /// any time its owner has priority during their turn.
    /// </summary>
    private static readonly Engine.Abilities.ActivatedAbility ForetellAbility = new()
    {
        Cost = new Engine.Abilities.AbilityCost(ManaCost.Parse("{2}")) { FromHand = true, ExileFaceDown = true },
        SpecialAction = true,
        ActivationCondition = new Engine.Abilities.YourTurn(),
        Effects = Array.Empty<Engine.Abilities.Effect>(),
        Text = "Foretell: pay {2} and exile this card from your hand face down.",
    };

    /// <summary>
    /// "Unearth [cost]" (rule 702.84a): "[Cost]: Return this card from your graveyard to the battlefield. It gains haste. Exile it at the
    /// beginning of the next end step. If it would leave the battlefield, exile it instead of putting it anywhere else. Activate only as a
    /// sorcery."
    /// </summary>
    /// <summary>"Encore [cost]" (rule 702.141a): "[Cost], Exile this card from your graveyard: …" as a sorcery.</summary>
    private static Engine.Abilities.ActivatedAbility? EncoreAbility(string line)
    {
        var m = EncoreLine().Match(line);
        if (!m.Success) return null;
        return new Engine.Abilities.ActivatedAbility
        {
            Cost = new Engine.Abilities.AbilityCost(ManaCost.Parse(m.Groups["cost"].Value), false, true) { FromGraveyard = true },
            SorcerySpeed = true,
            Effects = new Engine.Abilities.Effect[] { new Engine.Abilities.Encore() },
            Text = line,
        };
    }

    [GeneratedRegex(@"^Encore (?<cost>(\{[0-9WUBRGC/]+\})+)$")]
    private static partial Regex EncoreLine();

    private static Engine.Abilities.ActivatedAbility? UnearthAbility(string line)
    {
        var m = UnearthLine().Match(line);
        if (!m.Success) return null;
        return new Engine.Abilities.ActivatedAbility
        {
            Cost = new Engine.Abilities.AbilityCost(ManaCost.Parse(m.Groups[1].Value)) { FromGraveyard = true },
            SorcerySpeed = true,
            Effects = new Engine.Abilities.Effect[]
            {
                new Engine.Abilities.PutOntoBattlefield(Engine.Abilities.Subject.Self) { ExileIfLeaves = true, AddKeywords = new[] { Engine.Cards.Keyword.Haste } },
                new Engine.Abilities.AtNextEndStepAbout(Engine.Abilities.Subject.Self, new Engine.Abilities.Effect[] { new Engine.Abilities.ExileIt(Engine.Abilities.Subject.Triggered) }),
            },
            Text = line,
        };
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

    [GeneratedRegex(@"^Enchant (creature|land|artifact|enchantment|permanent|player|opponent)( you control)?$")]
    private static partial Regex EnchantLine();

    [GeneratedRegex(@"^Kicker ((\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex KickerLine();

    [GeneratedRegex(@"^Foretell ((\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex ForetellLine();

    [GeneratedRegex(@"^Unearth ((\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex UnearthLine();

    [GeneratedRegex(@"^Morph (?<cost>(\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex MorphLine();

    [GeneratedRegex(@"^Offspring (?<cost>(\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex OffspringLine();

    /// <summary>Offspring's trigger (rule 702.175a), added to every card with an offspring cost.</summary>
    private static readonly Engine.Abilities.AbilityDefinition OffspringTrigger = Scripts.CardScriptParser.Parse("""
        { "abilities": [ { "trigger": "enters", "if": { "atLeast": "offspringPaid", "value": 1 },
          "effects": [ { "copy": "self", "setPower": 1, "setToughness": 1 } ],
          "text": "Offspring — When this creature enters, if its offspring cost was paid, create a token that's a copy of it, except it's 1/1." } ] }
        """).Abilities[0];

    [GeneratedRegex(@"^Evoke (?<cost>(\{[0-9WUBRGC/]+\})+)$")]
    private static partial Regex EvokeLine();

    [GeneratedRegex(@"^Evoke—Exile an? (?<color>white|blue|black|red|green) card from your hand$")]
    private static partial Regex EvokeExileLine();

    /// <summary>The color of "Evoke—Exile a green card from your hand" (W, U, B, R or G), or null.</summary>
    private static string? EvokeExileColor(string oracleText) =>
        ReminderText().Replace(oracleText, "").Split('\n').Select(l => EvokeExileLine().Match(l.Trim().TrimEnd('.'))).FirstOrDefault(m => m.Success) is { } m
            ? m.Groups["color"].Value switch { "white" => "W", "blue" => "U", "black" => "B", "red" => "R", _ => "G" } : null;

    /// <summary>Evoke's trigger (rule 702.74a), added to every card with an evoke cost.</summary>
    private static readonly Engine.Abilities.AbilityDefinition EvokeTrigger = Scripts.CardScriptParser.Parse("""
        { "abilities": [ { "trigger": "enters", "if": "evoked", "effects": [ { "sacrificeIt": "self" } ],
          "text": "Evoke — When this creature enters, if its evoke cost was paid, its controller sacrifices it." } ] }
        """).Abilities[0];

    [GeneratedRegex(@"^Escape—(?<cost>(\{[0-9WUBRGC]+\})+), Exile (?<n>[a-z]+|[0-9]+) other cards from your graveyard$")]
    private static partial Regex EscapeLine();

    [GeneratedRegex(@"^(?:~|This creature|This permanent) escapes with (?:an?|(?<n>[a-z]+|[0-9]+)) \+1/\+1 counters? on it$")]
    private static partial Regex EscapesWithLine();

    [GeneratedRegex(@"^Toxic (?<n>[0-9]+)$")]
    private static partial Regex ToxicLine();

    [GeneratedRegex(@"^Vanishing (?<n>[0-9]+)$")]
    private static partial Regex VanishingLine();

    /// <summary>Ravenous's trigger (rule 702.156a); its "enters with X +1/+1 counters" is <see cref="Engine.Cards.CardDefinition.EntersWithXCounters"/>.</summary>
    private static readonly Engine.Abilities.AbilityDefinition RavenousTrigger = Scripts.CardScriptParser.Parse("""
        { "abilities": [ { "trigger": "enters", "if": { "atLeast": "X", "value": 5 }, "effects": [ { "draw": 1 } ],
          "text": "Ravenous — When this permanent enters, if X is 5 or more, draw a card." } ] }
        """).Abilities[0];

    [GeneratedRegex(@"^Impending (?<count>[0-9]+)—(?<cost>(\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex ImpendingLine();

    [GeneratedRegex(@"^Flashback ((\{[0-9WUBRGC]+\})+)$")]
    private static partial Regex FlashbackLine();

    [GeneratedRegex(@"^[Ww]ard(?: (?<mana>(\{[0-9WUBRGC]+\})+)|—(?:(?<mana>(\{[0-9WUBRGC]+\})+), )?[Pp]ay (?<life>\d+) life)$")]
    private static partial Regex WardLine();

    [GeneratedRegex(@"^(This land|This creature|This artifact|This permanent) enters tapped$")]
    private static partial Regex EntersTappedLine();
}
