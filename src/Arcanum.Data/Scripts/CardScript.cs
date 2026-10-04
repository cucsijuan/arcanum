// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Arcanum.Data.CardData;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;
using Replacements = Arcanum.Engine.Cards.Replacements;
using EnterChoice = Arcanum.Engine.Cards.EnterChoice;
using CardType = Arcanum.Engine.Cards.CardType;
using ManaOption = Arcanum.Engine.Cards.ManaOption;

namespace Arcanum.Data.Scripts;

/// <summary>Abilities parsed from a card script: the spell effect, the permanent's abilities and card-wide rules.</summary>
public sealed record CardScript(
    SpellAbility? Spell,
    IReadOnlyList<AbilityDefinition> Abilities,
    TargetSpec? Aura = null,
    bool EntersTapped = false,
    int EntersWithCounters = 0)
{
    public bool CantBeCountered { get; init; }
    public bool GivesControllerHexproof { get; init; }
    public IReadOnlyList<string> HexproofFrom { get; init; } = Array.Empty<string>();
    public bool PlayersCantGainLife { get; init; }
    public ManaCost? Kicker { get; init; }
    public ManaCost? Flashback { get; init; }
    public ManaCost? WardMana { get; init; }
    public int WardLife { get; init; }
    public ExtraCost? AdditionalCost { get; init; }
    public CostReduction? CostReduction { get; init; }
    public bool AttacksEachCombat { get; init; }
    public bool DoesntUntap { get; init; }
    public bool EntersWithXCounters { get; init; }
    public Quantity? PowerFrom { get; init; }
    public Quantity? ToughnessFrom { get; init; }
    public ObjectFilter? CantBeBlockedBy { get; init; }
    public Replacements Replaces { get; init; }
    public Condition? EntersWithCountersIf { get; init; }
    public bool OntoBattlefieldIfOpponentMakesYouDiscard { get; init; }
    public IReadOnlyList<ManaType>? TapForMana { get; init; }
    public int ManaAmount { get; init; } = 1;
    public Quantity? ManaAmountFrom { get; init; }
    public bool ManaFromChosenColor { get; init; }
    public EnterChoice ChooseOnEnter { get; init; }
    public CounterKind? CountersPerChosenType { get; init; }
    public Quantity? EntersWithCountersFrom { get; init; }
    public CardType HexproofFromTypes { get; init; }
    public IReadOnlyList<CostOption>? AdditionalCostOptions { get; init; }
    public AlternativeCost? AlternativeCost { get; init; }
    public ManaCost? FlashExtraCost { get; init; }
    public bool StartsOnBattlefield { get; init; }
    public ManaRider ManaRider { get; init; }
    public ObjectFilter? ManaOnlyFor { get; init; }
    public CounterKind EntersWithCounterKind { get; init; } = CounterKind.PlusOnePlusOne;
    public ExtraCost? GraveyardCastCost { get; init; }
    public IReadOnlyList<ManaOption>? ExtraMana { get; init; }

    /// <summary>Applies the card-wide rules of this script to a definition.</summary>
    public CardDefinition ApplyTo(CardDefinition d) => d with
    {
        CantBeCountered = d.CantBeCountered || CantBeCountered,
        GivesControllerHexproof = GivesControllerHexproof,
        HexproofFromColors = HexproofFrom.Count > 0 ? HexproofFrom : d.HexproofFromColors,
        PlayersCantGainLife = PlayersCantGainLife,
        Kicker = Kicker ?? d.Kicker,
        Flashback = Flashback ?? d.Flashback,
        WardMana = WardMana ?? d.WardMana,
        WardLife = WardLife > 0 ? WardLife : d.WardLife,
        AdditionalCost = AdditionalCost ?? d.AdditionalCost,
        SelfCostReduction = CostReduction ?? d.SelfCostReduction,
        AttacksEachCombat = d.AttacksEachCombat || AttacksEachCombat,
        DoesntUntap = DoesntUntap,
        EntersWithXCounters = EntersWithXCounters,
        PowerFrom = PowerFrom ?? d.PowerFrom,
        ToughnessFrom = ToughnessFrom ?? d.ToughnessFrom,
        CantBeBlockedBy = CantBeBlockedBy ?? d.CantBeBlockedBy,
        Replaces = d.Replaces | Replaces,
        EntersWithCountersIf = EntersWithCountersIf ?? d.EntersWithCountersIf,
        OntoBattlefieldIfOpponentMakesYouDiscard = OntoBattlefieldIfOpponentMakesYouDiscard,
        TapForMana = TapForMana ?? d.TapForMana,
        ManaAmount = ManaAmount,
        ManaAmountFrom = ManaAmountFrom,
        ManaFromChosenColor = ManaFromChosenColor,
        ChooseOnEnter = ChooseOnEnter,
        CountersPerChosenType = CountersPerChosenType,
        EntersWithCountersFrom = EntersWithCountersFrom ?? d.EntersWithCountersFrom,
        HexproofFromTypes = HexproofFromTypes,
        AdditionalCostOptions = AdditionalCostOptions,
        AlternativeCost = AlternativeCost,
        FlashExtraCost = FlashExtraCost,
        StartsOnBattlefieldFromOpeningHand = StartsOnBattlefield,
        ManaRider = ManaRider,
        ManaOnlyFor = ManaOnlyFor,
        EntersWithCounterKind = EntersWithCounterKind,
        GraveyardCastCost = GraveyardCastCost,
        ExtraManaOptions = ExtraMana ?? d.ExtraManaOptions,
    };
}

/// <summary>
/// Parses card scripts: small JSON documents describing what a card does. Example:
/// <code>
/// { "spell": { "targets": ["any"], "effects": [{ "damage": 2, "to": "target" }] } }
/// { "abilities": [{ "trigger": "enters", "effects": [{ "draw": 1 }], "text": "When this enters, draw a card." }] }
/// { "abilities": [{ "cost": "{T}", "targets": ["any"], "effects": [{ "damage": 1, "to": "target" }] }] }
/// </code>
/// The full vocabulary is documented in docs/card-scripts.md.
/// </summary>
public static class CardScriptParser
{
    public static CardScript Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        SpellAbility? spell = null;
        if (root.TryGetProperty("spell", out var s))
            spell = WithModes(s, new SpellAbility { Targets = Targets(s), Effects = Effects(s), Text = Text(s), ExileAfterResolving = Bool(s, "exileAfter") });

        var abilities = new List<AbilityDefinition>();
        if (root.TryGetProperty("abilities", out var list))
        {
            foreach (var a in list.EnumerateArray())
            {
                if (a.TryGetProperty("static", out var st))
                {
                    abilities.Add(ParseStatic(st) with { Text = Text(a) });
                }
                else if (a.TryGetProperty("spellCost", out var sc))
                {
                    abilities.Add(new SpellCostReduction(ParseFilter(sc.GetProperty("spells")), sc.GetProperty("amount").GetInt32()) { Text = Text(a) });
                }
                else if (a.TryGetProperty("trigger", out var trigger))
                {
                    abilities.Add(WithModes(a, new TriggeredAbility
                    {
                        Trigger = ParseTrigger(trigger.GetString()!), Targets = Targets(a), Effects = Effects(a), Text = Text(a),
                        Filter = a.TryGetProperty("filter", out var filter) ? ParseFilter(filter, ControllerFilter.You) : null,
                        Condition = a.TryGetProperty("if", out var condition) ? ParseCondition(condition) : null,
                        TriggerCondition = a.TryGetProperty("when", out var when) ? ParseCondition(when) : null,
                        NthOfTurn = a.TryGetProperty("nth", out var nth) ? nth.GetInt32() : null,
                        OnSelf = Bool(a, "onSelf"),
                        OncePerTurn = Bool(a, "oncePerTurn"),
                        TargetsSource = Bool(a, "targetsSource"),
                        PlacedByYou = Bool(a, "placedByYou"),
                        FromGraveyard = Bool(a, "fromGraveyard"),
                        CounterKind = a.TryGetProperty("counterKind", out var tck) ? ParseCounterKind(tck.GetString()) : CounterKind.PlusOnePlusOne,
                    }));
                }
                else if (a.TryGetProperty("cost", out var cost))
                {
                    abilities.Add(WithModes(a, new ActivatedAbility
                    {
                        Cost = ParseCost(cost.GetString()!), Targets = Targets(a), Effects = Effects(a), Text = Text(a),
                        SorcerySpeed = Bool(a, "sorcery"),
                        OncePerTurn = Bool(a, "oncePerTurn"),
                        OnlyOnce = Bool(a, "onlyOnce"),
                        ActivationCondition = a.TryGetProperty("activateIf", out var ifc) ? ParseCondition(ifc) : null,
                    }));
                }
                else throw new FormatException("An ability needs a \"trigger\", a \"cost\" or a \"static\".");
            }
        }
        return new CardScript(
            spell,
            abilities,
            root.TryGetProperty("aura", out var aura) ? ParseTarget(aura) : null,
            Bool(root, "entersTapped"),
            root.TryGetProperty("entersWithCounters", out var counters) && counters.ValueKind == JsonValueKind.Number ? counters.GetInt32() : 0)
        {
            EntersWithXCounters = root.TryGetProperty("entersWithCounters", out var xc) && xc.ValueKind == JsonValueKind.String && xc.GetString() == "X",
            CantBeCountered = Bool(root, "uncounterable"),
            GivesControllerHexproof = Bool(root, "givesHexproof"),
            HexproofFrom = root.TryGetProperty("hexproofFrom", out var hf) ? hf.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>(),
            PlayersCantGainLife = Bool(root, "playersCantGainLife"),
            Kicker = root.TryGetProperty("kicker", out var kicker) ? ManaCost.Parse(kicker.GetString()!) : null,
            Flashback = root.TryGetProperty("flashback", out var flashback) ? ManaCost.Parse(flashback.GetString()!) : null,
            WardMana = root.TryGetProperty("ward", out var ward) ? ManaCost.Parse(ward.GetString()!) : null,
            WardLife = root.TryGetProperty("wardLife", out var wardLife) ? wardLife.GetInt32() : 0,
            AdditionalCost = root.TryGetProperty("additionalCost", out var extra) ? ParseExtraCost(extra) : null,
            CostReduction = root.TryGetProperty("costReduction", out var cr) ? ParseCostReduction(cr) : null,
            AttacksEachCombat = Bool(root, "attacksEachCombat"),
            DoesntUntap = Bool(root, "doesntUntap"),
            PowerFrom = root.TryGetProperty("powerFrom", out var pf) ? ParseQuantity(pf) : null,
            ToughnessFrom = root.TryGetProperty("toughnessFrom", out var tf) ? ParseQuantity(tf) : null,
            CantBeBlockedBy = root.TryGetProperty("cantBeBlockedBy", out var cbb) ? ParseFilter(cbb, ControllerFilter.Any) : null,
            EntersWithCountersIf = root.TryGetProperty("entersWithCountersIf", out var ewc) ? ParseCondition(ewc) : null,
            OntoBattlefieldIfOpponentMakesYouDiscard = Bool(root, "ontoBattlefieldIfDiscarded"),
            TapForMana = root.TryGetProperty("tapForMana", out var tfm) ? ParseManaTypes(tfm.GetString()!) : null,
            ManaAmount = root.TryGetProperty("manaAmount", out var mam) ? mam.GetInt32() : 1,
            ManaAmountFrom = root.TryGetProperty("manaAmountFrom", out var maf) ? ParseQuantity(maf) : null,
            ManaFromChosenColor = Bool(root, "manaFromChosenColor"),
            ChooseOnEnter = root.TryGetProperty("chooseOnEnter", out var coe) ? Enum.Parse<EnterChoice>(coe.GetString()!, ignoreCase: true) : EnterChoice.None,
            CountersPerChosenType = root.TryGetProperty("countersPerChosenType", out var cpt) ? ParseCounterKind(cpt.GetString()) : null,
            HexproofFromTypes = root.TryGetProperty("hexproofFromTypes", out var hft) ? ParseTypes(hft) : 0,
            AdditionalCostOptions = root.TryGetProperty("additionalCostOptions", out var aco)
                ? aco.EnumerateArray().Select(o => new CostOption(o.TryGetProperty("mana", out var om) ? ManaCost.Parse(om.GetString()!) : null,
                    o.TryGetProperty("cost", out var oc) ? ParseExtraCost(oc) : null)).ToList()
                : null,
            AlternativeCost = root.TryGetProperty("alternativeCost", out var alt)
                ? new AlternativeCost(ManaCost.Parse(alt.GetProperty("cost").GetString()!), alt.TryGetProperty("if", out var altIf) ? ParseCondition(altIf) : null)
                : null,
            FlashExtraCost = root.TryGetProperty("flashExtraCost", out var fec) ? ManaCost.Parse(fec.GetString()!) : null,
            StartsOnBattlefield = Bool(root, "startsOnBattlefield"),
            ManaRider = root.TryGetProperty("manaRider", out var mr) ? Enum.Parse<ManaRider>(mr.GetString()!, ignoreCase: true) : ManaRider.None,
            ManaOnlyFor = root.TryGetProperty("manaOnlyFor", out var mof) ? ParseFilter(mof, ControllerFilter.Any) : null,
            EntersWithCounterKind = root.TryGetProperty("entersWithCounterKind", out var ewk) ? ParseCounterKind(ewk.GetString()) : CounterKind.PlusOnePlusOne,
            GraveyardCastCost = root.TryGetProperty("graveyardCastCost", out var gcc) ? ParseExtraCost(gcc) : null,
            ExtraMana = root.TryGetProperty("extraMana", out var em)
                ? em.EnumerateArray().Select(m => new ManaOption(ParseManaTypes(m.GetProperty("types").GetString()!),
                    m.TryGetProperty("amount", out var ma) ? ma.GetInt32() : 1,
                    m.TryGetProperty("onlyFor", out var mo) ? ParseFilter(mo, ControllerFilter.Any) : null,
                    Bool(m, "abilitiesToo"))).ToList()
                : null,
            EntersWithCountersFrom = root.TryGetProperty("entersWithCounters", out var ewcf) && (ewcf.ValueKind == JsonValueKind.Object || (ewcf.ValueKind == JsonValueKind.String && ewcf.GetString() != "X")) ? ParseQuantity(ewcf) : null,
            Replaces = root.TryGetProperty("replaces", out var rep)
                ? rep.EnumerateArray().Aggregate(Replacements.None, (acc, r) => acc | Enum.Parse<Replacements>(r.GetString()!, ignoreCase: true))
                : Replacements.None,
        };
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.GetBoolean();

    private static CardType ParseTypes(JsonElement list) =>
        list.EnumerateArray().Aggregate((CardType)0, (acc, t) => acc | Enum.Parse<CardType>(t.GetString()!, ignoreCase: true));

    /// <summary>"modes": [{ "text": "...", "targets": [...], "effects": [...] }], "chooseCount": 2, "upTo": true</summary>
    private static T WithModes<T>(JsonElement e, T ability) where T : AbilityDefinition
    {
        ability = ability with
        {
            TargetRule = e.TryGetProperty("targetRule", out var rule) ? Enum.Parse<TargetRule>(rule.GetString()!, ignoreCase: true) : TargetRule.None,
            WhenKicked = e.TryGetProperty("whenKicked", out var wk) ? WithModes(wk, new SpellAbility { Targets = Targets(wk), Effects = Effects(wk), Text = Text(wk) }) : null,
        };
        if (!e.TryGetProperty("modes", out var modes)) return ability;
        return ability with
        {
            Modes = modes.EnumerateArray().Select(m => new Mode(Text(m), Targets(m), Effects(m))).ToList(),
            ModesOncePerObject = Bool(e, "modesOnce"),
            ModeCount = e.TryGetProperty("chooseCount", out var n) ? n.GetInt32() : 1,
            UpToModes = Bool(e, "upTo"),
        };
    }

    /// <summary>{ "discard": 1, "sacrifice": filter, "sacrificeCount": 1, "life": 2 }</summary>
    public static ExtraCost ParseExtraCost(JsonElement e) => new(
        e.TryGetProperty("discard", out var d) ? d.GetInt32() : 0,
        e.TryGetProperty("sacrifice", out var s) ? ParseFilter(s, ControllerFilter.You) : null,
        e.TryGetProperty("sacrificeCount", out var n) ? n.GetInt32() : 1,
        e.TryGetProperty("life", out var l) ? l.GetInt32() : 0)
    {
        TapCreatures = e.TryGetProperty("tap", out var t) ? ParseFilter(t, ControllerFilter.You) : null,
        TapCount = e.TryGetProperty("tapCount", out var tc) ? tc.GetInt32() : 0,
        CrewPower = e.TryGetProperty("crew", out var crew) ? crew.GetInt32() : 0,
        RemoveCountersFromYourCreatures = e.TryGetProperty("removeCountersFromCreatures", out var rc) ? rc.GetInt32() : 0,
    };

    /// <summary>{ "amount": 3, "if": condition } or { "amount": 1, "perPermanent": filter } or { "amount": 1, "perGraveyardCard": filter }</summary>
    public static CostReduction ParseCostReduction(JsonElement e) => new(
        e.GetProperty("amount").GetInt32(),
        e.TryGetProperty("if", out var c) ? ParseCondition(c) : null,
        e.TryGetProperty("perPermanent", out var p) ? ParseFilter(p, ControllerFilter.You) : null,
        e.TryGetProperty("perGraveyardCard", out var g) ? ParseFilter(g, ControllerFilter.You) : null)
    {
        ByTotalPower = Bool(e, "byTotalPower"),
        IfTargets = e.TryGetProperty("ifTargets", out var it) ? ParseFilter(it, ControllerFilter.Any) : null,
    };

    /// <summary>{ "affects": "creatures:you", "other": true, "subtype": "Goblin", "pump": [1, 1], "keywords": ["Flying"] }</summary>
    private static StaticAbility ParseStatic(JsonElement s)
    {
        var scope = s.GetProperty("affects").GetString() switch
        {
            "self" => AffectedScope.Self,
            "creatures:you" => AffectedScope.YourCreatures,
            "creatures:opponents" => AffectedScope.OpponentsCreatures,
            "creatures" => AffectedScope.AllCreatures,
            "enchanted" => AffectedScope.Enchanted,
            "equipped" => AffectedScope.Equipped,
            "permanents:you" => AffectedScope.YourPermanents,
            var unknown => throw new FormatException($"Unknown static scope '{unknown}'."),
        };
        var filter = new AffectedFilter(
            scope,
            Bool(s, "other"),
            s.TryGetProperty("subtype", out var subtype) ? subtype.GetString() : null);
        var pt = s.TryGetProperty("pump", out var pump) ? pump.EnumerateArray().Select(ParseQuantity).ToArray() : new Quantity[] { 0, 0 };
        int Fixed(Quantity q) => q.IsFixed ? q.Value * q.Multiplier : 0;
        return new StaticAbility(filter, Fixed(pt[0]), Fixed(pt[1]), ParseKeywords(s))
        {
            Filter = s.TryGetProperty("filter", out var f) ? ParseFilter(f, ControllerFilter.Any) : null,
            While = s.TryGetProperty("while", out var w) ? ParseCondition(w) : null,
            PowerBonus = pt[0].IsFixed ? null : pt[0],
            ToughnessBonus = pt[1].IsFixed ? null : pt[1],
            AddSubtypes = s.TryGetProperty("addSubtypes", out var st) ? st.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
            AddChosenType = Bool(s, "addChosenType"),
            GrantsAbilities = s.TryGetProperty("grants", out var gr) ? Parse("{\"abilities\":" + gr.GetRawText() + "}").Abilities : null,
            LosesAllAbilities = Bool(s, "losesAbilities"),
            SetPower = s.TryGetProperty("setPower", out var sp) ? sp.GetInt32() : null,
            SetToughness = s.TryGetProperty("setToughness", out var stt) ? stt.GetInt32() : null,
            SetTypes = s.TryGetProperty("setTypes", out var sty) ? ParseTypes(sty) : null,
            SetSubtypes = s.TryGetProperty("setSubtypes", out var sst) ? sst.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
            SetColors = s.TryGetProperty("setColors", out var sc) ? sc.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
            SetName = s.TryGetProperty("setName", out var sn) ? sn.GetString() : null,
            AddTypes = s.TryGetProperty("addTypes", out var at) ? ParseTypes(at) : 0,
            GrantsMana = s.TryGetProperty("grantsMana", out var gm) ? ParseManaTypes(gm.GetString()!) : null,
            GrantsManaAmount = s.TryGetProperty("grantsManaAmount", out var gma) ? gma.GetInt32() : 1,
            GivesControl = Bool(s, "givesControl"),
        };
    }

    private static IReadOnlyList<Keyword>? ParseKeywords(JsonElement e) =>
        e.TryGetProperty("keywords", out var k)
            ? k.EnumerateArray().Select(x => ParseKeyword(x.GetString()!)).ToList()
            : null;

    private static Keyword ParseKeyword(string name) =>
        Keywords.TryParse(name, out var kw) ? kw : throw new FormatException($"Unknown keyword '{name}'.");

    private static string Text(JsonElement e) => e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

    private static IReadOnlyList<TargetSpec> Targets(JsonElement e) =>
        e.TryGetProperty("targets", out var t) ? t.EnumerateArray().Select(ParseTarget).ToList() : Array.Empty<TargetSpec>();

    /// <summary>A target as a string ("creature:opponent") or an object { "kind": ..., "controller": ..., "filter": {...}, "optional": true, "text": "..." }.</summary>
    public static TargetSpec ParseTarget(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return ParseTarget(e.GetString()!);
        var spec = ParseTarget(e.GetProperty("kind").GetString()!);
        if (e.TryGetProperty("controller", out var c)) spec = spec with { Controller = ParseController(c.GetString()!) };
        return spec with
        {
            Filter = e.TryGetProperty("filter", out var f) ? ParseFilter(f, ControllerFilter.Any) : null,
            Optional = Bool(e, "optional"),
            Text = e.TryGetProperty("text", out var text) ? text.GetString() : null,
            AnyNumber = Bool(e, "anyNumber"),
            AttachedToTarget = e.TryGetProperty("attachedToTarget", out var att) ? ParseSubject(att.GetString()).Index : null,
            SingleTargetOnly = Bool(e, "singleTarget"),
            ControlledByTriggeredPlayer = Bool(e, "controlledByTriggeredPlayer"),
        };
    }

    /// <summary>"any", "creature", "creature:you", "creature:opponent", "player", "opponent", "spell", "graveyardCard:you", ...</summary>
    public static TargetSpec ParseTarget(string text)
    {
        var parts = text.Split(':');
        if (parts[0] == "opponent") return new TargetSpec(TargetKind.Player, ControllerFilter.Opponent);
        if (!Enum.TryParse<TargetKind>(parts[0], ignoreCase: true, out var kind)) throw new FormatException($"Unknown target '{text}'.");
        var controller = parts.Length > 1 ? ParseController(parts[1]) : ControllerFilter.Any;
        return new TargetSpec(kind, controller);
    }

    private static ControllerFilter ParseController(string text) => text switch
    {
        "you" => ControllerFilter.You,
        "opponent" => ControllerFilter.Opponent,
        "any" => ControllerFilter.Any,
        _ => throw new FormatException($"Unknown controller '{text}'."),
    };

    /// <summary>"target" (first target), "target2", "you", "opponents", "everyone", "self", "targetController", "targetOwner".</summary>
    public static Subject ParseSubject(string? text) => text switch
    {
        null or "you" => Subject.You,
        "target" => Subject.TargetAt(0),
        "opponents" => Subject.EachOpponent,
        "everyone" => new Subject(SubjectKind.EachPlayer),
        "self" => Subject.Self,
        "targetController" => new Subject(SubjectKind.TargetController),
        "targetOwner" => new Subject(SubjectKind.TargetOwner),
        "triggered" => Subject.Triggered,
        "triggeredPlayer" => Subject.TriggeredPlayer,
        "attached" => Subject.Attached,
        "eachTarget" => new Subject(SubjectKind.EachTarget),
        "granter" => new Subject(SubjectKind.Granter),
        "granterPermanent" => new Subject(SubjectKind.GranterPermanent),
        _ when text.StartsWith("eachTarget") && int.TryParse(text[10..], out int et) => new Subject(SubjectKind.EachTarget, et - 1),
        _ when text.StartsWith("targetController") && int.TryParse(text[16..], out int c) => new Subject(SubjectKind.TargetController, c - 1),
        _ when text.StartsWith("target") && int.TryParse(text[6..], out int n) => Subject.TargetAt(n - 1),
        _ => throw new FormatException($"Unknown subject '{text}'."),
    };

    /// <summary>A subject string, or { "each": filter } for every matching permanent (controller defaults to any).</summary>
    public static Subject ParseSubject(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? ParseSubject(e.GetString())
        : e.TryGetProperty("each", out var f) ? Subject.Each(ParseFilter(f, ControllerFilter.Any))
        : throw new FormatException($"Unknown subject {e.GetRawText()}.");

    public static TriggerEvent ParseTrigger(string text) => text switch
    {
        "enters" => TriggerEvent.EntersBattlefield,
        "dies" => TriggerEvent.Dies,
        "attacks" => TriggerEvent.Attacks,
        "upkeep" => TriggerEvent.YourUpkeep,
        "endStep" => TriggerEvent.YourEndStep,
        "combatDamageToPlayer" => TriggerEvent.DealsCombatDamageToPlayer,
        "blocks" => TriggerEvent.Blocks,
        "attacksOrBlocks" => TriggerEvent.AttacksOrBlocks,
        "creatureEnters" => TriggerEvent.CreatureEnters,
        "landfall" => TriggerEvent.LandEnters,
        "creatureDies" => TriggerEvent.CreatureDies,
        "gainLife" => TriggerEvent.YouGainLife,
        "castSpell" => TriggerEvent.YouCastSpell,
        "beginCombat" => TriggerEvent.YourBeginCombat,
        "youAttack" => TriggerEvent.YouAttack,
        "opponentCastsSpell" => TriggerEvent.OpponentCastsSpell,
        "creatureAttacks" => TriggerEvent.CreatureAttacks,
        "opponentLosesLife" => TriggerEvent.OpponentLosesLife,
        "draw" => TriggerEvent.YouDrawCard,
        "creatureCombatDamageToPlayer" => TriggerEvent.CreatureDealsCombatDamageToPlayer,
        "eachEndStep" => TriggerEvent.EachEndStep,
        "eachBeginCombat" => TriggerEvent.EachBeginCombat,
        "eachUpkeep" => TriggerEvent.EachUpkeep,
        "countersPlaced" => TriggerEvent.CountersPlaced,
        "becomesTapped" => TriggerEvent.BecomesTapped,
        "opponentDraws" => TriggerEvent.OpponentDrawsCard,
        "eachDrawStep" => TriggerEvent.EachDrawStep,
        "anyPlayerCastsSpell" => TriggerEvent.AnyPlayerCastsSpell,
        "selfSacrificed" => TriggerEvent.SelfSacrificed,
        "noncombatDamageToOpponent" => TriggerEvent.YourSourceDealsNoncombatDamageToOpponent,
        "creatureCombatDamage" => TriggerEvent.CreatureDealsCombatDamage,
        "dealsCombatDamage" => TriggerEvent.DealsCombatDamage,
        "opponentDiscards" => TriggerEvent.OpponentDiscards,
        "becomesUntapped" => TriggerEvent.BecomesUntapped,
        _ => throw new FormatException($"Unknown trigger '{text}'."),
    };

    /// <summary>
    /// { "types": ["instant", "sorcery"], "not": ["creature"], "subtype": "Elf", "controller": "you", "other": true,
    ///   "minPower": 4, "maxPower": 2, "minToughness": 4, "minManaValue": 6, "maxManaValue": 2, "colors": ["B", "R"],
    ///   "keyword": "Flying", "without": "Flying", "tapped": true, "inCombat": true, "attacking": true, "blocking": true,
    ///   "multicolored": true, "colorless": true, "enchanted": true, "equipped": true, "commander": true,
    ///   "token": false, "supertype": "basic", "notSubtype": "Human" }
    /// </summary>
    public static ObjectFilter ParseFilter(JsonElement f) => ParseFilter(f, ControllerFilter.You);

    public static ObjectFilter ParseFilter(JsonElement f, ControllerFilter defaultController)
    {
        CardType Types(string name) => f.TryGetProperty(name, out var list)
            ? list.EnumerateArray().Aggregate((CardType)0, (acc, t) => acc | (Enum.TryParse<CardType>(t.GetString(), ignoreCase: true, out var type) ? type : throw new FormatException($"Unknown card type '{t}'.")))
            : 0;
        int? Int(string name) => f.TryGetProperty(name, out var v) ? v.GetInt32() : null;
        bool? OptBool(string name) => f.TryGetProperty(name, out var v) ? v.GetBoolean() : null;
        return new ObjectFilter(
            Types("types"),
            Types("not"),
            f.TryGetProperty("subtype", out var subtype) ? subtype.GetString() : null,
            f.TryGetProperty("controller", out var controller) ? ParseController(controller.GetString()!) : defaultController,
            Bool(f, "other"),
            Int("minPower"),
            OptBool("token"),
            Int("maxPower"),
            Int("minToughness"),
            Int("minManaValue"),
            Int("maxManaValue"),
            f.TryGetProperty("colors", out var colors) ? colors.EnumerateArray().Select(c => c.GetString()!).ToList() : null,
            f.TryGetProperty("keyword", out var kw) ? ParseKeyword(kw.GetString()!) : null,
            f.TryGetProperty("without", out var without) ? ParseKeyword(without.GetString()!) : null,
            OptBool("tapped"),
            OptBool("inCombat"),
            OptBool("attacking"),
            f.TryGetProperty("supertype", out var st) ? Enum.Parse<Supertype>(st.GetString()!, ignoreCase: true) : 0,
            f.TryGetProperty("notSubtype", out var ns) ? ns.GetString() : null,
            f.TryGetProperty("name", out var nm) ? nm.GetString() : null,
            OptBool("hasCounters"),
            f.TryGetProperty("anyOf", out var anyOf) ? anyOf.EnumerateArray().Select(x => ParseFilter(x, ControllerFilter.Any)).ToList() : null,
            Bool(f, "attachedToSource"),
            Bool(f, "chosenColor"),
            Bool(f, "chosenType"),
            Bool(f, "damagedBySource"),
            Bool(f, "maxManaValueSourcePower"),
            OptBool("attached"),
            f.TryGetProperty("notSupertype", out var nst) ? Enum.Parse<Supertype>(nst.GetString()!, ignoreCase: true) : 0,
            Bool(f, "maxManaValueLandCount"),
            Bool(f, "ownedByYou"),
            Bool(f, "putIntoZoneThisTurn"),
            OptBool("blocking"),
            OptBool("multicolored"),
            OptBool("colorless"),
            OptBool("enchanted"),
            OptBool("equipped"),
            OptBool("commander"));
    }

    /// <summary>
    /// "raid", "morbid", "threshold", "ferocious", "kicked", "opponentLostLife", "yourTurn", or { "gainedLife": 1 },
    /// { "graveyard": 7 }, { "control": filter, "count": 2 }, { "life": 10 }, { "counters": 3 }, { "not": condition }.
    /// </summary>
    public static Condition ParseCondition(JsonElement c)
    {
        if (c.ValueKind == JsonValueKind.String)
        {
            return c.GetString() switch
            {
                "raid" => new AttackedThisTurn(),
                "morbid" => new CreatureDiedThisTurn(),
                "threshold" => new CardsInGraveyard(7),
                "ferocious" => new YouControl(new ObjectFilter(CardType.Creature, MinPower: 4)),
                "gainedLife" => new GainedLifeThisTurn(),
                "kicked" => new WasKicked(),
                "opponentLostLife" => new OpponentLostLifeThisTurn(),
                "yourTurn" => new YourTurn(),
                "createdThisWay" => new CreatedThisWay(),
                "attacking" => new SourceAttacking(),
                "triggeredWasAttacking" => new TriggeredWasAttacking(),
                "youSacrificed" => new YouSacrificedThisWay(),
                "castFromHand" => new WasCastFromHand(),
                "wasCast" => new WasCast(),
                var unknown => throw new FormatException($"Unknown condition '{unknown}'."),
            };
        }
        if (c.TryGetProperty("gainedLife", out var gained)) return new GainedLifeThisTurn(gained.GetInt32());
        if (c.TryGetProperty("graveyard", out var graveyard))
            return new CardsInGraveyard(graveyard.GetInt32(), c.TryGetProperty("filter", out var gf) ? ParseFilter(gf, ControllerFilter.Any) : null);
        if (c.TryGetProperty("attackers", out var attackers)) return new AttackingCreatures(attackers.GetInt32());
        if (c.TryGetProperty("control", out var control))
            return new YouControl(ParseFilter(control), c.TryGetProperty("count", out var count) ? count.GetInt32() : 1);
        if (c.TryGetProperty("life", out var life)) return new LifeAtLeast(life.GetInt32());
        if (c.TryGetProperty("counters", out var counters))
            return new SourceHasCounters(counters.GetInt32(), c.TryGetProperty("kind", out var ck) ? ParseCounterKind(ck.GetString()) : CounterKind.PlusOnePlusOne);
        if (c.TryGetProperty("targetLifeExactly", out var tle)) return new TargetLifeExactly(c.TryGetProperty("target", out var tl) ? ParseSubject(tl.GetString()).Index : 0, tle.GetInt32());
        if (c.TryGetProperty("xAtLeast", out var xal)) return new XAtLeast(xal.GetInt32());
        if (c.TryGetProperty("sourceWas", out var sw)) return new SourceWasSubtype(sw.GetString()!);
        if (c.TryGetProperty("sourceHadCounters", out var shc)) return new SourceHadCounters(ParseCounterKind(shc.GetString()));
        if (c.TryGetProperty("differentNames", out var dn)) return new DifferentNames(ParseFilter(dn), c.GetProperty("count").GetInt32());
        if (c.TryGetProperty("resolvedThisTurn", out var rtt)) return new ResolvedThisTurn(rtt.GetInt32());
        if (c.TryGetProperty("triggeredCounters", out var trc)) return new TriggeredHasCounters(trc.GetInt32());
        if (c.TryGetProperty("targetAttachedTo", out var tat))
            return new TargetAttachedTo(ParseSubject(tat.GetString()).Index, ParseSubject(c.GetProperty("to").GetString()).Index);
        if (c.TryGetProperty("atLeast", out var al)) return new QuantityAtLeast(ParseQuantity(al), c.GetProperty("value").GetInt32());
        if (c.TryGetProperty("sourceIs", out var sis)) return new SourceIs(ParseFilter(sis, ControllerFilter.Any));
        if (c.TryGetProperty("not", out var inner)) return new Not(ParseCondition(inner));
        if (c.TryGetProperty("target", out var ti)) return new TargetMatches(ParseSubject(ti.GetString()).Index, ParseFilter(c.GetProperty("is"), ControllerFilter.Any));
        if (c.TryGetProperty("lifeAboveStarting", out var las)) return new LifeAboveStarting(las.GetInt32());
        if (c.TryGetProperty("all", out var all)) return new All(all.EnumerateArray().Select(ParseCondition).ToList());
        if (c.TryGetProperty("totalPower", out var tp)) return new TotalPowerAtLeast(tp.GetInt32());
        throw new FormatException($"Unknown condition {c.GetRawText()}.");
    }

    /// <summary>
    /// A number: 3, "X", "-X", "lifeGained", "life", "handSize", or { "count": filter, "times": 2 },
    /// { "graveyard": filter }, { "power": "self" | "target" | "target2" }, { "toughness": "target" }, { "manaValue": "target" }.
    /// </summary>
    public static Quantity ParseQuantity(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.GetInt32();
            case JsonValueKind.String:
                return e.GetString() switch
                {
                    "X" => Quantity.X,
                    "-X" => Quantity.X with { Multiplier = -1 },
                    "lifeGained" => new Quantity(0, QuantityKind.LifeGainedThisTurn),
                    "life" => new Quantity(0, QuantityKind.YourLife),
                    "handSize" => new Quantity(0, QuantityKind.HandSize),
                    "triggerAmount" => new Quantity(0, QuantityKind.TriggerAmount),
                    "-triggerAmount" => new Quantity(0, QuantityKind.TriggerAmount, Multiplier: -1),
                    "triggeredPower" => new Quantity(0, QuantityKind.TriggeredPower),
                    "opponentsGraveyards" => new Quantity(0, QuantityKind.OpponentsGraveyardCount),
                    "greatestOtherPower" => new Quantity(0, QuantityKind.GreatestOtherPower),
                    "sacrificedToughness" => new Quantity(0, QuantityKind.SacrificedToughness),
                    "sacrificedPower" => new Quantity(0, QuantityKind.SacrificedPower),
                    "lifeLostThisWay" => new Quantity(0, QuantityKind.LifeLostThisWay),
                    "destroyedThisWay" => new Quantity(0, QuantityKind.DestroyedThisWay),
                    "excessDamage" => new Quantity(0, QuantityKind.ExcessDamage),
                    "triggeredColors" => new Quantity(0, QuantityKind.TriggeredColors),
                    var unknown => throw new FormatException($"Unknown quantity '{unknown}'."),
                };
        }
        int times = e.TryGetProperty("times", out var t) ? t.GetInt32() : 1;
        int offset = e.TryGetProperty("offset", out var off) ? off.GetInt32() : 0;
        if (e.TryGetProperty("milled", out var mil)) return new Quantity(0, QuantityKind.MilledThisWay, ParseFilter(mil, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("exiled", out var exl)) return new Quantity(0, QuantityKind.ExiledThisWay, ParseFilter(exl, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("distinctManaValues", out var dmv)) return new Quantity(0, QuantityKind.DistinctManaValues, ParseFilter(dmv), times, Offset: offset);
        if (e.TryGetProperty("spellsCastBefore", out var scb)) return new Quantity(0, QuantityKind.SpellsCastBeforeTriggered, ParseFilter(scb, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("spellsCast", out var sct)) return new Quantity(0, QuantityKind.SpellsCastThisTurn, ParseFilter(sct, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("count", out var count)) return new Quantity(0, QuantityKind.PermanentCount, ParseFilter(count), times);
        if (e.TryGetProperty("graveyard", out var gy)) return new Quantity(0, QuantityKind.GraveyardCount, ParseFilter(gy), times);
        if (e.TryGetProperty("attacking", out var att)) return new Quantity(0, QuantityKind.AttackingCount, ParseFilter(att), times);
        if (e.TryGetProperty("counters", out var ctr)) return new Quantity(0, QuantityKind.SourceCounters, Multiplier: times, Counter: ParseCounterKind(ctr.GetString()));
        if (e.TryGetProperty("power", out var power))
        {
            var subject = ParseSubject(power.GetString());
            return subject.Kind == SubjectKind.Self
                ? new Quantity(0, QuantityKind.SourcePower, Multiplier: times)
                : new Quantity(0, QuantityKind.TargetPower, Multiplier: times, Index: subject.Index);
        }
        if (e.TryGetProperty("toughness", out var toughness))
            return new Quantity(0, QuantityKind.TargetToughness, Multiplier: times, Index: ParseSubject(toughness.GetString()).Index);
        if (e.TryGetProperty("manaValue", out var manaValue))
            return new Quantity(0, QuantityKind.TargetManaValue, Multiplier: times, Index: ParseSubject(manaValue.GetString()).Index);
        throw new FormatException($"Unknown quantity {e.GetRawText()}.");
    }

    /// <summary>
    /// "{2}{R}, {T}, sacrifice" plus "discard", "discard:2", "life:2", "sacrifice:creature" (another creature you control),
    /// "removeCounters:5", "exileFromGraveyard" (activated from the graveyard, exiling this card), "fromGraveyard".
    /// </summary>
    public static AbilityCost ParseCost(string text)
    {
        var mana = ManaCost.Zero;
        bool tap = false, sacrifice = false, fromGraveyard = false;
        int discard = 0, life = 0, removeCounters = 0;
        var removeKind = CounterKind.PlusOnePlusOne;
        bool exileSelf = false, returnToHand = false, tapGranter = false;
        int? loyalty = null;
        int addCounters = 0, tapCount = 0, crew = 0;
        var addKind = CounterKind.PlusOnePlusOne;
        ObjectFilter? tapFilter = null;
        ObjectFilter? sacrificeOther = null;
        foreach (var raw in text.Split(','))
        {
            var part = raw.Trim();
            var lower = part.ToLowerInvariant();
            if (part == "{T}") tap = true;
            else if (System.Text.RegularExpressions.Regex.IsMatch(part, @"^([+\-\u2212]\d+|0)$"))
                loyalty = int.Parse(part.Replace('\u2212', '-'));
            else if (lower == "sacrifice") sacrifice = true;
            else if (lower == "exilefromgraveyard") { sacrifice = true; fromGraveyard = true; }
            else if (lower == "fromgraveyard") fromGraveyard = true;
            else if (lower == "discard") discard = 1;
            else if (lower.StartsWith("discard:")) discard = int.Parse(lower[8..]);
            else if (lower.StartsWith("life:")) life = int.Parse(lower[5..]);
            else if (lower.StartsWith("removecounters:"))
            {
                var bits = lower[15..].Split(':');
                removeCounters = int.Parse(bits[0]);
                if (bits.Length > 1) removeKind = ParseCounterKind(bits[1]);
            }
            else if (lower == "exile") exileSelf = true;
            else if (lower == "returntohand") returnToHand = true;
            else if (lower == "tapgranter") tapGranter = true;
            else if (lower.StartsWith("addcounters:"))
            {
                var bits = lower[12..].Split(':');
                addCounters = int.Parse(bits[0]);
                if (bits.Length > 1) addKind = ParseCounterKind(bits[1]);
            }
            else if (lower.StartsWith("tapcreatures:"))
            {
                var bits = part[13..].Split(':');
                tapCount = int.Parse(bits[0]);
                tapFilter = new ObjectFilter(CardType.Creature, Subtype: bits.Length > 1 ? bits[1] : null, Other: true);
            }
            else if (lower.StartsWith("crew:")) crew = int.Parse(lower[5..]);
            else if (lower.StartsWith("sacrifice:"))
                sacrificeOther = new ObjectFilter(Enum.Parse<CardType>(lower[10..], ignoreCase: true), Other: true);
            else if (part.Length > 0) mana = ManaCost.Parse(part);
        }
        var extra = discard > 0 || life > 0 || sacrificeOther is not null || tapFilter is not null || crew > 0
            ? new ExtraCost(discard, sacrificeOther, 1, life) { TapCreatures = tapFilter, TapCount = tapCount, CrewPower = crew }
            : null;
        return new AbilityCost(mana, tap, sacrifice)
        {
            Extra = extra, FromGraveyard = fromGraveyard, RemoveCounters = removeCounters, RemoveCounterKind = removeKind, ExileSelf = exileSelf,
            Loyalty = loyalty,
            AddCounters = addCounters, AddCounterKind = addKind, ReturnSelfToHand = returnToHand, TapGranter = tapGranter,
        };
    }

    private static IReadOnlyList<Effect> Effects(JsonElement e) =>
        e.TryGetProperty("effects", out var list) ? EffectList(list)
        : e.TryGetProperty("modes", out _) ? Array.Empty<Effect>()
        : throw new FormatException("Missing \"effects\".");

    private static IReadOnlyList<Effect> EffectList(JsonElement list) => list.EnumerateArray().Select(ParseEffect).ToList();

    private static Effect ParseEffect(JsonElement e)
    {
        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int Int(string name) => e.GetProperty(name).GetInt32();
        bool Flag(string name) => e.TryGetProperty(name, out var v) && v.GetBoolean();
        Quantity Qty(string name) => ParseQuantity(e.GetProperty(name));
        Subject Subj(string name, string fallback) => e.TryGetProperty(name, out var v) ? ParseSubject(v) : ParseSubject(fallback);
        Subject? Value(string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Object ? ParseSubject(v) : null;

        if (e.TryGetProperty("scry", out _)) return new Scry(Int("scry"));
        if (e.TryGetProperty("surveil", out _)) return new Surveil(Int("surveil"));
        if (Str("fight") is { } fighter) return new Fight(ParseSubject(fighter), ParseSubject(Str("with") ?? "target2"));
        if (e.TryGetProperty("discard", out _)) return new Discard(Qty("discard"), Subj("who", "you"));
        if (e.TryGetProperty("if", out var condition))
            return new IfThen(ParseCondition(condition), EffectList(e.GetProperty("then")), e.TryGetProperty("else", out var otherwise) ? EffectList(otherwise) : null);
        if (Str("may") is { } prompt) return new MayDo(prompt, Effects(e));
        if (e.TryGetProperty("damage", out _)) return new DealDamage(Qty("damage"), Subj("to", "target"));
        if (e.TryGetProperty("draw", out _)) return new DrawCards(Qty("draw"), Subj("who", "you"));
        if (e.TryGetProperty("gainLife", out _)) return new GainLife(Qty("gainLife"), Subj("who", "you"));
        if (e.TryGetProperty("loseLife", out _)) return new LoseLife(Qty("loseLife"), Subj("who", "you"));
        if (e.TryGetProperty("mill", out _)) return new Mill(Qty("mill"), Subj("who", "you"));
        if (Value("destroy") is { } destroy) return new Destroy(destroy);
        if (Value("exile") is { } exile)
            return new ExileIt(exile) { WithCounter = Str("withCounter") is { } wc ? ParseCounterKind(wc) : null, ExceptCreatedThisWay = Flag("exceptCreated") };
        if (Flag("loseGame")) return new LoseGame();
        if (Value("bounce") is { } bounce) return new ReturnToHand(bounce);
        if (Value("tap") is { } tap) return new TapIt(tap);
        if (Value("untap") is { } untap) return new UntapIt(untap);
        if (Value("counter") is { } counter) return new CounterSpell(counter);
        if (Value("attach") is { } attach) return new AttachSelf(attach);
        if (Value("reanimate") is { } reanimate)
            return new PutOntoBattlefield(reanimate, Flag("tapped"), Flag("ownerControl"))
            {
                Counters = e.TryGetProperty("counters", out var rcn) ? rcn.GetInt32() : 0,
                CounterKind = ParseCounterKind(Str("counterKind")),
                AddSubtypes = e.TryGetProperty("addSubtypes", out var ras) ? ras.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
                AddKeywords = ParseKeywords(e),
            };
        if (Value("toLibrary") is { } toLibrary) return new PutIntoLibrary(toLibrary, Flag("bottom")) { Top = Flag("top") };
        if (Value("gainControl") is { } gain) return new GainControl(gain, Flag("untilEndOfTurn"), e.TryGetProperty("to", out var gto) ? ParseSubject(gto) : null);
        if (Str("mayPay") is { } payPrompt)
            return new MayPay(payPrompt, e.TryGetProperty("mana", out var pm) ? ManaCost.Parse(pm.GetString()!) : null,
                e.TryGetProperty("cost", out var pc) ? ParseExtraCost(pc) : null, Effects(e));
        if (e.TryGetProperty("returnFromGraveyard", out var rfg))
            return new ReturnFromGraveyard(ParseFilter(rfg, ControllerFilter.Any), e.TryGetProperty("count", out var rc) ? rc.GetInt32() : 1,
                Str("to") == "battlefield" ? Engine.State.Zone.Battlefield : Engine.State.Zone.Hand, Flag("upTo"))
            {
                ExcludeSacrificed = Flag("excludeSacrificed"),
            };
        if (Value("discardHand") is { } dh) return new DiscardHand(dh);
        if (Value("changeTarget") is { } ctg) return new ChangeTarget(ctg);
        if (Value("destroySameName") is { } dsn) return new DestroySameName(dsn);
        if (e.TryGetProperty("distributeCounters", out _)) return new DistributeCounters(Int("distributeCounters"));
        if (e.TryGetProperty("revealUntil", out var ru))
            return new RevealUntil(ParseFilter(ru, ControllerFilter.Any), Str("to") == "battlefield" ? Engine.State.Zone.Battlefield : Engine.State.Zone.Hand);
        if (Value("unless") is { } unlessWho)
            return new Unless(unlessWho, e.GetProperty("options").EnumerateArray().Select(ParseExtraCost).ToList(), EffectList(e.GetProperty("otherwise")));
        if (e.TryGetProperty("opponentMaySacrifice", out var oms)) return new OpponentMaySacrifice(ParseFilter(oms, ControllerFilter.Any), Effects(e));
        if (e.TryGetProperty("piles", out _)) return new Piles(Int("piles"));
        if (Flag("winGame")) return new WinGame();
        if (e.TryGetProperty("untapUpTo", out _))
            return new UntapUpTo(Int("untapUpTo"), e.TryGetProperty("filter", out var uf) ? ParseFilter(uf) : new ObjectFilter(CardType.Land));
        if (e.TryGetProperty("poison", out _)) return new AddPoison(Int("poison"), Subj("who", "triggeredPlayer"));
        if (Flag("endTurn")) return new EndTheTurn();
        if (Flag("additionalCombat")) return new AdditionalCombat(Flag("untapCreatures"));
        if (Value("copySpell") is { } copySpell) return new CopySpell(copySpell, e.TryGetProperty("count", out var csc) ? ParseQuantity(csc) : 1);
        if (e.TryGetProperty("addManaAnyColor", out _)) return new AddManaOfAnyColor(Int("addManaAnyColor"));
        if (Flag("returnExiledWithThis")) return new ReturnExiledWithThis();
        if (e.TryGetProperty("searchExileWithThis", out var sew)) return new SearchAndExileWithThis(ParseFilter(sew, ControllerFilter.Any));
        if (Value("grantFlashback") is { } gfb) return new GrantFlashback(gfb);
        if (Value("castFromGraveyardThisTurn") is { } cfg) return new PlayableFromGraveyardThisTurn(cfg);
        if (Value("become") is { } become)
            return new Become(become,
                e.TryGetProperty("power", out var bp) ? bp.GetInt32() : null,
                e.TryGetProperty("toughness", out var bt) ? bt.GetInt32() : null,
                e.TryGetProperty("addTypes", out var bat) ? ParseTypes(bat) : 0,
                e.TryGetProperty("addSubtypes", out var bas) ? bas.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
                ParseKeywords(e),
                e.TryGetProperty("abilities", out var bab) ? Parse("{\"abilities\":" + bab.GetRawText() + "}").Abilities : null,
                Flag("permanent"))
            {
                SetSubtypes = e.TryGetProperty("setSubtypes", out var bss) ? bss.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
            };
        if (Str("mayPayX") is { } payX) return new MayPayX(payX, Effects(e));
        if (Flag("noMaxHandSize")) return new NoMaximumHandSizeForever();
        if (Flag("copyNextInstantOrSorcery")) return new CopyNextInstantOrSorcery();
        if (Flag("castFromLibraryTopsFree")) return new CastFromEachLibraryTopFree();
        if (Str("returnNextEndStepOneFewer") is { } rnk) return new ReturnAtNextEndStepWithOneFewer(ParseCounterKind(rnk));
        if (Flag("destroyManaValueXDamaged")) return new DestroyManaValueXOfDamagedPlayers();
        if (Str("addManaUntilEndOfTurn") is { } amu) return new AddManaUntilEndOfTurn(ManaCost.Parse(amu).Pips);
        if (Str("emblem") is { } emblemName) return new CreateEmblem(emblemName, Parse(e.GetRawText()).Abilities);
        if (e.TryGetProperty("exileTopPlayable", out _))
            return new ExileTopPlayable(Int("exileTopPlayable"), !e.TryGetProperty("chooseOne", out var co) || co.GetBoolean(), Flag("untilNextTurn"), Flag("free"));
        if (e.TryGetProperty("divideDamage", out _)) return new DealDamageDivided(Int("divideDamage"));
        if (Value("keepOneOfEachType") is { } keep) return new KeepOneOfEachType(keep);
        if (e.TryGetProperty("millUntil", out var mu)) return new MillUntil(Subj("who", "you"), ParseFilter(mu, ControllerFilter.Any));
        if (Value("exileUntilLeaves") is { } eul) return new ExileUntilSourceLeaves(eul);
        if (Value("flicker") is { } flick) return new ExileAndReturnAtEndStep(flick, Flag("yours"));
        if (Value("copy") is { } copyOf)
            return new CreateTokenCopy(copyOf, e.TryGetProperty("count", out var cc) ? ParseQuantity(cc) : 1, Flag("haste"), Flag("sacrificeAtEndStep"))
            {
                AddSubtypes = e.TryGetProperty("addSubtypes", out var cas) ? cas.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
            };
        if (Value("sacrificeIt") is { } sacIt) return new SacrificeIt(sacIt);
        if (Value("exileIfDies") is { } eid) return new ExileIfDiesThisTurn(eid);
        if (Value("preventCombatDamage") is { } pcd) return new PreventCombatDamageTo(pcd);
        if (e.TryGetProperty("lookAtTop", out _))
            return new LookAtTopTake(Int("lookAtTop"), e.TryGetProperty("filter", out var lf) ? ParseFilter(lf, ControllerFilter.Any) : null,
                e.TryGetProperty("take", out var take) ? take.GetInt32() : 1,
                Str("to") switch { null or "hand" => Engine.State.Zone.Hand, "battlefield" => Engine.State.Zone.Battlefield, "top" => Engine.State.Zone.Library,
                    var unknown => throw new FormatException($"Unknown destination '{unknown}'.") },
                Str("rest") == "graveyard")
            {
                CountIsX = Flag("countIsX"),
                MaxManaValueX = Flag("maxManaValueX"),
                Reveal = Flag("reveal"),
                RevealAll = Flag("revealAll"),
            };
        if (Value("discardChosen") is { } dchosen)
            return new DiscardChosenByYou(dchosen, e.TryGetProperty("filter", out var df) ? ParseFilter(df, ControllerFilter.Any) : null, e.TryGetProperty("count", out var dn) ? dn.GetInt32() : 1);
        if (Value("exileGraveyard") is { } eg) return new ExileGraveyard(eg);
        if (e.TryGetProperty("whenYouDo", out var reflexive))
            return new ReflexiveTrigger(WithModes(reflexive, new TriggeredAbility
            {
                Trigger = TriggerEvent.Reflexive, Targets = Targets(reflexive), Effects = Effects(reflexive), Text = Text(reflexive),
            }), reflexive.TryGetProperty("if", out var rif) ? ParseCondition(rif) : null);
        if (Value("doubleCounters") is { } dbl)
            return new DoubleCounters(dbl, e.TryGetProperty("kind", out var dk) ? ParseCounterKind(dk.GetString()) : null);
        if (e.TryGetProperty("removeCounters", out _)) return new RemoveCounters(Qty("removeCounters"), Subj("what", "self"), ParseCounterKind(Str("kind")));
        if (Value("shuffleGraveyard") is { } sg) return new ShuffleGraveyardIntoLibrary(sg);
        if (Str("addMana") is { } addMana) return new AddMana(ManaCost.Parse(addMana).Pips);
        if (Value("bite") is { } biter) return new DealsDamageEqualToPower(biter, Subj("to", "target2"));
        if (e.TryGetProperty("reanimateAll", out var raf)) return new ReanimateAll(Subj("from", "you"), ParseFilter(raf, ControllerFilter.Any));
        if (e.TryGetProperty("bounceAll", out var baf)) return new BounceAll(ParseFilter(baf, ControllerFilter.Any), e.TryGetProperty("relativeTo", out var rt) ? ParseSubject(rt) : null);
        if (e.TryGetProperty("search", out var search))
        {
            var to = Str("to") switch
            {
                null or "hand" => Engine.State.Zone.Hand,
                "battlefield" => Engine.State.Zone.Battlefield,
                "graveyard" => Engine.State.Zone.Graveyard,
                "top" => Engine.State.Zone.Library,
                var unknown => throw new FormatException($"Unknown search destination '{unknown}'."),
            };
            return new SearchLibrary(ParseFilter(search, ControllerFilter.Any), e.TryGetProperty("count", out var c) ? c.GetInt32() : 1, to, Flag("tapped"))
            {
                Who = e.TryGetProperty("who", out var sw) ? ParseSubject(sw) : null,
                Optional = Flag("optional"),
                Reveal = Flag("reveal"),
            };
        }
        if (e.TryGetProperty("sacrifice", out _))
            return new Sacrifice(Qty("sacrifice"), e.TryGetProperty("filter", out var sf) ? ParseFilter(sf, ControllerFilter.Any) : new ObjectFilter(CardType.Creature), Subj("who", "you"));
        if (e.TryGetProperty("pump", out var pump))
        {
            var pt = pump.EnumerateArray().Select(ParseQuantity).ToArray();
            return new PumpUntilEndOfTurn(pt[0], pt[1], Subj("what", "target"), ParseKeywords(e));
        }
        if (e.TryGetProperty("counters", out _))
        {
            return new AddCounters(Qty("counters"), Subj("what", "target"), ParseCounterKind(Str("kind")));
        }
        if (e.TryGetProperty("tokens", out _))
            return new CreateTokens(ParseToken(e.GetProperty("token")), Qty("tokens"), Subj("for", "you"), Flag("tapped")) { HasteUntilEndOfTurn = Flag("hasteUntilEndOfTurn") };
        throw new FormatException($"Unknown effect {e.GetRawText()}.");
    }

    /// <summary>"any" (one of the five colors) or mana symbols "{C}" / "{R}{G}" (one of those types).</summary>
    private static IReadOnlyList<ManaType> ParseManaTypes(string text) =>
        text == "any"
            ? new[] { ManaType.White, ManaType.Blue, ManaType.Black, ManaType.Red, ManaType.Green }
            : ManaCost.Parse(text).Pips.Distinct().ToList();

    private static CounterKind ParseCounterKind(string? kind) => kind switch
    {
        null or "+1/+1" => CounterKind.PlusOnePlusOne,
        "-1/-1" => CounterKind.MinusOneMinusOne,
        _ => Enum.Parse<CounterKind>(kind, ignoreCase: true),
    };

    /// <summary>A token described in full, or the name of a token the rules define (Treasure, Food, Clue).</summary>
    private static CardDefinition ParseToken(JsonElement t)
    {
        if (t.ValueKind == JsonValueKind.String)
            return PredefinedTokens.ByName(t.GetString()!) ?? throw new FormatException($"Unknown predefined token '{t}'.");
        var (supertypes, types, subtypes) = TypeLine.Parse(t.GetProperty("types").GetString()!);
        var abilities = t.TryGetProperty("abilities", out _) ? Parse(t.GetRawText()).Abilities : Array.Empty<AbilityDefinition>();
        return new CardDefinition
        {
            Name = t.GetProperty("name").GetString()!,
            Types = types,
            Supertypes = supertypes,
            Subtypes = subtypes,
            Power = t.TryGetProperty("power", out var p) ? p.GetInt32() : null,
            Toughness = t.TryGetProperty("toughness", out var th) ? th.GetInt32() : null,
            Keywords = t.TryGetProperty("keywords", out var k) ? k.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>(),
            Colors = t.TryGetProperty("colors", out var c) ? c.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>(),
            Abilities = abilities,
            OracleText = t.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            IsToken = true,
        };
    }
}
