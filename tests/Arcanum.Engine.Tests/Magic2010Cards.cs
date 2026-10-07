// SPDX-License-Identifier: AGPL-3.0-or-later
// Generated from the Magic 2010 card scripts of the classic content module: the same rules text and scripts, so these tests
// check the scripts as players get them.
using Arcanum.Data.CardData;
using Arcanum.Data.Scripts;
using Arcanum.Engine.Cards;

namespace Arcanum.Engine.Tests;

public static class Magic2010Cards
{
    private static CardDefinition Make(CardRecord record, string script)
    {
        var (definition, support) = CardFactory.Create(record, CardScriptParser.Parse(script));
        if (support != CardSupport.Full) throw new InvalidOperationException($"{record.Name} isn't supported.");
        return definition;
    }

    private static readonly Dictionary<string, CardDefinition> Cards = new()
    {
        ["Clone"] = Make(new CardRecord
        {
            OracleId = "42226b87-0746-4ebf-9fd0-108d508462af", Name = "Clone", Layout = "normal", ManaCost = "{3}{U}", TypeLine = "Creature — Shapeshifter",
            OracleText = "You may have this creature enter as a copy of any creature on the battlefield.", Power = "0", Toughness = "0", Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Clone\",\n  \"entersAsCopy\": {\n    \"types\": [\n      \"creature\"\n    ]\n  }\n}"),
        ["Hive Mind"] = Make(new CardRecord
        {
            OracleId = "f97e405d-4c24-4d4d-9e19-e349c073113c", Name = "Hive Mind", Layout = "normal", ManaCost = "{5}{U}", TypeLine = "Enchantment",
            OracleText = "Whenever a player casts an instant or sorcery spell, each other player copies that spell. Each of those players may choose new targets for their copy.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Hive Mind\",\n  \"abilities\": [\n    {\n      \"trigger\": \"anyPlayerCastsSpell\",\n      \"filter\": {\n        \"types\": [\n          \"instant\",\n          \"sorcery\"\n        ],\n        \"controller\": \"any\"\n      },\n      \"effects\": [\n        {\n          \"copySpell\": \"triggered\",\n          \"eachOtherPlayer\": true\n        }\n      ],\n      \"text\": \"Whenever a player casts an instant or sorcery spell, each other player copies that spell. Each of those players may choose new targets for their copy.\"\n    }\n  ]\n}"),
        ["Warp World"] = Make(new CardRecord
        {
            OracleId = "50a228a2-c8b6-4416-b0ab-417926a9b9b6", Name = "Warp World", Layout = "normal", ManaCost = "{5}{R}{R}{R}", TypeLine = "Sorcery",
            OracleText = "Each player shuffles all permanents they own into their library, then reveals that many cards from the top of their library. Each player puts all artifact, creature, and land cards revealed this way onto the battlefield, then does the same for enchantment cards, then puts all cards revealed this way that weren't put onto the battlefield on the bottom of their library.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Warp World\",\n  \"spell\": {\n    \"effects\": [\n      {\n        \"warpWorld\": true\n      }\n    ],\n    \"text\": \"Each player shuffles all permanents they own into their library, then reveals that many cards from the top of their library. Each player puts all artifact, creature, and land cards revealed this way onto the battlefield, then does the same for enchantment cards, then puts all cards revealed this way that weren't put onto the battlefield on the bottom of their library.\"\n  }\n}"),
        ["Mirror of Fate"] = Make(new CardRecord
        {
            OracleId = "38ffe614-e8f7-4625-b921-9409e7e7ee5b", Name = "Mirror of Fate", Layout = "normal", ManaCost = "{5}", TypeLine = "Artifact",
            OracleText = "{T}, Sacrifice this artifact: Choose up to seven face-up exiled cards you own. Exile all the cards from your library, then put the chosen cards on top of your library.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Mirror of Fate\",\n  \"abilities\": [\n    {\n      \"cost\": \"{T}, sacrifice\",\n      \"effects\": [\n        {\n          \"rebuildLibraryFromExile\": 7\n        }\n      ],\n      \"text\": \"{T}, Sacrifice this artifact: Choose up to seven face-up exiled cards you own. Exile all the cards from your library, then put the chosen cards on top of your library.\"\n    }\n  ]\n}"),
        ["Haunting Echoes"] = Make(new CardRecord
        {
            OracleId = "3abd345a-69a5-486e-ada9-dd1ca0decc28", Name = "Haunting Echoes", Layout = "normal", ManaCost = "{3}{B}{B}", TypeLine = "Sorcery",
            OracleText = "Exile all cards from target player's graveyard other than basic land cards. For each card exiled this way, search that player's library for all cards with the same name as that card and exile them. Then that player shuffles.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Haunting Echoes\",\n  \"spell\": {\n    \"targets\": [\n      \"player\"\n    ],\n    \"effects\": [\n      {\n        \"exileGraveyardAndNamesakes\": \"target\",\n        \"filter\": {\n          \"anyOf\": [\n            {\n              \"not\": [\n                \"land\"\n              ]\n            },\n            {\n              \"notSupertype\": \"basic\"\n            }\n          ]\n        }\n      }\n    ],\n    \"text\": \"Exile all cards from target player's graveyard other than basic land cards. For each card exiled this way, search that player's library for all cards with the same name as that card and exile them. Then that player shuffles.\"\n  }\n}"),
        ["Sphinx Ambassador"] = Make(new CardRecord
        {
            OracleId = "69e1a166-02f7-43f8-b099-dbb01034370c", Name = "Sphinx Ambassador", Layout = "normal", ManaCost = "{5}{U}{U}", TypeLine = "Creature — Sphinx",
            OracleText = "Flying\nWhenever this creature deals combat damage to a player, search that player's library for a card, then that player chooses a card name. If you searched for a creature card that doesn't have that name, you may put it onto the battlefield under your control. Then that player shuffles.", Power = "5", Toughness = "5", Loyalty = null,
            Keywords = new string[] { "Flying" }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Sphinx Ambassador\",\n  \"abilities\": [\n    {\n      \"trigger\": \"combatDamageToPlayer\",\n      \"effects\": [\n        {\n          \"searchThenNameCard\": \"triggeredPlayer\",\n          \"filter\": {\n            \"types\": [\n              \"creature\"\n            ]\n          }\n        }\n      ],\n      \"text\": \"Whenever this creature deals combat damage to a player, search that player's library for a card, then that player chooses a card name. If you searched for a creature card that doesn't have that name, you may put it onto the battlefield under your control. Then that player shuffles.\"\n    }\n  ]\n}"),
        ["Djinn of Wishes"] = Make(new CardRecord
        {
            OracleId = "a34c3012-f787-4998-93c7-89c4decaa1ba", Name = "Djinn of Wishes", Layout = "normal", ManaCost = "{3}{U}{U}", TypeLine = "Creature — Djinn",
            OracleText = "Flying\nThis creature enters with three wish counters on it.\n{2}{U}{U}, Remove a wish counter from this creature: Reveal the top card of your library. You may play that card without paying its mana cost. If you don't, exile it.", Power = "4", Toughness = "4", Loyalty = null,
            Keywords = new string[] { "Flying" }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Djinn of Wishes\",\n  \"entersWithCounters\": 3,\n  \"entersWithCounterKind\": \"wish\",\n  \"abilities\": [\n    {\n      \"cost\": \"{2}{U}{U}, removeCounters:1:wish\",\n      \"effects\": [\n        {\n          \"playTopFree\": true\n        }\n      ],\n      \"text\": \"{2}{U}{U}, Remove a wish counter from this creature: Reveal the top card of your library. You may play that card without paying its mana cost. If you don't, exile it.\"\n    }\n  ]\n}"),
        ["Polymorph"] = Make(new CardRecord
        {
            OracleId = "039115f2-6322-4674-bc64-f21883ed375a", Name = "Polymorph", Layout = "normal", ManaCost = "{3}{U}", TypeLine = "Sorcery",
            OracleText = "Destroy target creature. It can't be regenerated. Its controller reveals cards from the top of their library until they reveal a creature card. The player puts that card onto the battlefield, then shuffles all other cards revealed this way into their library.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Polymorph\",\n  \"spell\": {\n    \"targets\": [\n      \"creature\"\n    ],\n    \"effects\": [\n      {\n        \"destroy\": \"target\",\n        \"noRegeneration\": true\n      },\n      {\n        \"revealUntil\": {\n          \"types\": [\n            \"creature\"\n          ]\n        },\n        \"from\": \"targetController\",\n        \"to\": \"battlefield\",\n        \"revealerPuts\": true,\n        \"rest\": \"shuffle\"\n      }\n    ],\n    \"text\": \"Destroy target creature. It can't be regenerated. Its controller reveals cards from the top of their library until they reveal a creature card. The player puts that card onto the battlefield, then shuffles all other cards revealed this way into their library.\"\n  }\n}"),
        ["Ponder"] = Make(new CardRecord
        {
            OracleId = "02090581-61aa-4348-ad57-451be8ee91c2", Name = "Ponder", Layout = "normal", ManaCost = "{U}", TypeLine = "Sorcery",
            OracleText = "Look at the top three cards of your library, then put them back in any order. You may shuffle.\nDraw a card.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Ponder\",\n  \"spell\": {\n    \"effects\": [\n      {\n        \"lookAtTop\": 3,\n        \"take\": 0,\n        \"rest\": \"top\",\n        \"restAnyOrder\": true\n      },\n      {\n        \"may\": \"Shuffle your library?\",\n        \"effects\": [\n          {\n            \"shuffle\": \"you\"\n          }\n        ]\n      },\n      {\n        \"draw\": 1\n      }\n    ],\n    \"text\": \"Look at the top three cards of your library, then put them back in any order. You may shuffle. Draw a card.\"\n  }\n}"),
        ["Traumatize"] = Make(new CardRecord
        {
            OracleId = "e2ea7d01-6564-4a7f-b935-49a5e3978dac", Name = "Traumatize", Layout = "normal", ManaCost = "{3}{U}{U}", TypeLine = "Sorcery",
            OracleText = "Target player mills half their library, rounded down.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] { "Mill" }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Traumatize\",\n  \"spell\": {\n    \"targets\": [\n      \"player\"\n    ],\n    \"effects\": [\n      {\n        \"mill\": \"halfLibrary\",\n        \"who\": \"target\"\n      }\n    ],\n    \"text\": \"Target player mills half their library, rounded down.\"\n  }\n}"),
        ["Lurking Predators"] = Make(new CardRecord
        {
            OracleId = "15fbb7b1-c62d-4f82-9f35-2c10299779f4", Name = "Lurking Predators", Layout = "normal", ManaCost = "{4}{G}{G}", TypeLine = "Enchantment",
            OracleText = "Whenever an opponent casts a spell, reveal the top card of your library. If it's a creature card, put it onto the battlefield. Otherwise, you may put that card on the bottom of your library.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Lurking Predators\",\n  \"abilities\": [\n    {\n      \"trigger\": \"opponentCastsSpell\",\n      \"filter\": {\n        \"controller\": \"any\"\n      },\n      \"effects\": [\n        {\n          \"revealTop\": {\n            \"types\": [\n              \"creature\"\n            ]\n          },\n          \"effects\": [\n            {\n              \"reanimate\": \"found\"\n            }\n          ],\n          \"else\": [\n            {\n              \"may\": \"Put that card on the bottom of your library?\",\n              \"effects\": [\n                {\n                  \"toLibrary\": \"found\",\n                  \"bottom\": true\n                }\n              ]\n            }\n          ]\n        }\n      ],\n      \"text\": \"Whenever an opponent casts a spell, reveal the top card of your library. If it's a creature card, put it onto the battlefield. Otherwise, you may put that card on the bottom of your library.\"\n    }\n  ]\n}"),
        ["Open the Vaults"] = Make(new CardRecord
        {
            OracleId = "1e9c473e-bd65-4e1f-b2ab-cac58dc581c9", Name = "Open the Vaults", Layout = "normal", ManaCost = "{4}{W}{W}", TypeLine = "Sorcery",
            OracleText = "Return all artifact and enchantment cards from all graveyards to the battlefield under their owners' control. (Auras with nothing to enchant remain in graveyards.)", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Open the Vaults\",\n  \"spell\": {\n    \"effects\": [\n      {\n        \"reanimateAll\": {\n          \"types\": [\n            \"artifact\",\n            \"enchantment\"\n          ]\n        },\n        \"from\": \"everyone\",\n        \"ownerControl\": true\n      }\n    ],\n    \"text\": \"Return all artifact and enchantment cards from all graveyards to the battlefield under their owners' control. (Auras with nothing to enchant remain in graveyards.)\"\n  }\n}"),
        ["Rise from the Grave"] = Make(new CardRecord
        {
            OracleId = "4e769107-0f32-4181-9e57-ffebc2228d3a", Name = "Rise from the Grave", Layout = "normal", ManaCost = "{4}{B}", TypeLine = "Sorcery",
            OracleText = "Put target creature card from a graveyard onto the battlefield under your control. That creature is a black Zombie in addition to its other colors and types.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Rise from the Grave\",\n  \"spell\": {\n    \"targets\": [\n      {\n        \"kind\": \"graveyardCard\",\n        \"filter\": {\n          \"types\": [\n            \"creature\"\n          ]\n        }\n      }\n    ],\n    \"effects\": [\n      {\n        \"reanimate\": \"target\",\n        \"addSubtypes\": [\n          \"Zombie\"\n        ],\n        \"addColors\": [\n          \"B\"\n        ]\n      }\n    ],\n    \"text\": \"Put target creature card from a graveyard onto the battlefield under your control. That creature is a black Zombie in addition to its other colors and types.\"\n  }\n}"),
        ["Awakener Druid"] = Make(new CardRecord
        {
            OracleId = "7e8f5b34-fd16-4307-9104-17b584b25b14", Name = "Awakener Druid", Layout = "normal", ManaCost = "{2}{G}", TypeLine = "Creature — Human Druid",
            OracleText = "When this creature enters, target Forest becomes a 4/5 green Treefolk creature for as long as this creature remains on the battlefield. It's still a land.", Power = "1", Toughness = "1", Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Awakener Druid\",\n  \"abilities\": [\n    {\n      \"trigger\": \"enters\",\n      \"targets\": [\n        {\n          \"kind\": \"land\",\n          \"filter\": {\n            \"subtype\": \"Forest\"\n          },\n          \"text\": \"target Forest\"\n        }\n      ],\n      \"effects\": [\n        {\n          \"become\": \"target\",\n          \"power\": 4,\n          \"toughness\": 5,\n          \"addTypes\": [\n            \"creature\"\n          ],\n          \"addSubtypes\": [\n            \"Treefolk\"\n          ],\n          \"setColors\": [\n            \"G\"\n          ],\n          \"whileSource\": true\n        }\n      ],\n      \"text\": \"When this creature enters, target Forest becomes a 4/5 green Treefolk creature for as long as this creature remains on the battlefield. It's still a land.\"\n    }\n  ]\n}"),
        ["Ajani Goldmane"] = Make(new CardRecord
        {
            OracleId = "dfb5f660-fd8b-4b7b-934c-6a71cd182f15", Name = "Ajani Goldmane", Layout = "normal", ManaCost = "{2}{W}{W}", TypeLine = "Legendary Planeswalker — Ajani",
            OracleText = "+1: You gain 2 life.\n−1: Put a +1/+1 counter on each creature you control. Those creatures gain vigilance until end of turn.\n−6: Create a white Avatar creature token. It has \"This token's power and toughness are each equal to your life total.\"", Power = null, Toughness = null, Loyalty = "4",
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Ajani Goldmane\",\n  \"abilities\": [\n    {\n      \"cost\": \"+1\",\n      \"effects\": [\n        {\n          \"gainLife\": 2\n        }\n      ],\n      \"text\": \"+1: You gain 2 life.\"\n    },\n    {\n      \"cost\": \"-1\",\n      \"effects\": [\n        {\n          \"counters\": 1,\n          \"what\": {\n            \"each\": {\n              \"types\": [\n                \"creature\"\n              ],\n              \"controller\": \"you\"\n            }\n          }\n        },\n        {\n          \"pump\": [\n            0,\n            0\n          ],\n          \"what\": {\n            \"each\": {\n              \"types\": [\n                \"creature\"\n              ],\n              \"controller\": \"you\"\n            }\n          },\n          \"keywords\": [\n            \"Vigilance\"\n          ]\n        }\n      ],\n      \"text\": \"−1: Put a +1/+1 counter on each creature you control. Those creatures gain vigilance until end of turn.\"\n    },\n    {\n      \"cost\": \"-6\",\n      \"effects\": [\n        {\n          \"tokens\": 1,\n          \"token\": {\n            \"name\": \"Avatar\",\n            \"types\": \"Creature — Avatar\",\n            \"colors\": [\n              \"W\"\n            ],\n            \"powerFrom\": \"life\",\n            \"toughnessFrom\": \"life\",\n            \"text\": \"This token's power and toughness are each equal to your life total.\"\n          }\n        }\n      ],\n      \"text\": \"−6: Create a white Avatar creature token. It has \\\"This token's power and toughness are each equal to your life total.\\\"\"\n    }\n  ]\n}"),
        ["Coat of Arms"] = Make(new CardRecord
        {
            OracleId = "5f7f133e-58ea-41ab-b1be-be4b400fac4c", Name = "Coat of Arms", Layout = "normal", ManaCost = "{5}", TypeLine = "Artifact",
            OracleText = "Each creature gets +1/+1 for each other creature on the battlefield that shares at least one creature type with it. (For example, if two Goblin Warriors and a Goblin Shaman are on the battlefield, each gets +2/+2.)", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Coat of Arms\",\n  \"abilities\": [\n    {\n      \"static\": {\n        \"affects\": \"creatures\",\n        \"pump\": [\n          \"otherCreaturesSharingType\",\n          \"otherCreaturesSharingType\"\n        ]\n      },\n      \"text\": \"Each creature gets +1/+1 for each other creature on the battlefield that shares at least one creature type with it.\"\n    }\n  ]\n}"),
        ["Convincing Mirage"] = Make(new CardRecord
        {
            OracleId = "685c4665-e04c-4012-aa5b-888a042c2a21", Name = "Convincing Mirage", Layout = "normal", ManaCost = "{1}{U}", TypeLine = "Enchantment — Aura",
            OracleText = "Enchant land\nAs this Aura enters, choose a basic land type.\nEnchanted land is the chosen type.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] { "Enchant" }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Convincing Mirage\",\n  \"chooseOnEnter\": \"basicLandType\",\n  \"abilities\": [\n    {\n      \"static\": {\n        \"affects\": \"enchanted\",\n        \"setChosenLandType\": true\n      },\n      \"text\": \"Enchanted land is the chosen type.\"\n    }\n  ]\n}"),
        ["Telepathy"] = Make(new CardRecord
        {
            OracleId = "d2e647ac-717f-46e6-baa8-6a793e7bdc32", Name = "Telepathy", Layout = "normal", ManaCost = "{U}", TypeLine = "Enchantment",
            OracleText = "Your opponents play with their hands revealed.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Telepathy\",\n  \"opponentsPlayWithHandsRevealed\": true\n}"),
        ["Vampire Nocturnus"] = Make(new CardRecord
        {
            OracleId = "2109c49e-dc81-4a10-8265-3cf8c90db523", Name = "Vampire Nocturnus", Layout = "normal", ManaCost = "{1}{B}{B}{B}", TypeLine = "Creature — Vampire",
            OracleText = "Play with the top card of your library revealed.\nAs long as the top card of your library is black, this creature and other Vampire creatures you control get +2/+1 and have flying.", Power = "3", Toughness = "3", Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Vampire Nocturnus\",\n  \"playWithTopCardRevealed\": true,\n  \"abilities\": [\n    {\n      \"static\": {\n        \"affects\": \"self\",\n        \"pump\": [\n          2,\n          1\n        ],\n        \"keywords\": [\n          \"Flying\"\n        ],\n        \"while\": {\n          \"topOfLibrary\": {\n            \"colors\": [\n              \"B\"\n            ]\n          }\n        }\n      },\n      \"text\": \"As long as the top card of your library is black, this creature gets +2/+1 and has flying.\"\n    },\n    {\n      \"static\": {\n        \"affects\": \"creatures:you\",\n        \"other\": true,\n        \"subtype\": \"Vampire\",\n        \"pump\": [\n          2,\n          1\n        ],\n        \"keywords\": [\n          \"Flying\"\n        ],\n        \"while\": {\n          \"topOfLibrary\": {\n            \"colors\": [\n              \"B\"\n            ]\n          }\n        }\n      },\n      \"text\": \"As long as the top card of your library is black, other Vampire creatures you control get +2/+1 and have flying.\"\n    }\n  ]\n}"),
        ["Silence"] = Make(new CardRecord
        {
            OracleId = "8aed54cb-d1bb-45ad-adbe-38e55d84ff31", Name = "Silence", Layout = "normal", ManaCost = "{W}", TypeLine = "Instant",
            OracleText = "Your opponents can't cast spells this turn.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Silence\",\n  \"spell\": {\n    \"effects\": [\n      {\n        \"opponentsCantCastSpells\": true\n      }\n    ],\n    \"text\": \"Your opponents can't cast spells this turn.\"\n  }\n}"),
        ["Soul Bleed"] = Make(new CardRecord
        {
            OracleId = "e4f1acd6-b883-470b-88c2-3989011869d7", Name = "Soul Bleed", Layout = "normal", ManaCost = "{2}{B}", TypeLine = "Enchantment — Aura",
            OracleText = "Enchant creature\nAt the beginning of the upkeep of enchanted creature's controller, that player loses 1 life.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] { "Enchant" }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Soul Bleed\",\n  \"abilities\": [\n    {\n      \"trigger\": \"enchantedControllersUpkeep\",\n      \"effects\": [\n        {\n          \"loseLife\": 1,\n          \"who\": \"triggeredPlayer\"\n        }\n      ],\n      \"text\": \"At the beginning of the upkeep of enchanted creature's controller, that player loses 1 life.\"\n    }\n  ]\n}"),
        ["Doom Blade"] = Make(new CardRecord
        {
            OracleId = "59e7f2ae-4535-4191-98be-3e65b6b2befa", Name = "Doom Blade", Layout = "normal", ManaCost = "{1}{B}", TypeLine = "Instant",
            OracleText = "Destroy target nonblack creature.", Power = null, Toughness = null, Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Doom Blade\",\n  \"spell\": {\n    \"targets\": [\n      {\n        \"kind\": \"creature\",\n        \"filter\": {\n          \"notColors\": [\n            \"B\"\n          ]\n        },\n        \"text\": \"target nonblack creature\"\n      }\n    ],\n    \"effects\": [\n      {\n        \"destroy\": \"target\"\n      }\n    ],\n    \"text\": \"Destroy target nonblack creature.\"\n  }\n}"),
        ["Dread Warlock"] = Make(new CardRecord
        {
            OracleId = "5791710d-c718-4f64-9c14-3668154716f1", Name = "Dread Warlock", Layout = "normal", ManaCost = "{1}{B}{B}", TypeLine = "Creature — Human Wizard Warlock",
            OracleText = "This creature can't be blocked except by black creatures.", Power = "2", Toughness = "2", Loyalty = null,
            Keywords = new string[] {  }.Where(k => k.Length > 0).ToArray(),
        }, "{\n  \"name\": \"Dread Warlock\",\n  \"cantBeBlockedBy\": {\n    \"types\": [\n      \"creature\"\n    ],\n    \"notColors\": [\n      \"B\"\n    ]\n  }\n}"),
    };

    public static CardDefinition Get(string name) => Cards[name];
}
