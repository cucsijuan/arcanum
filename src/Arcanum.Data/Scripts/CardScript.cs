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
    public int? CantBeCounteredIfXAtLeast { get; init; }
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
    public ObjectFilter? EnterCounterOn { get; init; }
    public CounterKind EnterCounterKind { get; init; }
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

    /// <summary>For an adventurer card: the script of its Adventure (the card's second face).</summary>
    public CardScript? Adventure { get; init; }

    /// <summary>For a transforming double-faced card: the script of its back face.</summary>
    public CardScript? Back { get; init; }

    public CardDefinition? Gift { get; init; }
    public Condition? FlashIf { get; init; }
    public ManaCost? AttackTax { get; init; }
    public Condition? AttackTaxIf { get; init; }
    public ManaCost? BlockTax { get; init; }
    public Condition? BlockTaxIf { get; init; }
    public ObjectFilter? CantBeTargetedBy { get; init; }
    public Condition? CantBeCounteredIf { get; init; }
    public Condition? AdditionalLandPlayIf { get; init; }
    public Condition? EntersTappedUnless { get; init; }
    public int EquipDiscount { get; init; }
    public Condition? FreeFirstEquipIf { get; init; }
    public bool ManaOnlyForAbilitiesToo { get; init; }
    public ExtraCost? WardCost { get; init; }
    public int MinimumBlockers { get; init; }
    public Quantity? OthersEnterWithCounters { get; init; }
    public int Cascade { get; init; }
    public int EnterLife { get; init; }
    public bool OpponentsCantGainLife { get; init; }
    public bool CantBeCopied { get; init; }
    public ObjectFilter? EnterReveal { get; init; }
    public ManaCost? Multikicker { get; init; }
    public ManaCost? Replicate { get; init; }
    public ManaCost? Squad { get; init; }
    public ManaCost? Dash { get; init; }
    public ManaCost? Splice { get; init; }
    public ManaCost? Miracle { get; init; }
    public bool Storm { get; init; }
    public bool Undaunted { get; init; }
    public bool Delve { get; init; }
    public bool Conspire { get; init; }
    public bool Exert { get; init; }
    public bool PayXLife { get; init; }
    public int XCountersTimes { get; init; }
    public ExtraCost? FlashbackExtra { get; init; }
    public bool FlashbackExilesX { get; init; }
    public Quantity? FlashbackReduction { get; init; }
    public IReadOnlyList<CardScript>? Split { get; init; }
    public bool CantBeSacrificed { get; init; }
    public bool CantAttackIfPowerAboveHandSize { get; init; }
    public ObjectFilter? CantAttackUnless { get; init; }
    public IReadOnlyList<string>? ProtectionFromSubtypes { get; init; }
    public ObjectFilter? GraveyardEnterBonus { get; init; }
    public int Devour { get; init; }
    public ObjectFilter? DevourFilter { get; init; }
    public Condition? ExertIf { get; init; }
    public bool StartsIfNotStarting { get; init; }
    public CounterKind? StartsWithCounter { get; init; }
    public int StartsExile { get; init; }
    public ObjectFilter? EntersAsCopy { get; init; }
    public ManaType? XManaType { get; init; }
    public int ExtraTargetCost { get; init; }

    /// <summary>Applies the card-wide rules of this script to a definition.</summary>
    public CardDefinition ApplyTo(CardDefinition d) => d with
    {
        CantBeCountered = d.CantBeCountered || CantBeCountered,
        CantBeCounteredIfXAtLeast = CantBeCounteredIfXAtLeast ?? d.CantBeCounteredIfXAtLeast,
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
        EntersWithXCounters = EntersWithXCounters || XCountersTimes > 0,
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
        EnterCounterOn = EnterCounterOn,
        EnterCounterKind = EnterCounterKind,
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
        Gift = Gift,
        FlashIf = FlashIf,
        AttackTax = AttackTax,
        AttackTaxIf = AttackTaxIf,
        BlockTax = BlockTax ?? d.BlockTax,
        BlockTaxIf = BlockTaxIf ?? d.BlockTaxIf,
        CantBeTargetedBy = CantBeTargetedBy ?? d.CantBeTargetedBy,
        CantBeCounteredIf = CantBeCounteredIf ?? d.CantBeCounteredIf,
        AdditionalLandPlayIf = AdditionalLandPlayIf,
        EntersTappedUnless = EntersTappedUnless,
        EquipDiscount = EquipDiscount,
        FreeFirstEquipIf = FreeFirstEquipIf,
        ManaOnlyForAbilitiesToo = ManaOnlyForAbilitiesToo,
        WardCost = WardCost ?? d.WardCost,
        MinimumBlockers = MinimumBlockers,
        OthersEnterWithCounters = OthersEnterWithCounters,
        Cascade = Cascade,
        EnterLife = EnterLife,
        OpponentsCantGainLife = OpponentsCantGainLife,
        CantBeCopied = CantBeCopied,
        EnterRevealFilter = EnterReveal ?? d.EnterRevealFilter,
        Multikicker = Multikicker ?? d.Multikicker,
        Replicate = Replicate ?? d.Replicate,
        Squad = Squad ?? d.Squad,
        Dash = Dash ?? d.Dash,
        Splice = Splice ?? d.Splice,
        Miracle = Miracle ?? d.Miracle,
        Storm = Storm || d.Storm,
        Undaunted = Undaunted || d.Undaunted,
        Delve = Delve || d.Delve,
        Conspire = Conspire || d.Conspire,
        Exert = Exert || d.Exert,
        PayXLife = PayXLife || d.PayXLife,
        XCountersMultiplier = XCountersTimes > 0 ? XCountersTimes : d.XCountersMultiplier,
        FlashbackExtra = FlashbackExtra ?? d.FlashbackExtra,
        FlashbackExilesX = FlashbackExilesX || d.FlashbackExilesX,
        FlashbackReduction = FlashbackReduction ?? d.FlashbackReduction,
        CantBeSacrificed = CantBeSacrificed || d.CantBeSacrificed,
        CantAttackIfPowerAboveHandSize = CantAttackIfPowerAboveHandSize || d.CantAttackIfPowerAboveHandSize,
        CantAttackUnlessDefenderControls = CantAttackUnless ?? d.CantAttackUnlessDefenderControls,
        ProtectionFromSubtypes = ProtectionFromSubtypes ?? d.ProtectionFromSubtypes,
        GraveyardEnterBonus = GraveyardEnterBonus ?? d.GraveyardEnterBonus,
        Devour = Devour > 0 ? Devour : d.Devour,
        DevourFilter = DevourFilter ?? d.DevourFilter,
        ExertIf = ExertIf ?? d.ExertIf,
        StartsOnlyIfNotStartingPlayer = StartsIfNotStarting,
        StartsWithCounter = StartsWithCounter,
        StartsExilingFromHand = StartsExile,
        EntersAsCopyOf = EntersAsCopy ?? d.EntersAsCopyOf,
        XManaType = XManaType ?? d.XManaType,
        ExtraTargetCost = ExtraTargetCost > 0 ? ExtraTargetCost : d.ExtraTargetCost,
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
                else if (a.TryGetProperty("abilityCost", out var ac))
                {
                    abilities.Add(new AbilityCostReduction(ac.TryGetProperty("sources", out var acs) ? ParseFilter(acs, ControllerFilter.You) : ObjectFilter.Anything,
                        ac.GetProperty("amount").GetInt32(), Bool(ac, "equipOnly")) { Text = Text(a) });
                }
                else if (a.TryGetProperty("spellCostIncrease", out var sci))
                {
                    abilities.Add(new SpellCostIncrease(ParseFilter(sci.GetProperty("spells"), ControllerFilter.Any), sci.GetProperty("amount").GetInt32()) { Text = Text(a) });
                }
                else if (a.TryGetProperty("spellCost", out var sc))
                {
                    var amount = sc.GetProperty("amount");
                    abilities.Add(new SpellCostReduction(ParseFilter(sc.GetProperty("spells")), amount.ValueKind == JsonValueKind.Number ? amount.GetInt32() : 0)
                    {
                        Text = Text(a),
                        AmountFrom = amount.ValueKind == JsonValueKind.Number ? null : ParseQuantity(amount),
                        FirstOfTurn = Bool(sc, "firstOfTurn"),
                        GrantsFlash = Bool(sc, "grantsFlash"),
                    });
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
                        CounterKind = a.TryGetProperty("counterKind", out var tck) && tck.GetString() != "any" ? ParseCounterKind(tck.GetString()) : CounterKind.PlusOnePlusOne,
                        Chapters = a.TryGetProperty("chapters", out var ch) ? ch.EnumerateArray().Select(x => x.GetInt32()).ToList() : Array.Empty<int>(),
                        Batched = Bool(a, "batched"),
                        AnyCounterKind = a.TryGetProperty("counterKind", out var anyKind) && anyKind.GetString() == "any",
                        ExceptFirstInDrawStep = Bool(a, "exceptFirstInDrawStep"),
                        SpellTargets = a.TryGetProperty("spellTargets", out var spt) ? ParseFilter(spt, ControllerFilter.Any) : null,
                    }));
                }
                else if (a.TryGetProperty("cost", out var cost))
                {
                    abilities.Add(WithModes(a, new ActivatedAbility
                    {
                        Cost = WithDiscardFilter(ParseCost(cost.GetString()!), a), Targets = Targets(a), Effects = Effects(a), Text = Text(a),
                        SorcerySpeed = Bool(a, "sorcery"),
                        OncePerTurn = Bool(a, "oncePerTurn"),
                        OnlyOnce = Bool(a, "onlyOnce"),
                        ActivationCondition = a.TryGetProperty("activateIf", out var ifc) ? ParseCondition(ifc) : null,
                        IsEquip = Bool(a, "equip"),
                        CostReductionPer = a.TryGetProperty("costReductionPer", out var crp) ? ParseFilter(crp, ControllerFilter.You) : null,
                        CostReductionIf = a.TryGetProperty("costReductionIf", out var cri) ? ParseCondition(cri) : null,
                        CostReductionAmount = a.TryGetProperty("costReductionAmount", out var cra) ? cra.GetInt32() : 0,
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
            CantBeCounteredIfXAtLeast = root.TryGetProperty("uncounterableIfXAtLeast", out var ucx) ? ucx.GetInt32() : null,
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
            ChooseOnEnter = root.TryGetProperty("chooseOnEnter", out var coe) ? Enum.Parse<EnterChoice>(coe.GetString()!, ignoreCase: true)
                : root.TryGetProperty("entersCounterOn", out _) ? EnterChoice.CounterOnPermanent : EnterChoice.None,
            EnterCounterOn = root.TryGetProperty("entersCounterOn", out var eco) ? ParseFilter(eco.GetProperty("filter"), ControllerFilter.You) : null,
            EnterCounterKind = root.TryGetProperty("entersCounterOn", out var eck) ? ParseCounterKind(eck.GetProperty("kind").GetString()) : CounterKind.PlusOnePlusOne,
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
                ? em.EnumerateArray().Select(m => new ManaOption(
                    m.TryGetProperty("types", out var mt) ? ParseManaTypes(mt.GetString()!) : Array.Empty<ManaType>(),
                    m.TryGetProperty("amount", out var ma) ? ma.GetInt32() : 1,
                    m.TryGetProperty("onlyFor", out var mo) ? ParseFilter(mo, ControllerFilter.Any) : null,
                    Bool(m, "abilitiesToo"))
                    {
                        OneOfEach = Bool(m, "oneOfEach"),
                        Combination = Bool(m, "combination"),
                        LifeCost = m.TryGetProperty("lifeCost", out var mlc) ? mlc.GetInt32() : 0,
                        Rider = m.TryGetProperty("rider", out var mrd) ? Enum.Parse<ManaRider>(mrd.GetString()!, ignoreCase: true) : ManaRider.None,
                        ColorsAmongYourPermanents = Bool(m, "colorsAmongYourPermanents"),
                        ColorsAmongLegendaryCreatureCardsInGraveyard = Bool(m, "colorsAmongGraveyardLegends"),
                        CommanderIdentity = Bool(m, "commanderIdentity"),
                        ColorsOpponentsLandsCouldProduce = Bool(m, "opponentsLands"),
                        TypesYourLandsCouldProduce = Bool(m, "yourLands"),
                        DamageToController = m.TryGetProperty("damage", out var mdmg) ? mdmg.GetInt32() : 0,
                        GainLife = m.TryGetProperty("gainLife", out var mgl) ? mgl.GetInt32() : 0,
                        While = m.TryGetProperty("while", out var mwh) ? ParseCondition(mwh) : null,
                    }).ToList()
                : null,
            EntersWithCountersFrom = root.TryGetProperty("entersWithCounters", out var ewcf) && (ewcf.ValueKind == JsonValueKind.Object || (ewcf.ValueKind == JsonValueKind.String && ewcf.GetString() != "X")) ? ParseQuantity(ewcf) : null,
            Adventure = root.TryGetProperty("adventure", out var adv) ? Parse(adv.GetRawText()) : null,
            Back = root.TryGetProperty("back", out var backFace) ? Parse(backFace.GetRawText()) : null,
            Gift = root.TryGetProperty("gift", out var gift) ? ParseToken(gift) : null,
            FlashIf = root.TryGetProperty("flashIf", out var fli) ? ParseCondition(fli) : null,
            AttackTax = root.TryGetProperty("attackTax", out var atx) ? ManaCost.Parse(atx.GetString()!) : null,
            AttackTaxIf = root.TryGetProperty("attackTaxIf", out var ati) ? ParseCondition(ati) : null,
            BlockTax = root.TryGetProperty("blockTax", out var btx) ? ManaCost.Parse(btx.GetString()!) : null,
            BlockTaxIf = root.TryGetProperty("blockTaxIf", out var bti) ? ParseCondition(bti) : null,
            CantBeTargetedBy = root.TryGetProperty("cantBeTargetedBy", out var cbtb) ? ParseFilter(cbtb, ControllerFilter.Any) : null,
            CantBeCounteredIf = root.TryGetProperty("uncounterableIf", out var ucif) ? ParseCondition(ucif) : null,
            AdditionalLandPlayIf = root.TryGetProperty("additionalLandPlayIf", out var alp) ? ParseCondition(alp) : null,
            EntersTappedUnless = root.TryGetProperty("entersTappedUnless", out var etu) ? ParseCondition(etu) : null,
            EquipDiscount = root.TryGetProperty("equipDiscount", out var eqd) ? eqd.GetInt32() : 0,
            FreeFirstEquipIf = root.TryGetProperty("freeFirstEquipIf", out var ffe) ? ParseCondition(ffe) : null,
            ManaOnlyForAbilitiesToo = Bool(root, "manaOnlyForAbilitiesToo"),
            WardCost = root.TryGetProperty("wardCost", out var wc) ? ParseExtraCost(wc) : null,
            MinimumBlockers = root.TryGetProperty("minimumBlockers", out var mb) ? mb.GetInt32() : 0,
            OthersEnterWithCounters = root.TryGetProperty("othersEnterWithCounters", out var oec) ? ParseQuantity(oec) : null,
            Cascade = root.TryGetProperty("cascade", out var cas) ? cas.GetInt32() : 0,
            EnterLife = root.TryGetProperty("enterLife", out var el) ? el.GetInt32() : 0,
            OpponentsCantGainLife = Bool(root, "opponentsCantGainLife"),
            CantBeCopied = Bool(root, "cantBeCopied"),
            EnterReveal = root.TryGetProperty("enterReveal", out var erv) ? ParseFilter(erv, ControllerFilter.Any) : null,
            Multikicker = root.TryGetProperty("multikicker", out var mk) ? ManaCost.Parse(mk.GetString()!) : null,
            Replicate = root.TryGetProperty("replicate", out var rpl) ? ManaCost.Parse(rpl.GetString()!) : null,
            Squad = root.TryGetProperty("squad", out var sqd) ? ManaCost.Parse(sqd.GetString()!) : null,
            Dash = root.TryGetProperty("dash", out var dsh) ? ManaCost.Parse(dsh.GetString()!) : null,
            Splice = root.TryGetProperty("splice", out var spl) ? ManaCost.Parse(spl.GetString()!) : null,
            Miracle = root.TryGetProperty("miracle", out var mir) ? ManaCost.Parse(mir.GetString()!) : null,
            Storm = Bool(root, "storm"),
            Undaunted = Bool(root, "undaunted"),
            Delve = Bool(root, "delve"),
            Conspire = Bool(root, "conspire"),
            Exert = Bool(root, "exert"),
            PayXLife = Bool(root, "payXLife"),
            XCountersTimes = root.TryGetProperty("entersWithXCountersTimes", out var xct) ? xct.GetInt32() : 0,
            FlashbackExtra = root.TryGetProperty("flashbackExtra", out var fbe) ? ParseExtraCost(fbe) : null,
            FlashbackExilesX = Bool(root, "flashbackExilesX"),
            FlashbackReduction = root.TryGetProperty("flashbackReduction", out var fbr) ? ParseQuantity(fbr) : null,
            Split = root.TryGetProperty("split", out var spl2) ? spl2.EnumerateArray().Select(h => Parse(h.GetRawText())).ToList() : null,
            CantBeSacrificed = Bool(root, "cantBeSacrificed"),
            CantAttackIfPowerAboveHandSize = Bool(root, "cantAttackIfPowerAboveHandSize"),
            CantAttackUnless = root.TryGetProperty("cantAttackUnlessDefenderControls", out var cau) ? ParseFilter(cau, ControllerFilter.Any) : null,
            ProtectionFromSubtypes = root.TryGetProperty("protectionFromSubtypes", out var pfs) ? pfs.EnumerateArray().Select(t => t.GetString()!).ToList() : null,
            GraveyardEnterBonus = root.TryGetProperty("graveyardEnterBonus", out var geb) ? ParseFilter(geb, ControllerFilter.You) : null,
            Devour = root.TryGetProperty("devour", out var dvr) ? dvr.GetInt32() : 0,
            DevourFilter = root.TryGetProperty("devourFilter", out var dvf) ? ParseFilter(dvf, ControllerFilter.You) : null,
            ExertIf = root.TryGetProperty("exertIf", out var exi) ? ParseCondition(exi) : null,
            StartsIfNotStarting = Bool(root, "startsIfNotStartingPlayer"),
            StartsWithCounter = root.TryGetProperty("startsWithCounter", out var swc) ? ParseCounterKind(swc.GetString()) : null,
            StartsExile = root.TryGetProperty("startsExilingFromHand", out var sef) ? sef.GetInt32() : 0,
            EntersAsCopy = root.TryGetProperty("entersAsCopy", out var eac) ? ParseFilter(eac, ControllerFilter.Any) : null,
            XManaType = root.TryGetProperty("xManaType", out var xmt) ? ParseManaTypes(xmt.GetString()!).Single() : null,
            ExtraTargetCost = root.TryGetProperty("extraTargetCost", out var etc) ? etc.GetInt32() : 0,
            Replaces = (root.TryGetProperty("replaces", out var rep)
                ? rep.EnumerateArray().Aggregate(Replacements.None, (acc, r) => acc | Enum.Parse<Replacements>(r.GetString()!, ignoreCase: true))
                : Replacements.None)
                | (Bool(root, "opponentsPlayWithHandsRevealed") ? Replacements.OpponentsPlayWithHandsRevealed : Replacements.None)
                | (Bool(root, "playWithTopCardRevealed") ? Replacements.PlayWithTopCardRevealed : Replacements.None),
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
            ModesOncePerTurn = Bool(e, "modesOncePerTurn"),
            ModesMayRepeat = Bool(e, "modesRepeat"),
            ExtraModeIf = e.TryGetProperty("extraModeIf", out var emi) ? ParseCondition(emi) : null,
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
        DiscardFilter = e.TryGetProperty("discardFilter", out var dfl) ? ParseFilter(dfl, ControllerFilter.Any) : null,
        ExileFromGraveyard = e.TryGetProperty("exileGraveyard", out var exg) ? exg.GetInt32() : 0,
        ExileFromGraveyardFilter = e.TryGetProperty("exileGraveyardFilter", out var exgf) ? ParseFilter(exgf, ControllerFilter.Any) : null,
    };

    /// <summary>"instant|sorcery": any of these card types.</summary>
    private static CardType ParseTypeList(string text) =>
        text.Split('|').Aggregate((CardType)0, (acc, t) => acc | Enum.Parse<CardType>(t, ignoreCase: true));

    /// <summary>{ "amount": 3, "if": condition } or { "amount": 1, "perPermanent": filter } or { "amount": 1, "perGraveyardCard": filter }</summary>
    public static CostReduction ParseCostReduction(JsonElement e) => new(
        e.GetProperty("amount").GetInt32(),
        e.TryGetProperty("if", out var c) ? ParseCondition(c) : null,
        e.TryGetProperty("perPermanent", out var p) ? ParseFilter(p, ControllerFilter.You) : null,
        e.TryGetProperty("perGraveyardCard", out var g) ? ParseFilter(g, ControllerFilter.You) : null)
    {
        ByTotalPower = Bool(e, "byTotalPower"),
        PowerFilter = e.TryGetProperty("powerFilter", out var pfl) ? ParseFilter(pfl, ControllerFilter.You) : null,
        AmountFrom = e.TryGetProperty("amountFrom", out var caf) ? ParseQuantity(caf) : null,
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
            "permanents" => AffectedScope.AllPermanents,
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
            SetChosenLandType = Bool(s, "setChosenLandType"),
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
            GivesControlToMonarch = Bool(s, "givesControlToMonarch"),
            CantAttackYou = Bool(s, "cantAttackYou"),
            Goads = Bool(s, "goads"),
            ProtectionFromRingBearers = Bool(s, "protectionFromRingBearers"),
            FromGraveyard = Bool(s, "fromGraveyard"),
            GrantsWard = s.TryGetProperty("grantsWard", out var gw) ? ManaCost.Parse(gw.GetString()!) : null,
            ExtraTriggers = Bool(s, "extraTriggers"),
            GrantsGraveyardAbilities = s.TryGetProperty("graveyardAbilities", out var ga) ? ParseFilter(ga, ControllerFilter.Any) : null,
            LosesKeywords = s.TryGetProperty("loseKeywords", out var lk) ? lk.EnumerateArray().Select(x => ParseKeyword(x.GetString()!)).ToList() : null,
            PreventsDamage = s.TryGetProperty("preventDamage", out var pd) ? Enum.Parse<StaticDamagePrevention>(pd.GetString()!, ignoreCase: true) : StaticDamagePrevention.None,
            CantActivateAbilities = Bool(s, "cantActivate"),
            CopiesChosenCreature = Bool(s, "copyOfChosen"),
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
            ControlledByDefendingPlayer = Bool(e, "controlledByDefendingPlayer"),
            RepeatFrom = e.TryGetProperty("upTo", out var upTo) ? ParseQuantity(upTo) : null,
            PerPlayer = Bool(e, "perPlayer"),
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
        "created" => new Subject(SubjectKind.Created),
        "found" => new Subject(SubjectKind.Found),
        "sacrificers" => new Subject(SubjectKind.Sacrificers),
        "amassed" => new Subject(SubjectKind.Amassed),
        "discarded" => new Subject(SubjectKind.Discarded),
        "yourRingBearer" => new Subject(SubjectKind.RingBearer),
        "playerToYourRight" => new Subject(SubjectKind.PlayerToYourRight),
        "attackersOfTriggered" => new Subject(SubjectKind.AttackersOfTriggered),
        "attackers" => new Subject(SubjectKind.Attackers),
        "chosen" => new Subject(SubjectKind.Chosen),
        "chosenPlayer" => new Subject(SubjectKind.ChosenPlayer),
        "exiled" => new Subject(SubjectKind.ExiledThisWay),
        "lastControlled" => new Subject(SubjectKind.ControlGainedThisWay),
        "opponentsDamagedBySameName" => new Subject(SubjectKind.OpponentsDamagedBySameName),
        "youAndChosenPlayer" => new Subject(SubjectKind.YouAndChosenPlayer),
        "opponentsWhoVotedWithYou" => new Subject(SubjectKind.OpponentsWhoVotedWithYou),
        "youAndOpponentsWhoVotedWithYou" => new Subject(SubjectKind.YouAndOpponentsWhoVotedWithYou),
        _ when text.StartsWith("eachTarget") && int.TryParse(text[10..], out int et) => new Subject(SubjectKind.EachTarget, et - 1),
        _ when text.StartsWith("targetController") && int.TryParse(text[16..], out int c) => new Subject(SubjectKind.TargetController, c - 1),
        _ when text.StartsWith("target") && int.TryParse(text[6..], out int n) => Subject.TargetAt(n - 1),
        _ => throw new FormatException($"Unknown subject '{text}'."),
    };

    /// <summary>A subject string, or { "each": filter } for every matching permanent (controller defaults to any).</summary>
    public static Subject ParseSubject(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? ParseSubject(e.GetString())
        : e.TryGetProperty("each", out var f) ? Subject.Each(ParseFilter(f, ControllerFilter.Any)) with
        {
            ControlledByTarget = e.TryGetProperty("controlledBy", out _),
            ExceptTargets = Bool(e, "exceptTargets"),
            AttachedToTarget = e.TryGetProperty("attachedTo", out _),
            Index = e.TryGetProperty("attachedTo", out var att) ? ParseSubject(att.GetString()).Index
                : e.TryGetProperty("controlledBy", out var cb) ? ParseSubject(cb.GetString()).Index : 0,
        }
        : e.TryGetProperty("discarded", out var df) ? new Subject(SubjectKind.Discarded, Filter: ParseFilter(df, ControllerFilter.Any))
        : e.TryGetProperty("choose", out var cf) ? new Subject(SubjectKind.ChooseOne, Filter: ParseFilter(cf, ControllerFilter.You))
        : e.TryGetProperty("damagedThisWay", out var dtw) ? new Subject(SubjectKind.DamagedThisWay, Filter: ParseFilter(dtw, ControllerFilter.Any))
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
        "chapter" => TriggerEvent.Chapter,
        "permanentEnters" => TriggerEvent.PermanentEnters,
        "leavesGraveyard" => TriggerEvent.LeavesGraveyard,
        "precombatMain" => TriggerEvent.YourPrecombatMain,
        "putIntoGraveyard" => TriggerEvent.PutIntoGraveyard,
        "becomesTarget" => TriggerEvent.BecomesTargetOfOpponent,
        "activateAbility" => TriggerEvent.YouActivateAbility,
        "playerLosesLife" => TriggerEvent.PlayerLosesLife,
        "youSacrifice" => TriggerEvent.YouSacrifice,
        "creatureExiledInstead" => TriggerEvent.CreatureExiledInstead,
        "dealtNoncombatDamage" => TriggerEvent.DealtNoncombatDamage,
        "becomesBlocked" => TriggerEvent.BecomesBlocked,
        "youScry" => TriggerEvent.YouScry,
        "combatDamageToYou" => TriggerEvent.CombatDamageToYou,
        "finalChapterResolved" => TriggerEvent.FinalChapterResolved,
        "ringTempts" => TriggerEvent.RingTemptsYou,
        "leaves" => TriggerEvent.LeavesBattlefield,
        "ringBearerChosen" => TriggerEvent.RingBearerChosen,
        "tokenCreated" => TriggerEvent.TokenCreated,
        "youAttackPlayer" => TriggerEvent.YouAttackPlayer,
        "dealsDamageTo" => TriggerEvent.DealsDamageToCreature,
        "excessNoncombatDamage" => TriggerEvent.ExcessNoncombatDamage,
        "becomesTargetOfSpell" => TriggerEvent.BecomesTargetOfSpell,
        "phasesIn" => TriggerEvent.PhasesIn,
        "equippedBlocksOrBlocked" => TriggerEvent.EquippedBlocksOrBecomesBlocked,
        "permanentBecomesTarget" => TriggerEvent.PermanentBecomesTargetOfOpponent,
        "monarchEndStep" => TriggerEvent.MonarchEndStep,
        "damageToYouPrevented" => TriggerEvent.DamageToYouPrevented,
        "playersFinishVoting" => TriggerEvent.PlayersFinishVoting,
        "opponentSacrifices" => TriggerEvent.OpponentSacrifices,
        "exerted" => TriggerEvent.Exerted,
        "transforms" => TriggerEvent.Transforms,
        "cycled" => TriggerEvent.Cycled,
        "castThis" => TriggerEvent.CastThis,
        "opponentActivatesAbility" => TriggerEvent.OpponentActivatesAbility,
        "opponentTapsArtifactForMana" => TriggerEvent.OpponentTapsArtifactForMana,
        "creatureLeaves" => TriggerEvent.CreatureLeaves,
        "creaturesAttackOpponent" => TriggerEvent.CreaturesAttackOpponent,
        "playerAttacks" => TriggerEvent.PlayerAttacks,
        "youActivateNonManaAbility" => TriggerEvent.YouActivateNonManaAbility,
        "enchantedControllersUpkeep" => TriggerEvent.EnchantedControllersUpkeep,
        "dealsDamageToOpponent" => TriggerEvent.DealsDamageToOpponent,
        "becomesTargetOfAny" => TriggerEvent.BecomesTarget,
        "attachedBecomesTarget" => TriggerEvent.AttachedBecomesTarget,
        "counterRemoved" => TriggerEvent.CounterRemoved,
        "playerTapsLandForMana" => TriggerEvent.PlayerTapsLandForMana,
        "blocksOrBlockedBy" => TriggerEvent.BlocksOrBecomesBlockedByCreature,
        "blocksCreature" => TriggerEvent.BlocksCreature,
        "youBecomeTargetOfOpponent" => TriggerEvent.YouBecomeTargetOfOpponent,
        "discardedByOpponent" => TriggerEvent.DiscardedByOpponent,
        "dealtDamage" => TriggerEvent.DealtDamage,
        "attachedBlocks" => TriggerEvent.AttachedBlocks,
        "permanentDies" => TriggerEvent.PermanentDies,
        "state" => TriggerEvent.StateTrigger,
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
            f.TryGetProperty("without", out var without) && without.ValueKind == JsonValueKind.String ? ParseKeyword(without.GetString()!) : null,
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
            OptBool("commander"),
            OptBool("inHand"),
            Bool(f, "fromBattlefieldThisTurn"),
            Bool(f, "paidWithTreasure"),
            Bool(f, "chosenParity"),
            Bool(f, "sharesNameWithYourLegendary"),
            Int("maxToughness"),
            Bool(f, "notChosenType"),
            Bool(f, "leastPower"),
            Bool(f, "damagedThisTurn"),
            Bool(f, "blockingSource"),
            Bool(f, "dealtCombatDamageToYou"),
            Bool(f, "maxManaValueTriggerAmount"))
        {
            MaxPowerTriggered = Bool(f, "maxPowerTriggered"),
            Historic = Bool(f, "historic"),
            PowerIsX = Bool(f, "powerIsX"),
            DamagedThisTurnByYourSpider = Bool(f, "damagedThisTurnByYourSpider"),
            LesserPowerThanSource = Bool(f, "lesserPower"),
            ToughnessLessThanSourcePower = Bool(f, "toughnessLessThanPower"),
            GreaterPowerThanSource = Bool(f, "greaterPower"),
            BlockedOrBlockedByLegendaryThisTurn = Bool(f, "blockedOrBlockedByLegendary"),
            SharesColorWithYourLegendaryCreature = Bool(f, "sharesColorWithYourLegendary"),
            NoSharedCreatureTypeWithYours = Bool(f, "noSharedCreatureType"),
            Renowned = Bool(f, "renowned"),
            Transformed = OptBool("transformed"),
            WithCounterKind = f.TryGetProperty("hasCounterKind", out var hck) ? ParseCounterKind(hck.GetString()) : null,
            AttackingYou = Bool(f, "attackingYou"),
            FromGraveyard = Bool(f, "fromGraveyard"),
            HasXInCost = Bool(f, "hasX"),
            CastFromHand = Bool(f, "castFromHand"),
            ExiledWithSource = Bool(f, "exiledWithSource"),
            ManaValueIsX = Bool(f, "manaValueIsX"),
            SharesCreatureTypeWithTriggered = Bool(f, "sharesCreatureTypeWithTriggered"),
            NotColors = f.TryGetProperty("notColors", out var notColors) ? notColors.EnumerateArray().Select(c => c.GetString()!).ToList() : null,
            ChosenName = Bool(f, "chosenName"),
            ManaValueIsTriggerAmount = Bool(f, "manaValueIsTriggerAmount"),
            IsSource = Bool(f, "self"),
            WithoutKeywords = f.TryGetProperty("without", out var withoutAll) && withoutAll.ValueKind == JsonValueKind.Array
                ? withoutAll.EnumerateArray().Select(k => ParseKeyword(k.GetString()!)).ToList() : null,
            PowerNotEqualToughness = Bool(f, "powerNotEqualToughness"),
            NotOwnedByYou = Bool(f, "notOwnedByYou"),
            NotTriggered = Bool(f, "notTriggered"),
            MaxManaValueX = Bool(f, "maxManaValueX"),
            TargetsYou = Bool(f, "targetsYou"),
        };
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
                "untapped" => new SourceUntapped(),
                "attacking" => new SourceAttacking(),
                "triggeredWasAttacking" => new TriggeredWasAttacking(),
                "youSacrificed" => new YouSacrificedThisWay(),
                "castFromHand" => new WasCastFromHand(),
                "wasCast" => new WasCast(),
                "enduringStory" => new HasEnduringStory(),
                "castFromGraveyard" => new WasCastFromGraveyard(),
                "giftPromised" => new GiftPromised(),
                "citysBlessing" => new HasCitysBlessing(),
                "opponentHasMostLife" => new OpponentHasMostLife(),
                "ringBearer" => new IsRingBearer(),
                "hasRingBearer" => new HasRingBearer(),
                "yourPermanentLeft" => new YourPermanentLeftThisTurn(),
                "attackedThisTurn" => new SourceAttackedThisTurn(),
                "attackedOrBlockedThisTurn" => new SourceAttackedOrBlockedThisTurn(),
                "greatestPower" => new YouControlGreatestPower(),
                "triggeredPlayerAttackedYou" => new TriggeredPlayerAttackedYou(),
                "monarch" => new IsMonarch(),
                "noMonarch" => new NoMonarch(),
                "noVotes" => new ReceivedNoVotes(),
                "renowned" => new SourceRenowned(),
                "transformed" => new SourceTransformed(),
                "frontFaceUp" => new SourceFrontFaceUp(),
                "dealtDamage" => new SourceHasDealtDamage(),
                "triggeredHadCounters" => new TriggeredHadCounters(),
                "hasAnyCounters" => new SourceHasAnyCounters(),
                "castDuringMainPhase" => new CastDuringYourMainPhase(),
                "exertedThisTurn" => new ExertedThisTurn(),
                "triggeredPlayerMostLife" => new TriggeredPlayerHasMostLife(),
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
        if (c.TryGetProperty("activatedThisTurn", out var att)) return new ActivatedThisTurn(att.GetInt32());
        if (c.TryGetProperty("resolvedThisTurn", out var rtt)) return new ResolvedThisTurn(rtt.GetInt32(), Bool(c, "exactly"));
        if (c.TryGetProperty("targetControlledByYou", out var tcy)) return new TargetControlledByYou(ParseSubject(tcy.GetString()).Index);
        if (c.TryGetProperty("drawn", out var drn)) return new CardsDrawnThisTurn(drn.GetInt32());
        if (c.TryGetProperty("triggered", out var trg)) return new TriggeredMatches(ParseFilter(trg, ControllerFilter.Any));
        if (c.TryGetProperty("attackersExactly", out var axe)) return new AttackingCreaturesExactly(axe.GetInt32());
        if (c.TryGetProperty("attackedWith", out var awi)) return new AttackedWithAtLeast(awi.GetInt32());
        if (c.TryGetProperty("attackingPower", out var apw)) return new AttackingPowerAtLeast(apw.GetInt32());
        if (c.TryGetProperty("triggeredCounters", out var trc)) return new TriggeredHasCounters(trc.GetInt32());
        if (c.TryGetProperty("targetAttachedTo", out var tat))
            return new TargetAttachedTo(ParseSubject(tat.GetString()).Index, ParseSubject(c.GetProperty("to").GetString()).Index);
        if (c.TryGetProperty("atLeast", out var al)) return new QuantityAtLeast(ParseQuantity(al), c.GetProperty("value").GetInt32());
        if (c.TryGetProperty("sourceIs", out var sis)) return new SourceIs(ParseFilter(sis, ControllerFilter.Any));
        if (c.TryGetProperty("yourCreaturesDied", out var ycd)) return new YourCreaturesDied(ycd.GetInt32());
        if (c.TryGetProperty("equippedInCombatWith", out var eicw)) return new EquippedInCombatWith(ParseFilter(eicw, ControllerFilter.Any));
        if (c.TryGetProperty("sacrificedThisTurn", out var stt))
            return new SacrificedThisTurn(ParseFilter(stt, ControllerFilter.Any), c.TryGetProperty("count", out var sttc) ? sttc.GetInt32() : 1);
        if (c.TryGetProperty("sacrificed", out var sacd)) return new SacrificedMatches(ParseFilter(sacd, ControllerFilter.Any));
        if (c.TryGetProperty("not", out var inner)) return new Not(ParseCondition(inner));
        if (c.TryGetProperty("topOfLibrary", out var tol)) return new TopOfLibrary(ParseFilter(tol, ControllerFilter.Any));
        if (c.TryGetProperty("target", out var ti)) return new TargetMatches(ParseSubject(ti.GetString()).Index, ParseFilter(c.GetProperty("is"), ControllerFilter.Any));
        if (c.TryGetProperty("lifeAboveStarting", out var las)) return new LifeAboveStarting(las.GetInt32());
        if (c.TryGetProperty("all", out var all)) return new All(all.EnumerateArray().Select(ParseCondition).ToList());
        if (c.TryGetProperty("totalPower", out var tp)) return new TotalPowerAtLeast(tp.GetInt32());
        if (c.TryGetProperty("cardTypesInGraveyard", out var ctg)) return new CardTypesInGraveyard(ctg.GetInt32());
        if (c.TryGetProperty("opponentHasMore", out var ohm)) return new OpponentHasMore(ohm.GetString()!);
        if (c.TryGetProperty("enteredThisTurn", out var ett)) return new EnteredThisTurn(ParseFilter(ett, ControllerFilter.Any));
        if (c.TryGetProperty("moreVotes", out var mvo)) return new MoreVotes(mvo.GetInt32());
        if (c.TryGetProperty("opponents", out var opc)) return new OpponentsAtLeast(opc.GetInt32());
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
                    "foundThisWay" => new Quantity(0, QuantityKind.FoundThisWay),
                    "-lifeGained" => new Quantity(0, QuantityKind.LifeGainedThisTurn, Multiplier: -1),
                    "life" => new Quantity(0, QuantityKind.YourLife),
                    "handSize" => new Quantity(0, QuantityKind.HandSize),
                    "opponentCount" => new Quantity(0, QuantityKind.OpponentCount),
                    "cardsInAllHands" => new Quantity(0, QuantityKind.CardsInAllHands),
                    "greatestCommanderManaValue" => new Quantity(0, QuantityKind.GreatestCommanderManaValue),
                    "otherAttackersSharingType" => new Quantity(0, QuantityKind.OtherAttackersSharingTypeWithTriggered),
                    "timesKicked" => new Quantity(0, QuantityKind.TimesKicked),
                    "squadPaid" => new Quantity(0, QuantityKind.SquadPaid),
                    "votesReceived" => new Quantity(0, QuantityKind.VotesReceived),
                    "opponentsVotedOtherwise" => new Quantity(0, QuantityKind.OpponentsVotedOtherwise),
                    "affectedHandSize" => new Quantity(0, QuantityKind.AffectedHandSize),
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
                    "manaSpent" => new Quantity(0, QuantityKind.ManaSpent),
                    "returned" => new Quantity(0, QuantityKind.ReturnedThisWay),
                    "greatestPower" => new Quantity(0, QuantityKind.GreatestPower),
                    "greatestToughness" => new Quantity(0, QuantityKind.GreatestToughness),
                    "otherSpellsManaValue" => new Quantity(0, QuantityKind.ManaValueOfOtherSpellsThisTurn),
                    "milledManaValue" => new Quantity(0, QuantityKind.MilledManaValue),
                    "tapped" => new Quantity(0, QuantityKind.TappedThisWay),
                    "ringLevel" => new Quantity(0, QuantityKind.RingLevel),
                    "attached" => new Quantity(0, QuantityKind.AttachedThisWay),
                    "ringBearerPower" => new Quantity(0, QuantityKind.RingBearerPower),
                    "permanentsSacrificedThisTurn" => new Quantity(0, QuantityKind.PermanentsSacrificedThisTurn),
                    "sacrificedThisWay" => new Quantity(0, QuantityKind.SacrificedThisWay),
                    "amassedPower" => new Quantity(0, QuantityKind.AmassedPower),
                    "halfLibrary" => new Quantity(0, QuantityKind.HalfLibrary),
                    "otherCreaturesSharingType" => new Quantity(0, QuantityKind.OtherCreaturesSharingTypeWithAffected),
                    var unknown => throw new FormatException($"Unknown quantity '{unknown}'."),
                };
        }
        if (e.TryGetProperty("attackingPower", out var apq)) return new Quantity(0, QuantityKind.AttackingPower, ParseFilter(apq));
        if (e.TryGetProperty("damageTakenThisTurn", out var dtt)) return new Quantity(0, QuantityKind.DamageTakenThisTurn, Index: ParseSubject(dtt.GetString()).Index);
        if (e.TryGetProperty("greatestPowerOf", out var gpo)) return new Quantity(0, QuantityKind.TargetPlayersGreatestPower, Index: ParseSubject(gpo.GetString()).Index);
        if (e.TryGetProperty("votes", out var votesFor)) return new Quantity(votesFor.GetInt32(), QuantityKind.VotesFor);
        if (e.TryGetProperty("sum", out var sum)) return new Quantity(0, QuantityKind.Sum) { Parts = sum.EnumerateArray().Select(ParseQuantity).ToList() };
        if (e.TryGetProperty("halfUp", out var halfUp)) return new Quantity(0, QuantityKind.HalfRoundedUp) { Parts = new[] { ParseQuantity(halfUp) } };
        int times = e.TryGetProperty("times", out var t) ? t.GetInt32() : 1;
        int offset = e.TryGetProperty("offset", out var off) ? off.GetInt32() : 0;
        if (e.TryGetProperty("milled", out var mil)) return new Quantity(0, QuantityKind.MilledThisWay, ParseFilter(mil, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("discarded", out var dsc)) return new Quantity(0, QuantityKind.DiscardedThisWay, ParseFilter(dsc, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("greatestAmongOpponents", out var gao)) return new Quantity(0, QuantityKind.GreatestAmongOpponents, ParseFilter(gao, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("countersAmong", out var cam))
            return new Quantity(0, QuantityKind.CountersAmong, ParseFilter(cam), times, Counter: ParseCounterKind(e.TryGetProperty("kind", out var cak) ? cak.GetString() : null), Offset: offset);
        if (e.TryGetProperty("graveyardsWith", out var gyw)) return new Quantity(gyw.GetInt32(), QuantityKind.GraveyardsWithAtLeast, Multiplier: times, Offset: offset);
        if (e.TryGetProperty("exiled", out var exl)) return new Quantity(0, QuantityKind.ExiledThisWay, ParseFilter(exl, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("greatestManaValue", out var gmv)) return new Quantity(0, QuantityKind.GreatestManaValue, ParseFilter(gmv), times, Offset: offset);
        if (e.TryGetProperty("distinctManaValues", out var dmv)) return new Quantity(0, QuantityKind.DistinctManaValues, ParseFilter(dmv), times, Offset: offset);
        if (e.TryGetProperty("spellsCastBefore", out var scb)) return new Quantity(0, QuantityKind.SpellsCastBeforeTriggered, ParseFilter(scb, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("spellsCast", out var sct)) return new Quantity(0, QuantityKind.SpellsCastThisTurn, ParseFilter(sct, ControllerFilter.Any), times, Offset: offset);
        if (e.TryGetProperty("count", out var count))
            return new Quantity(0, QuantityKind.PermanentCount, ParseFilter(count), times,
                Index: e.TryGetProperty("controlledBy", out var qcb) && qcb.GetString() != "triggeredPlayer" ? ParseSubject(qcb.GetString()).Index : 0)
            {
                ControlledByTarget = e.TryGetProperty("controlledBy", out var qcb2) && qcb2.GetString() != "triggeredPlayer",
                ControlledByTriggeredPlayer = e.TryGetProperty("controlledBy", out var qcb3) && qcb3.GetString() == "triggeredPlayer",
            };
        if (e.TryGetProperty("graveyard", out var gy))
            return new Quantity(0, QuantityKind.GraveyardCount, ParseFilter(gy), times, Index: e.TryGetProperty("of", out var gof) && gof.GetString() != "affected" ? ParseSubject(gof.GetString()).Index : 0)
            {
                ControlledByTarget = e.TryGetProperty("of", out var gof2) && gof2.GetString() != "affected",
                OfAffectedPlayer = e.TryGetProperty("of", out var gof3) && gof3.GetString() == "affected",
            };
        if (e.TryGetProperty("attacking", out var att)) return new Quantity(0, QuantityKind.AttackingCount, ParseFilter(att), times);
        if (e.TryGetProperty("counters", out var ctr)) return new Quantity(0, QuantityKind.SourceCounters, Multiplier: times, Counter: ParseCounterKind(ctr.GetString()));
        if (e.TryGetProperty("power", out var power))
        {
            var subject = ParseSubject(power.GetString());
            if (subject.Kind == SubjectKind.Attached) return new Quantity(0, QuantityKind.AttachedPower, Multiplier: times);
            return subject.Kind == SubjectKind.Self
                ? new Quantity(0, QuantityKind.SourcePower, Multiplier: times)
                : new Quantity(0, QuantityKind.TargetPower, Multiplier: times, Index: subject.Index);
        }
        if (e.TryGetProperty("toughness", out var toughness))
            return ParseSubject(toughness.GetString()).Kind == SubjectKind.Self
                ? new Quantity(0, QuantityKind.SourceToughness, Multiplier: times)
                : new Quantity(0, QuantityKind.TargetToughness, Multiplier: times, Index: ParseSubject(toughness.GetString()).Index);
        if (e.TryGetProperty("manaValue", out var manaValue))
            return new Quantity(0, QuantityKind.TargetManaValue, Multiplier: times, Index: ParseSubject(manaValue.GetString()).Index);
        throw new FormatException($"Unknown quantity {e.GetRawText()}.");
    }

    /// <summary>"discardFilter": the cards discarded to pay the cost must match it.</summary>
    private static AbilityCost WithDiscardFilter(AbilityCost cost, JsonElement a) =>
        a.TryGetProperty("discardFilter", out var df) && cost.Extra is { } extra
            ? cost with { Extra = extra with { DiscardFilter = ParseFilter(df, ControllerFilter.Any) } }
            : cost;

    /// <summary>
    /// "{2}{R}, {T}, sacrifice" plus "discard", "discard:2", "life:2", "sacrifice:creature" (another creature you control),
    /// "sacrifice:artifact|creature" (another artifact or creature), "sacrifice:Goblin" (another Goblin), "sacrificeAny:Goblin"
    /// (a Goblin, this permanent too), "exileGraveyardCards:1:creature" (one creature card from your graveyard),
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
        bool loyaltyX = false;
        int addCounters = 0, tapCount = 0, crew = 0, sacrificeCount = 1, exileGraveyard = 0, snow = 0;
        var addKind = CounterKind.PlusOnePlusOne;
        ObjectFilter? tapFilter = null;
        ObjectFilter? sacrificeOther = null;
        ObjectFilter? returnExiled = null, exileGraveyardFilter = null;
        foreach (var raw in text.Split(','))
        {
            var part = raw.Trim();
            var lower = part.ToLowerInvariant();
            if (part == "{T}") tap = true;
            else if (System.Text.RegularExpressions.Regex.IsMatch(part, @"^([+\-\u2212]\d+|0)$"))
                loyalty = int.Parse(part.Replace('\u2212', '-'));
            else if (part is "-X" or "\u2212X") loyaltyX = true;
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
            else if (lower.StartsWith("tappermanents:"))
            {
                // "tapPermanents:2:artifact" is "tap two untapped artifacts you control" (this permanent too; "artifact|creature" for either).
                var bits = part[14..].Split(':');
                tapCount = int.Parse(bits[0]);
                tapFilter = bits.Length > 1 ? new ObjectFilter(ParseTypeList(bits[1])) : new ObjectFilter();
            }
            else if (lower.StartsWith("crew:")) crew = int.Parse(lower[5..]);
            else if (lower.StartsWith("exilegraveyardcards:"))
            {
                // "exileGraveyardCards:1:creature": one creature card.
                var bits = part[20..].Split(':');
                exileGraveyard = int.Parse(bits[0]);
                if (bits.Length > 1) exileGraveyardFilter = new ObjectFilter(ParseTypeList(bits[1]), Controller: ControllerFilter.Any);
            }
            else if (lower.StartsWith("returnexiled:")) returnExiled = new ObjectFilter(Enum.Parse<CardType>(part[13..], ignoreCase: true), Controller: ControllerFilter.Any);
            else if (lower.StartsWith("sacrifice:") || lower.StartsWith("sacrificeany:"))
            {
                // "sacrificeAny:creature" is "sacrifice a creature" (this permanent too); "sacrifice:creature" is "another creature".
                bool anyPermanent = lower.StartsWith("sacrificeany:");
                var what = part[(anyPermanent ? 13 : 10)..];
                // "sacrifice:Food*3": sacrifice three Foods.
                if (what.Contains('*')) { sacrificeCount = int.Parse(what[(what.IndexOf('*') + 1)..]); what = what[..what.IndexOf('*')]; }
                bool legendary = what.StartsWith("legendary ", StringComparison.OrdinalIgnoreCase);
                if (legendary) what = what[10..];
                var kinds = what.Split('|');
                sacrificeOther = (kinds.All(k => Enum.TryParse<CardType>(k, ignoreCase: true, out _))
                    ? new ObjectFilter(kinds.Aggregate((CardType)0, (acc, k) => acc | Enum.Parse<CardType>(k, ignoreCase: true)), Other: !anyPermanent)
                    : new ObjectFilter(Subtype: what, Other: !anyPermanent)) with { Supertype = legendary ? Supertype.Legendary : 0 };
            }
            else if (part.Length > 0)
            {
                // {S}: mana from a snow source, paid on its own.
                snow += System.Text.RegularExpressions.Regex.Matches(part, @"\{S\}").Count;
                var rest = part.Replace("{S}", "");
                if (rest.Length > 0) mana = ManaCost.Parse(rest);
            }
        }
        var extra = discard > 0 || life > 0 || sacrificeOther is not null || tapFilter is not null || crew > 0 || exileGraveyard > 0 || returnExiled is not null
            ? new ExtraCost(discard, sacrificeOther, sacrificeCount, life) { TapCreatures = tapFilter, TapCount = tapCount, CrewPower = crew, ExileFromGraveyard = exileGraveyard, ExileFromGraveyardFilter = exileGraveyardFilter, ReturnExiledWithSource = returnExiled }
            : null;
        return new AbilityCost(mana, tap, sacrifice)
        {
            Extra = extra, FromGraveyard = fromGraveyard, RemoveCounters = removeCounters, RemoveCounterKind = removeKind, ExileSelf = exileSelf,
            Loyalty = loyalty, LoyaltyX = loyaltyX,
            AddCounters = addCounters, AddCounterKind = addKind, ReturnSelfToHand = returnToHand, TapGranter = tapGranter, SnowMana = snow,
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

        if (e.TryGetProperty("scry", out var scryN)) return scryN.ValueKind == JsonValueKind.Number ? new Scry(scryN.GetInt32()) : new Scry(0) { CountFrom = ParseQuantity(scryN) };
        if (e.TryGetProperty("surveil", out _)) return new Surveil(Int("surveil"));
        if (Str("fight") is { } fighter) return new Fight(ParseSubject(fighter), ParseSubject(Str("with") ?? "target2"));
        if (e.TryGetProperty("discard", out _)) return new Discard(Qty("discard"), Subj("who", "you")) { AtRandom = Flag("random") };
        if (e.TryGetProperty("if", out var condition))
            return new IfThen(ParseCondition(condition), EffectList(e.GetProperty("then")), e.TryGetProperty("else", out var otherwise) ? EffectList(otherwise) : null);
        if (Str("may") is { } prompt) return new MayDo(prompt, Effects(e)) { OncePerTurn = Flag("oncePerTurn"), Else = e.TryGetProperty("else", out var mde) ? EffectList(mde) : null };
        if (e.TryGetProperty("damage", out _)) return new DealDamage(Qty("damage"), Subj("to", "target")) { ExcessToController = Flag("excessToController"), Unpreventable = Flag("cantBePrevented") };
        if (e.TryGetProperty("draw", out _)) return new DrawCards(Qty("draw"), Subj("who", "you"));
        if (e.TryGetProperty("gainLife", out _)) return new GainLife(Qty("gainLife"), Subj("who", "you"));
        if (e.TryGetProperty("loseLife", out _)) return new LoseLife(Qty("loseLife"), Subj("who", "you"));
        if (e.TryGetProperty("mill", out _)) return new Mill(Qty("mill"), Subj("who", "you")) { RepeatWhileNonlandShareColor = Flag("repeatIfShareColor") };
        if (Value("destroy") is { } destroy) return new Destroy(destroy) { CantBeRegenerated = Flag("noRegeneration") };
        if (Value("exile") is { } exile)
            return new ExileIt(exile) { WithCounter = Str("withCounter") is { } wc ? ParseCounterKind(wc) : null, ExceptCreatedThisWay = Flag("exceptCreated"), Linked = Flag("linked") };
        if (Flag("loseGame")) return new LoseGame { Who = e.TryGetProperty("who", out var lgw) ? ParseSubject(lgw) : null };
        if (Value("removeFromCombat") is { } rfc) return new RemoveFromCombat(rfc);
        if (e.TryGetProperty("guessTop", out _)) return new GuessTopCard(Int("guessTop"), EffectList(e.GetProperty("right")), EffectList(e.GetProperty("wrong")));
        if (e.TryGetProperty("sacrificeAnyNumber", out var sany)) return new SacrificeAnyNumber(ParseFilter(sany, ControllerFilter.You));
        if (e.TryGetProperty("revealTopPutAny", out _)) return new RevealTopPutAny(Qty("revealTopPutAny"), e.TryGetProperty("filter", out var rtpf) ? ParseFilter(rtpf, ControllerFilter.Any) : ObjectFilter.Anything);
        if (Flag("noteCreatureType")) return new NoteCreatureType();
        if (e.TryGetProperty("atNextEndStepOf", out var aneo))
            return new AtPlayersNextEndStep(ParseSubject(aneo), new TriggeredAbility { Trigger = TriggerEvent.NextUpkeep, Targets = Targets(e), Effects = Effects(e), Text = Text(e) });
        if (e.TryGetProperty("moveCounterOfEachMissingKind", out var mcm))
            return new MoveCounterOfEachMissingKind(ParseSubject(mcm.GetProperty("from")), ParseSubject(mcm.GetProperty("to")));
        if (e.TryGetProperty("moveCounters", out var mvc))
            return new MoveChosenCounters(ParseSubject(mvc.GetProperty("from")), ParseSubject(mvc.GetProperty("to")), e.TryGetProperty("then", out var mvt) ? EffectList(mvt) : Array.Empty<Effect>());
        if (Value("loseAllAbilities") is { } laa) return new LoseAllAbilitiesUntilEndOfTurn(laa);
        if (Value("bounce") is { } bounce) return new ReturnToHand(bounce);
        if (Value("tap") is { } tap) return new TapIt(tap);
        if (Value("untap") is { } untap) return new UntapIt(untap);
        if (Value("counter") is { } counter)
            return new CounterSpell(counter)
            {
                UnlessPays = e.TryGetProperty("unlessPays", out var up) ? ManaCost.Parse(up.GetString()!) : null,
                ExilePermanentPlayable = Flag("exilePermanentPlayable"),
            };
        if (Value("attach") is { } attach) return e.TryGetProperty("to", out var attachTo) ? new Attach(attach, ParseSubject(attachTo)) : new AttachSelf(attach);
        if (Value("reanimate") is { } reanimate) return ParseEntering(e, reanimate);
        if (Value("toLibrary") is { } toLibrary)
            return new PutIntoLibrary(toLibrary, Flag("bottom")) { Top = Flag("top"), Position = e.TryGetProperty("position", out var pos) ? pos.GetInt32() : 0 };
        if (e.TryGetProperty("counterChoice", out var cch))
            return new AddChosenCounter(cch.EnumerateArray().Select(k => ParseCounterKind(k.GetString())).ToList(), Subj("what", "self"));
        if (Value("countersOfTriggeredKinds") is { } coktk) return new AddCountersOfTriggeredKinds(coktk);
        if (e.TryGetProperty("chooseEffect", out var cho))
            return new ChooseOneEffect(cho.EnumerateArray().Select(o => new EffectChoice(Text(o), Effects(o))).ToList());
        if (Value("gainControl") is { } gain)
            return new GainControl(gain, Flag("untilEndOfTurn"), e.TryGetProperty("to", out var gto) ? ParseSubject(gto) : null) { WhileYouControlSource = Flag("whileYouControl"), UntilEndOfYourNextTurn = Flag("untilEndOfYourNextTurn") };
        if (Str("mayPay") is { } payPrompt)
            return new MayPay(payPrompt, e.TryGetProperty("mana", out var pm) ? ManaCost.Parse(pm.GetString()!) : null,
                e.TryGetProperty("cost", out var pc) ? ParseExtraCost(pc) : null, Effects(e))
            {
                Options = e.TryGetProperty("options", out var mpo)
                    ? mpo.EnumerateArray().Select(o => new CostOption(o.TryGetProperty("mana", out var om) ? ManaCost.Parse(om.GetString()!) : null,
                        o.TryGetProperty("cost", out var oc) ? ParseExtraCost(oc) : null)).ToList()
                    : null,
                Else = e.TryGetProperty("else", out var mpe) ? EffectList(mpe) : null,
            };
        if (e.TryGetProperty("revealTop", out var rvt))
            return new RevealTop(ParseFilter(rvt, ControllerFilter.Any), Flag("optional"), Effects(e)) { Else = e.TryGetProperty("else", out var rvte) ? EffectList(rvte) : null };
        if (Flag("damageCantBePrevented")) return new DamageCantBePreventedThisTurn();
        if (e.TryGetProperty("simultaneously", out var sim)) return new Simultaneously(EffectList(sim));
        if (Value("goad") is { } goad) return new Goad(goad);
        if (Value("protectionFromChosenType") is { } pfct) return new ProtectionFromChosenType(pfct);
        if (Value("protectionFromColorsOf") is { } pfco) return new ProtectionFromColorsOf(pfco, Subj("what", "self"));
        if (e.TryGetProperty("exileHandDownTo", out _)) return new ExileHandDownTo(Int("exileHandDownTo"), Subj("who", "you"));
        if (e.TryGetProperty("returnFromGraveyard", out var rfg))
            return new ReturnFromGraveyard(ParseFilter(rfg, ControllerFilter.Any), e.TryGetProperty("count", out var rc) ? rc.GetInt32() : 1,
                Str("to") == "battlefield" ? Engine.State.Zone.Battlefield : Engine.State.Zone.Hand, Flag("upTo"))
            {
                ExcludeSacrificed = Flag("excludeSacrificed"),
                DifferentManaValues = Flag("differentManaValues"),
                AnyGraveyard = Flag("anyGraveyard"),
            };
        if (Value("discardHand") is { } dh) return new DiscardHand(dh);
        if (Value("changeTarget") is { } ctg) return new ChangeTarget(ctg) { ToSource = Str("to") == "self" };
        if (Value("destroySameName") is { } dsn) return new DestroySameName(dsn);
        if (e.TryGetProperty("distributeCounters", out _)) return new DistributeCounters(Int("distributeCounters"));
        if (e.TryGetProperty("revealUntil", out var ru))
            return new RevealUntil(ParseFilter(ru, ControllerFilter.Any), Str("to") == "battlefield" ? Engine.State.Zone.Battlefield : Engine.State.Zone.Hand)
            {
                BattlefieldIf = e.TryGetProperty("battlefieldIf", out var bif) ? ParseFilter(bif, ControllerFilter.Any) : null,
                CountFrom = e.TryGetProperty("count", out var ruc) ? ParseQuantity(ruc) : null,
                Tapped = Flag("tapped"),
                RestToGraveyard = Str("rest") == "graveyard",
                RestShuffled = Str("rest") == "shuffle",
                RevealerPuts = Flag("revealerPuts"),
                From = e.TryGetProperty("from", out var ruf) ? ParseSubject(ruf) : null,
                CastFree = Flag("castFree"),
                AttachTo = e.TryGetProperty("attachTo", out var ruat) ? ParseSubject(ruat) : null,
            };
        if (Value("unless") is { } unlessWho)
            return new Unless(unlessWho, e.GetProperty("options").EnumerateArray().Select(ParseExtraCost).ToList(), EffectList(e.GetProperty("otherwise")));
        if (e.TryGetProperty("opponentMaySacrifice", out var oms)) return new OpponentMaySacrifice(ParseFilter(oms, ControllerFilter.Any), Effects(e));
        if (e.TryGetProperty("piles", out _)) return new Piles(Int("piles")) { OpponentSeparates = Flag("opponentSeparates"), Revealed = Flag("revealed") };
        if (Flag("winGame")) return new WinGame();
        if (e.TryGetProperty("untapUpTo", out _))
            return new UntapUpTo(Int("untapUpTo"), e.TryGetProperty("filter", out var uf) ? ParseFilter(uf) : new ObjectFilter(CardType.Land));
        if (e.TryGetProperty("poison", out _)) return new AddPoison(Int("poison"), Subj("who", "triggeredPlayer"));
        if (Flag("endTurn")) return new EndTheTurn();
        if (Flag("extraTurn")) return new ExtraTurn(Subj("who", "you"));
        if (Flag("additionalCombat")) return new AdditionalCombat(Flag("untapCreatures"));
        if (Value("copySpell") is { } copySpell)
            return new CopySpell(copySpell, e.TryGetProperty("count", out var csc) ? ParseQuantity(csc) : 1) { NotLegendary = Flag("notLegendary"), EachOtherPlayer = Flag("eachOtherPlayer"), CounteredThisWay = Flag("counteredThisWay") };
        if (e.TryGetProperty("addManaAnyColor", out _)) return new AddManaOfAnyColor(Int("addManaAnyColor"));
        if (Flag("returnExiledWithThis")) return new ReturnExiledWithThis { Count = e.TryGetProperty("count", out var rewc) ? rewc.GetInt32() : 0 };
        if (e.TryGetProperty("searchExileWithThis", out var sew))
            return new SearchAndExileWithThis(ParseFilter(sew, ControllerFilter.Any)) { Count = e.TryGetProperty("count", out var sewc) ? sewc.GetInt32() : 1 };
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
                SetColors = e.TryGetProperty("setColors", out var bsc) ? bsc.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
                PowerFrom = e.TryGetProperty("powerFrom", out var bpf) ? ParseQuantity(bpf) : null,
                ToughnessFrom = e.TryGetProperty("toughnessFrom", out var btf) ? ParseQuantity(btf) : null,
                Continuous = Flag("continuous"),
                WhileSourceRemains = Flag("whileSource"),
            };
        if (Str("mayPayX") is { } payX) return new MayPayX(payX, Effects(e)) { Max = e.TryGetProperty("max", out var mpxm) ? ParseQuantity(mpxm) : null };
        if (Flag("noMaxHandSize")) return new NoMaximumHandSizeForever();
        if (Flag("ignoreHexproof")) return new IgnoreHexproofThisTurn();
        if (e.TryGetProperty("mayCastFromGraveyard", out var mcg)) return new MayCastFromGraveyardThisTurn(ParseFilter(mcg, ControllerFilter.Any));
        if (Str("counterTriggeringUnlessPays") is { } ctup) return new CounterTriggeringUnlessPays(ManaCost.Parse(ctup));
        if (Flag("copyNextInstantOrSorcery")) return new CopyNextInstantOrSorcery();
        if (Flag("castFromLibraryTopsFree")) return new CastFromEachLibraryTopFree();
        if (Str("returnNextEndStepOneFewer") is { } rnk) return new ReturnAtNextEndStepWithOneFewer(ParseCounterKind(rnk));
        if (Flag("destroyManaValueXDamaged")) return new DestroyManaValueXOfDamagedPlayers();
        if (Str("addManaUntilEndOfTurn") is { } amu) return new AddManaUntilEndOfTurn(ManaCost.Parse(amu).Pips);
        if (Str("emblem") is { } emblemName) return new CreateEmblem(emblemName, Parse(e.GetRawText()).Abilities) { UntilEndOfTurn = Flag("untilEndOfTurn") };
        if (e.TryGetProperty("exileTopPlayable", out var etp))
            return new ExileTopPlayable(etp.ValueKind == JsonValueKind.Number ? etp.GetInt32() : 0, !e.TryGetProperty("chooseOne", out var co) || co.GetBoolean(), Flag("untilNextTurn"), Flag("free"))
            {
                CountFrom = etp.ValueKind == JsonValueKind.Number ? null : ParseQuantity(etp),
                From = e.TryGetProperty("of", out var of) ? ParseSubject(of) : null,
                PayLife = Flag("payLife"),
                AnyManaType = Flag("anyMana"),
                Forever = Flag("forever"),
                FaceDown = Flag("faceDown"),
                CastOnly = Flag("castOnly"),
                While = e.TryGetProperty("while", out var etw) ? ParseCondition(etw) : null,
                WhenPlayed = e.TryGetProperty("whenPlayed", out var whp)
                    ? new TriggeredAbility { Trigger = TriggerEvent.Reflexive, Targets = Targets(whp), Effects = Effects(whp), Text = Text(whp) }
                    : null,
            };
        if (e.TryGetProperty("divideDamage", out _)) return new DealDamageDivided(Int("divideDamage"));
        if (Value("keepOneOfEachType") is { } keep) return new KeepOneOfEachType(keep);
        if (e.TryGetProperty("millUntil", out var mu)) return new MillUntil(Subj("who", "you"), ParseFilter(mu, ControllerFilter.Any));
        if (Value("exileUntilLeaves") is { } eul) return new ExileUntilSourceLeaves(eul) { Castable = Flag("castable") };
        if (Value("flicker") is { } flick) return new ExileAndReturnAtEndStep(flick, Flag("yours"));
        if (Value("copy") is { } copyOf)
            return new CreateTokenCopy(copyOf, e.TryGetProperty("count", out var cc) ? ParseQuantity(cc) : 1, Flag("haste"), Flag("sacrificeAtEndStep"))
            {
                AddSubtypes = e.TryGetProperty("addSubtypes", out var cas) ? cas.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
                NotLegendary = Flag("notLegendary"),
                SetPower = e.TryGetProperty("setPower", out var csp) ? csp.GetInt32() : null,
                SetToughness = e.TryGetProperty("setToughness", out var cst) ? cst.GetInt32() : null,
                SetColors = e.TryGetProperty("setColors", out var csc2) ? csc2.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
                SetTypes = e.TryGetProperty("setTypes", out var csty) ? ParseTypes(csty) : null,
                SetSubtypes = e.TryGetProperty("setSubtypes", out var cssub) ? cssub.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
                AddKeywords = e.TryGetProperty("addKeywords", out var cak) ? cak.EnumerateArray().Select(x => x.GetString()!).ToList() : null,
                AddAbilities = e.TryGetProperty("abilities", out var cab) ? Parse("{\"abilities\":" + cab.GetRawText() + "}").Abilities : null,
                Tapped = Flag("tapped"),
                Attacking = Flag("attacking"),
                AtNextEndStep = e.TryGetProperty("atNextEndStep", out var cane) ? EffectList(cane) : null,
                AtNextEndStepUnless = e.TryGetProperty("atNextEndStepUnless", out var canu) ? ParseCondition(canu) : null,
                ExileAtEndOfCombat = Flag("exileAtEndOfCombat"),
            };
        if (Value("sacrificeIt") is { } sacIt) return new SacrificeIt(sacIt);
        if (Value("exileIfDies") is { } eid) return new ExileIfDiesThisTurn(eid);
        if (Value("preventCombatDamage") is { } pcd) return new PreventCombatDamageTo(pcd);
        if (e.TryGetProperty("lookAtTop", out var lat))
            return new LookAtTopTake(lat.ValueKind == JsonValueKind.Number ? lat.GetInt32() : 0, e.TryGetProperty("filter", out var lf) ? ParseFilter(lf, ControllerFilter.Any) : null,
                e.TryGetProperty("take", out var take) ? take.GetInt32() : 1,
                Str("to") switch { null or "hand" => Engine.State.Zone.Hand, "battlefield" => Engine.State.Zone.Battlefield, "top" => Engine.State.Zone.Library,
                    var unknown => throw new FormatException($"Unknown destination '{unknown}'.") },
                Str("rest") == "graveyard")
            {
                Tapped = Flag("tapped"),
                RestShuffled = Str("rest") == "shuffle",
                RestOnTop = Str("rest") == "top",
                RestOrder = Flag("restAnyOrder") ? RestOrder.Chosen : RestOrder.Random,
                CountIsX = Flag("countIsX"),
                MaxManaValueX = Flag("maxManaValueX"),
                Reveal = Flag("reveal"),
                RevealAll = Flag("revealAll"),
                Required = Flag("required"),
                CountFrom = lat.ValueKind == JsonValueKind.Number ? null : ParseQuantity(lat),
            };
        if (Value("discardChosen") is { } dchosen)
            return new DiscardChosenByYou(dchosen, e.TryGetProperty("filter", out var df) ? ParseFilter(df, ControllerFilter.Any) : null, e.TryGetProperty("count", out var dn) ? dn.GetInt32() : 1);
        if (Value("exileGraveyard") is { } eg)
            return new ExileGraveyard(eg) { Filter = e.TryGetProperty("filter", out var egf) ? ParseFilter(egf, ControllerFilter.Any) : null, Playable = Flag("playable") };
        if (e.TryGetProperty("whenYouDo", out var reflexive))
            return new ReflexiveTrigger(WithModes(reflexive, new TriggeredAbility
            {
                Trigger = TriggerEvent.Reflexive, Targets = Targets(reflexive), Effects = Effects(reflexive), Text = Text(reflexive),
            }), reflexive.TryGetProperty("if", out var rif) ? ParseCondition(rif) : null)
            {
                About = reflexive.TryGetProperty("about", out var rab) ? ParseSubject(rab) : null,
                Amount = reflexive.TryGetProperty("amount", out var ram) ? ParseQuantity(ram) : null,
            };
        if (Value("doubleCounters") is { } dbl)
            return new DoubleCounters(dbl, e.TryGetProperty("kind", out var dk) ? ParseCounterKind(dk.GetString()) : null);
        if (e.TryGetProperty("removeCounters", out _)) return new RemoveCounters(Qty("removeCounters"), Subj("what", "self"), ParseCounterKind(Str("kind")));
        if (Value("shuffleGraveyard") is { } sg) return new ShuffleGraveyardIntoLibrary(sg);
        if (Str("addMana") is { } addMana) return new AddMana(ManaCost.Parse(addMana).Pips) { Times = e.TryGetProperty("times", out var amt) ? ParseQuantity(amt) : null };
        if (Value("bite") is { } biter) return new DealsDamageEqualToPower(biter, Subj("to", "target2"));
        if (e.TryGetProperty("reanimateAll", out var raf))
            return new ReanimateAll(Subj("from", "you"), ParseFilter(raf, ControllerFilter.Any)) { As = ParseEntering(e, Subject.You) };
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
            var searchCount = e.TryGetProperty("count", out var c) ? c : default;
            return new SearchLibrary(ParseFilter(search, ControllerFilter.Any), searchCount.ValueKind == JsonValueKind.Number ? searchCount.GetInt32() : 1, to, Flag("tapped"))
            {
                CountFrom = searchCount.ValueKind is JsonValueKind.String or JsonValueKind.Object ? ParseQuantity(searchCount) : null,
                OneToBattlefieldRestToHand = Flag("split"),
                Who = e.TryGetProperty("who", out var sw) ? ParseSubject(sw) : null,
                Optional = Flag("optional"),
                Reveal = Flag("reveal"),
                MaxManaValueX = Flag("maxManaValueX"),
                ShareLandType = Flag("shareLandType"),
                WithExiled = Flag("withExiled"),
            };
        }
        if (e.TryGetProperty("sacrifice", out _))
            return new Sacrifice(Qty("sacrifice"), e.TryGetProperty("filter", out var sf) ? ParseFilter(sf, ControllerFilter.Any) : new ObjectFilter(CardType.Creature), Subj("who", "you"));
        if (e.TryGetProperty("pump", out var pump))
        {
            var pt = pump.EnumerateArray().Select(ParseQuantity).ToArray();
            return new PumpUntilEndOfTurn(pt[0], pt[1], Subj("what", "target"), ParseKeywords(e))
            {
                WhileSourceRemains = Flag("whileSource") || Flag("whileYouControl"),
                WhileYouControlSource = Flag("whileYouControl"),
                UntilYourNextTurn = Flag("untilYourNextTurn"),
                LoseKeywords = e.TryGetProperty("loseKeywords", out var lk) ? lk.EnumerateArray().Select(x => ParseKeyword(x.GetString()!)).ToList() : null,
                CantBeBlockedBy = e.TryGetProperty("cantBeBlockedBy", out var pcb) ? ParseFilter(pcb, ControllerFilter.Any) : null,
            };
        }
        if (e.TryGetProperty("counters", out _))
        {
            return new AddCounters(Qty("counters"), Subj("what", "target"), ParseCounterKind(Str("kind")));
        }
        if (Flag("recruit"))
            return new Recruit(new CardDefinition
            {
                Name = "Human Soldier", Types = CardType.Creature, Subtypes = new[] { "Human", "Soldier" }, Power = 1, Toughness = 1, Colors = new[] { "W" }, IsToken = true,
            }, Subj("who", "you"));
        if (e.TryGetProperty("takeMilled", out var tkm))
            return new TakeMilled(tkm.ValueKind == JsonValueKind.Object ? ParseFilter(tkm, ControllerFilter.Any) : null, e.TryGetProperty("count", out var tkc) ? tkc.GetInt32() : -1);
        if (Value("removeAllCounters") is { } rac) return new RemoveAllCounters(rac);
        if (e.TryGetProperty("exileUncastEntering", out var eue)) return new ExileUncastEntering(ParseFilter(eue, ControllerFilter.Any));
        if (e.TryGetProperty("revealTopPut", out var rtp)) return new RevealTopPutOntoBattlefield(Subj("who", "you"), ParseFilter(rtp, ControllerFilter.Any));
        if (e.TryGetProperty("exileLibraryAllBut", out var elb)) return new ExileLibraryAllButBottom(Subj("who", "target"), elb.GetInt32());
        if (Value("cantHaveCounters") is { } chc) return new PreventCounters(chc);
        if (Value("blink") is { } blink) return new Blink(blink) { Tapped = Flag("tapped"), Transformed = Flag("transformed") };
        if (Value("shuffleIntoLibrary") is { } sil) return new ShuffleIntoLibrary(sil);
        if (Flag("additionalLand")) return new AdditionalLandThisTurn();
        if (Flag("noSpellsThisTurn")) return new NoSpellsThisTurn();
        if (Flag("playTopFree")) return new PlayTopFreeOrExile();
        if (Flag("warpWorld")) return new WarpWorld();
        if (e.TryGetProperty("rebuildLibraryFromExile", out _)) return new RebuildLibraryFromExile(Int("rebuildLibraryFromExile"));
        if (Value("exileGraveyardAndNamesakes") is { } egn)
            return new ExileGraveyardAndNamesakes(egn, e.TryGetProperty("filter", out var egnf) ? ParseFilter(egnf, ControllerFilter.Any) : null);
        if (Value("searchThenNameCard") is { } stn)
            return new SearchThenNameCard(stn, e.TryGetProperty("filter", out var stnf) ? ParseFilter(stnf, ControllerFilter.Any) : ObjectFilter.Anything);
        if (Value("shuffle") is { } shuffleWho) return new ShuffleLibrary(shuffleWho);
        if (Flag("opponentsCantCastSpells")) return new OpponentsCantCastSpellsThisTurn();
        if (Value("exchangeControl") is { } exc) return new ExchangeControl(exc, Subj("with", "target2"));
        if (e.TryGetProperty("atNextUpkeep", out var anu))
            return new AtNextUpkeep(new TriggeredAbility { Trigger = TriggerEvent.NextUpkeep, Targets = Targets(anu), Effects = Effects(anu), Text = Text(anu) },
                e.TryGetProperty("amount", out var anua) ? ParseQuantity(anua) : null)
            {
                Yours = Flag("yours"),
                Player = e.TryGetProperty("player", out var anup) ? ParseSubject(anup) : null,
            };
        if (Flag("chooseType")) return new ChooseCreatureType();
        if (e.TryGetProperty("revealTopRandom", out _))
            return new RevealTopPutRandom(Int("revealTopRandom"), e.TryGetProperty("filter", out var rtf) ? ParseFilter(rtf, ControllerFilter.Any) : ObjectFilter.Anything,
                Str("to") == "battlefield" ? Engine.State.Zone.Battlefield : Engine.State.Zone.Hand);
        if (e.TryGetProperty("searchHandOrLibrary", out var shl))
            return new SearchHandOrLibrary(ParseFilter(shl, ControllerFilter.Any), Str("to") == "battlefield" ? Engine.State.Zone.Battlefield : Engine.State.Zone.Hand);
        if (e.TryGetProperty("addManaCombination", out _))
            return new AddManaInAnyCombination(Qty("addManaCombination"), e.TryGetProperty("onlyFor", out var amo) ? ParseFilter(amo, ControllerFilter.Any) : null);
        if (e.TryGetProperty("behold", out var bh)) return new Behold(ParseFilter(bh, ControllerFilter.You), Effects(e));
        if (e.TryGetProperty("castFromGraveyard", out var cfgy))
            return new CastFromGraveyardNow(ParseFilter(cfgy, ControllerFilter.Any))
            {
                Of = e.TryGetProperty("of", out var cgo) ? ParseSubject(cgo) : null,
                Free = Flag("free"),
                MaxManaValue = e.TryGetProperty("maxManaValue", out var cgm) ? ParseQuantity(cgm) : null,
                Card = e.TryGetProperty("card", out var cgc) ? ParseSubject(cgc) : null,
                FromMilled = Flag("fromMilled"),
            };
        if (Value("preventDamageBy") is { } pdb) return new PreventDamageBy(pdb);
        if (Value("phaseOut") is { } po) return new PhaseOut(po);
        if (Flag("returnLinkedExiled")) return new ReturnLinkedExiled();
        if (e.TryGetProperty("cantBlockThisTurn", out var cbt)) return new CantBlockThisTurn(ParseFilter(cbt, ControllerFilter.Any));
        if (Value("unblockableByMostLifePlayer") is { } ubm) return new UnblockableByMostLifePlayer(ubm);
        if (Flag("ringTempts")) return new RingTemptsYou();
        if (Value("becomeMonarch") is { } monarch) return new BecomeMonarch(monarch);
        if (Value("exileUntilOpponentMonarch") is { } eom) return new ExileUntilOpponentIsMonarch(eom);
        if (e.TryGetProperty("playerMayPay", out _)) return new PlayerMayPay(Subj("who", "triggeredPlayer"), Qty("playerMayPay"), EffectList(e.GetProperty("ifNot")));
        if (Value("cantAttackYouThisCombat") is { } cay) return new CantAttackYouThisCombat(cay);
        if (e.TryGetProperty("preventDamage", out var pdm))
            return new PreventDamageThisTurn(Flag("combatOnly"), e.TryGetProperty("dealtBy", out var pdb2) ? ParseSubject(pdb2) : null,
                e.TryGetProperty("sources", out var pds) ? ParseFilter(pds, ControllerFilter.Any) : null, Flag("toYou") || (pdm.ValueKind == JsonValueKind.String && pdm.GetString() == "toYou"))
            {
                ToYourCreatures = Flag("toYourCreatures"),
            };
        if (Flag("tripleDamage")) return new TripleDamageThisTurn();
        if (e.TryGetProperty("divideEvenly", out _)) return new DealDamageDividedEvenly(Qty("divideEvenly"));
        if (Value("destroyRandom") is { } destroyRandom) return new DestroyOneAtRandom(destroyRandom);
        if (e.TryGetProperty("redirectDamage", out _)) return new RedirectNextDamage(Qty("redirectDamage"), Subj("to", "target"));
        if (e.TryGetProperty("vote", out var vote))
            return new Vote(vote.ValueKind == JsonValueKind.Array ? vote.EnumerateArray().Select(x => x.GetString()!).ToList() : Array.Empty<string>(), Flag("secret"),
                vote.ValueKind == JsonValueKind.String ? Enum.Parse<VoteFor>(vote.GetString()!, ignoreCase: true) : VoteFor.Options,
                e.TryGetProperty("filter", out var vf) ? ParseFilter(vf, ControllerFilter.Any) : null);
        if (Flag("stunVoted")) return new StunVotedCreatures();
        if (Value("cantAttackPlayerThisTurn") is { } capt) return new CantAttackPlayerThisTurn(capt);
        if (Value("cantSacrificeThisTurn") is { } cstt) return new CantSacrificeThisTurn(cstt);
        if (Value("protectionFromOpponents") is { } pfo) return new ProtectionFromOpponents(pfo);
        if (Value("exileTopFaceDown") is { } etfd) return new ExileTopFaceDown(etfd);
        if (Flag("playOneExiledFree")) return new PlayOneExiledWithThisFree();
        if (Flag("takeCountersOfTriggered")) return new TakeCountersOfTriggered();
        if (e.TryGetProperty("moveAllCounters", out var mac)) return new MoveAllCounters(ParseSubject(mac.GetProperty("from")), ParseSubject(mac.GetProperty("to")));
        if (Flag("mayBounceSharingType")) return new MayBounceAnotherSharingType();
        if (Flag("copyTriggeredAbility")) return new CopyTriggeredAbility();
        if (e.TryGetProperty("swapGraveyardAndBattlefield", out var sgb)) return new SwapGraveyardAndBattlefield(ParseFilter(sgb, ControllerFilter.Any));
        if (e.TryGetProperty("temptingOfferSearch", out var tos)) return new TemptingOfferSearch(ParseFilter(tos, ControllerFilter.Any));
        if (e.TryGetProperty("copyEachYouControl", out var cey)) return new CopyEachYouControl(ParseFilter(cey, ControllerFilter.You));
        if (e.TryGetProperty("opponentsExileGreatestPower", out var oeg))
            return new OpponentsExileGreatestPower(e.TryGetProperty("damageIf", out var oegd) ? ParseCondition(oegd) : null);
        if (Flag("chooseOpponent")) return new ChooseAnOpponent();
        if (Value("destroyPowerAbove") is { } dpa) return new DestroyPowerAbove(dpa);
        if (e.TryGetProperty("exileFromGraveyardChosen", out var efgc)) return new ExileChosenFromGraveyard(ParseFilter(efgc, ControllerFilter.Any));
        if (e.TryGetProperty("returnFromGraveyardAll", out var rfga)) return new ReturnAllFromGraveyardToHand(ParseFilter(rfga, ControllerFilter.Any));
        if (Flag("copyIfOpponentsTopSharesType")) return new CopyIfOpponentsTopSharesType();
        if (e.TryGetProperty("suspendWhenResolves", out _)) return new SuspendWhenResolves(Int("suspendWhenResolves"));
        if (e.TryGetProperty("choose", out var chf) && chf.ValueKind == JsonValueKind.Object)
            return new ChooseObjects(Subj("chooser", "you"), ParseFilter(chf, ControllerFilter.Any), Flag("optional"));
        if (Value("bounceSameManaValue") is { } bsm) return new BounceSameManaValue(bsm);
        if (Value("attacksYouThisTurn") is { } ayt) return new AttacksYouThisTurn(ayt);
        if (Value("skipNextUntap") is { } snu) return new SkipNextUntap(snu, Value("player"));
        if (e.TryGetProperty("tapAllToDamage", out var tatd)) return new TapAllToDamage(ParseFilter(tatd, ControllerFilter.You), Subj("to", "target"));
        if (e.TryGetProperty("atNextEndStepAbout", out var anea)) return new AtNextEndStepAbout(ParseSubject(anea), EffectList(e.GetProperty("effects")));
        if (e.TryGetProperty("drawUpTo", out _)) return new DrawUpTo(Int("drawUpTo"), Subj("who", "you"));
        if (e.TryGetProperty("hideaway", out _)) return new Hideaway(Int("hideaway"));
        if (Flag("playHiddenFree")) return new PlayLinkedExiledFree();
        if (Value("becomeRenowned") is { } renowned) return new BecomeRenowned(renowned);
        if (Value("transform") is { } transform) return new Transform(transform);
        if (Value("regenerate") is { } regen) return new Regenerate(regen);
        if (Flag("populate")) return new Populate();
        if (e.TryGetProperty("repeat", out _)) return new Repeat(Qty("repeat"), Effects(e));
        if (Flag("exileThisSpell")) return new ExileThisSpell();
        if (Flag("thisSpellToLibraryBottom")) return new ThisSpellToLibraryBottom();
        if (e.TryGetProperty("votersGiveCreatures", out _)) return new VotersGiveCreatures(Int("votersGiveCreatures"));
        if (e.TryGetProperty("eachPlayer", out var epl))
            return new ForEachPlayer(e.TryGetProperty("who", out var epw) ? ParseSubject(epw) : new Subject(SubjectKind.EachPlayer),
                e.TryGetProperty("onlyIf", out var epi) ? ParseCondition(epi) : null, EffectList(epl));
        if (Flag("cantLoseThisTurn")) return new CantLoseThisTurn();
        if (e.TryGetProperty("ownersGainControl", out var ogc)) return new OwnersGainControl(ParseFilter(ogc, ControllerFilter.Any));
        if (Flag("playerProtection")) return new PlayerProtection();
        if (e.TryGetProperty("castFromHandFree", out var cfh)) return new CastFromHandFree(ParseFilter(cfh, ControllerFilter.Any), Qty("maxManaValue"));
        if (e.TryGetProperty("putFromHand", out var pfh)) return new PutFromHand(ParseFilter(pfh, ControllerFilter.Any)) { One = Flag("one"), Tapped = Flag("tapped"), Attacking = Flag("attacking") };
        if (e.TryGetProperty("destroyAllBut", out var dab)) return new DestroyAllButChosen(ParseFilter(dab, ControllerFilter.Any), Int("keep"));
        if (e.TryGetProperty("handToBottom", out _)) return new HandToBottom(Int("handToBottom"));
        if (e.TryGetProperty("tapAnyNumber", out var tan)) return new TapAnyNumber(ParseFilter(tan, ControllerFilter.You));
        if (Str("opponentChooses") is { } ocPrompt) return new OpponentChooses(ocPrompt, EffectList(e.GetProperty("yes")), EffectList(e.GetProperty("no")));
        if (Value("castCopy") is { } castCopy) return new CastCopyOfExiled(castCopy);
        if (e.TryGetProperty("amass", out _))
        {
            var type = Str("type") ?? "Orc";
            var army = new CardDefinition
            {
                Name = $"{type} Army", Types = CardType.Creature, Subtypes = new[] { type, "Army" }, Power = 0, Toughness = 0, Colors = new[] { "B" },
                IsToken = true,
            };
            return new Amass(Qty("amass"), type, army, Subj("who", "you"));
        }
        if (e.TryGetProperty("tokens", out _))
            return new CreateTokens(ParseToken(e.GetProperty("token")), Qty("tokens"), Subj("for", "you"), Flag("tapped"))
            {
                HasteUntilEndOfTurn = Flag("hasteUntilEndOfTurn"), Attacking = Flag("attacking"), SacrificeAtEndOfCombat = Flag("sacrificeAtEndOfCombat"),
                PowerAndToughness = e.TryGetProperty("size", out var tsz) ? ParseQuantity(tsz) : null,
            };
        throw new FormatException($"Unknown effect {e.GetRawText()}.");
    }

    /// <summary>A card put onto the battlefield: tapped, under its owner's control, with counters, attached, and what it becomes.</summary>
    private static PutOntoBattlefield ParseEntering(JsonElement e, Subject what)
    {
        bool Flag(string name) => e.TryGetProperty(name, out var v) && v.GetBoolean();
        List<string>? Strings(string name) => e.TryGetProperty(name, out var v) ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : null;
        return new PutOntoBattlefield(what, Flag("tapped"), Flag("ownerControl"))
        {
            Attacking = Flag("attacking"),
            Counters = e.TryGetProperty("counters", out var rcn) ? rcn.GetInt32() : 0,
            CounterKind = ParseCounterKind(e.TryGetProperty("counterKind", out var rck) ? rck.GetString() : null),
            CounterKinds = e.TryGetProperty("counterKinds", out var rcks) ? rcks.EnumerateArray().Select(k => ParseCounterKind(k.GetString())).ToList() : null,
            AddSubtypes = Strings("addSubtypes"),
            AddColors = Strings("addColors"),
            AddKeywords = ParseKeywords(e),
            AttachTo = e.TryGetProperty("attachTo", out var at) ? ParseSubject(at) : null,
            Transformed = Flag("transformed"),
            ExileIfLeaves = Flag("exileIfLeaves"),
            SetTypes = e.TryGetProperty("setTypes", out var st) ? ParseTypes(st) : null,
            SetSubtypes = Strings("setSubtypes"),
            AddAbilities = e.TryGetProperty("abilities", out var ab) ? Parse("{\"abilities\":" + ab.GetRawText() + "}").Abilities : null,
        };
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
        _ => Enum.Parse<CounterKind>(kind.Replace(" ", "").Replace("-", ""), ignoreCase: true), // "first strike"
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
            // "This token's power and toughness are each equal to …": a characteristic-defining ability (rule 604.3).
            PowerFrom = t.TryGetProperty("powerFrom", out var tpf) ? ParseQuantity(tpf) : null,
            ToughnessFrom = t.TryGetProperty("toughnessFrom", out var ttf) ? ParseQuantity(ttf) : null,
        };
    }
}
