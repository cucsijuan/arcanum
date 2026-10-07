// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Abilities;
using Arcanum.Engine.Cards;
using Arcanum.Engine.Mana;

namespace Arcanum.Data.Tests;

/// <summary>Importing nonmodal double-faced cards (layout "transform") and their scripts' "back" part.</summary>
public class TransformImportTests
{
    // Invented cards in the card source's JSON Lines shape. The back face has a color indicator (its colors) and no mana cost.
    private const string Fixture = """
        {"oracle_id":"t-1","name":"Moonlit Pup // Howling Brute","layout":"transform","mana_cost":"","type_line":"Creature — Wolf // Creature — Wolf Horror","oracle_text":"","colors":["G"],"color_identity":["G","R"],"keywords":["Transform","Trample"],"games":["paper"],"card_faces":[{"name":"Moonlit Pup","mana_cost":"{1}{G}","type_line":"Creature — Wolf","oracle_text":"{1}: Transform Moonlit Pup.","power":"2","toughness":"2","colors":["G"]},{"name":"Howling Brute","mana_cost":"","type_line":"Creature — Wolf Horror","oracle_text":"Trample\nWhen this creature transforms into Howling Brute, you gain 3 life.","power":"4","toughness":"4","colors":["R"]}]}
        {"oracle_id":"t-2","name":"Lantern Keeper // Lantern Sage","layout":"transform","mana_cost":"","type_line":"Creature — Human // Legendary Planeswalker — Sage","oracle_text":"","colors":["U"],"keywords":["Transform"],"games":["paper"],"card_faces":[{"name":"Lantern Keeper","mana_cost":"{2}{U}","type_line":"Creature — Human","oracle_text":"{T}: Transform this creature.","power":"1","toughness":"3","colors":["U"]},{"name":"Lantern Sage","mana_cost":"","type_line":"Legendary Planeswalker — Sage","oracle_text":"+1: Draw a card.","loyalty":"3","colors":["U"]}]}
        {"oracle_id":"t-3","name":"Dawn Hound // Dusk Hound","layout":"transform","mana_cost":"","type_line":"Creature — Dog // Creature — Dog","oracle_text":"","colors":["W"],"keywords":["Daybound","Nightbound"],"games":["paper"],"card_faces":[{"name":"Dawn Hound","mana_cost":"{W}","type_line":"Creature — Dog","oracle_text":"Daybound","power":"1","toughness":"1","colors":["W"]},{"name":"Dusk Hound","mana_cost":"","type_line":"Creature — Dog","oracle_text":"Nightbound","power":"2","toughness":"2","colors":["W"]}]}
        """;

    private const string PupScript = """
        {
          "abilities": [{ "cost": "{1}", "effects": [{ "transform": "self" }], "text": "{1}: Transform Moonlit Pup." }],
          "back": {
            "abilities": [{ "trigger": "transforms", "onSelf": true, "effects": [{ "gainLife": 3 }], "text": "When this creature transforms into Howling Brute, you gain 3 life." }]
          }
        }
        """;

    private static Dictionary<string, CardRecord> Records() =>
        OracleJsonl.Import(new MemoryStream(Encoding.UTF8.GetBytes(Fixture))).ToDictionary(r => r.Name);

    [Fact]
    public void ImportingATransformingCardGivesBothFaces()
    {
        var record = Records()["Moonlit Pup // Howling Brute"];
        Assert.Equal(2, record.Faces.Count);
        Assert.Equal(new[] { "R" }, record.Faces[1].Colors);

        var (pup, _) = CardFactory.Create(record, CardScriptParser.Parse(PupScript));
        Assert.Equal("Moonlit Pup", pup.Name);
        Assert.Equal(ManaCost.Parse("{1}{G}"), pup.ManaCost);
        Assert.Equal(2, pup.Power);
        Assert.Equal(new[] { "G" }, pup.ColorList);
        Assert.Equal("t-1", pup.OracleId);
        var back = Assert.IsType<CardDefinition>(pup.BackFace);
        Assert.Equal("Howling Brute", back.Name);
        Assert.Equal(ManaCost.Zero, back.ManaCost);
        Assert.Equal(4, back.Power);
        Assert.Equal(4, back.Toughness);
        Assert.Equal(new[] { "R" }, back.ColorList); // its color indicator
        Assert.Contains(Keyword.Trample, back.KeywordAbilities);
        Assert.DoesNotContain(Keyword.Trample, pup.KeywordAbilities); // each face keeps the keywords its own text has
        Assert.Contains("transforms into Howling Brute", back.OracleText);
        Assert.Equal(new[] { "G", "R" }, pup.ColorIdentity);

        var (keeper, _) = CardFactory.Create(Records()["Lantern Keeper // Lantern Sage"]);
        Assert.Equal(3, keeper.BackFace!.Loyalty);
        Assert.True(keeper.BackFace.Is(CardType.Planeswalker));
        Assert.Equal(Supertype.Legendary, keeper.BackFace.Supertypes);
    }

    [Fact]
    public void TheBackPartOfAScriptGivesTheBackFaceItsAbilities()
    {
        var script = CardScriptParser.Parse(PupScript);
        var transform = Assert.IsType<Transform>(Assert.IsType<ActivatedAbility>(Assert.Single(script.Abilities)).Effects[0]);
        Assert.Equal(SubjectKind.Self, transform.What.Kind);
        var back = Assert.IsType<CardScript>(script.Back);
        var trigger = Assert.IsType<TriggeredAbility>(Assert.Single(back.Abilities));
        Assert.Equal(TriggerEvent.Transforms, trigger.Trigger);
        Assert.True(trigger.OnSelf);

        var (pup, _) = CardFactory.Create(Records()["Moonlit Pup // Howling Brute"], script);
        Assert.Equal(TriggerEvent.Transforms, Assert.IsType<TriggeredAbility>(Assert.Single(pup.BackFace!.Abilities)).Trigger);
        Assert.IsType<ActivatedAbility>(Assert.Single(pup.Abilities));
    }

    [Fact]
    public void TransformVocabularyParses()
    {
        var script = CardScriptParser.Parse("""
            {
              "abilities": [
                { "trigger": "dies", "effects": [{ "reanimate": "self", "transformed": true }] },
                { "trigger": "transforms", "filter": { "types": ["creature"] }, "if": "transformed", "effects": [{ "draw": 1 }] },
                { "cost": "{2}", "activateIf": "frontFaceUp", "targets": [{ "kind": "creature", "filter": { "transformed": true } }], "effects": [{ "transform": "target" }] }
              ]
            }
            """);
        var put = Assert.IsType<PutOntoBattlefield>(Assert.IsType<TriggeredAbility>(script.Abilities[0]).Effects[0]);
        Assert.True(put.Transformed);
        var watcher = Assert.IsType<TriggeredAbility>(script.Abilities[1]);
        Assert.Equal(TriggerEvent.Transforms, watcher.Trigger);
        Assert.IsType<SourceTransformed>(watcher.Condition);
        var activated = Assert.IsType<ActivatedAbility>(script.Abilities[2]);
        Assert.IsType<SourceFrontFaceUp>(activated.ActivationCondition);
        Assert.True(activated.Targets[0].Filter!.Transformed);
        Assert.Equal(SubjectKind.Target, Assert.IsType<Transform>(activated.Effects[0]).What.Kind);
    }

    [Fact]
    public void ATransformingCardIsSupportedOnlyWhenBothFacesAre()
    {
        var records = Records();
        var pup = records["Moonlit Pup // Howling Brute"];
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(pup).Support); // no script at all
        // A script for the front face only: the back face's triggered ability isn't covered.
        var frontOnly = CardScriptParser.Parse("""{ "abilities": [{ "cost": "{1}", "effects": [{ "transform": "self" }] }] }""");
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(pup, frontOnly).Support);
        Assert.Equal(CardSupport.Full, CardFactory.Create(pup, CardScriptParser.Parse(PupScript)).Support);
        // Day and night (daybound / nightbound) cards are transforming cards the engine doesn't run.
        var hound = CardScriptParser.Parse("""{ "back": { } }""");
        Assert.Equal(CardSupport.Unsupported, CardFactory.Create(records["Dawn Hound // Dusk Hound"], hound).Support);
    }

    [Fact]
    public void TheDatabaseFindsATransformingCardByItsFrontFaceAndOffersBothFaceNames()
    {
        var db = new CardDatabase(Records().Values, new Dictionary<string, CardScript> { ["t-1"] = CardScriptParser.Parse(PupScript) });
        Assert.True(db.TryGet("Moonlit Pup", out var pup));
        Assert.Equal("Howling Brute", pup.BackFace?.Name);
        Assert.Equal(CardSupport.Full, db.SupportOf("Moonlit Pup"));
        Assert.Contains("Moonlit Pup", db.Names);
        Assert.Contains("Howling Brute", db.Names);
        Assert.DoesNotContain("Moonlit Pup // Howling Brute", db.Names); // either face's name, not both (rule 712.19)
    }

    [Fact]
    public void CompactFormatKeepsTheFacesColorsAndLoyalty()
    {
        var ms = new MemoryStream();
        OracleJsonl.WriteCompact(Records().Values, ms);
        ms.Position = 0;
        var back = OracleJsonl.ReadCompact(ms).Single(r => r.Name == "Lantern Keeper // Lantern Sage").Faces[1];
        Assert.Equal("3", back.Loyalty);
        Assert.Equal(new[] { "U" }, back.Colors);
    }
}
