// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.State;

namespace Arcanum.Engine.Views;

public static class ViewBuilder
{
    /// <param name="revealAll">Show every hidden card (deck-test / hotseat / spectator-with-cheats).</param>
    /// <param name="commanderTaxPerCast">Commander tax per previous cast (0 outside commander games).</param>
    public static GameView Build(GameState state, PlayerId viewer, bool revealAll = false, int commanderTaxPerCast = 0)
    {
        CardView View(CardId id) => Card(state, id, viewer, revealAll, commanderTaxPerCast);

        IReadOnlyList<CardView> Views(IEnumerable<CardId> ids) => ids.Select(View).ToList();

        return new GameView
        {
            Viewer = viewer,
            TurnNumber = state.TurnNumber,
            ActivePlayer = state.ActivePlayer,
            PriorityPlayer = state.PriorityPlayer,
            Step = state.Step,
            Players = state.Players.Select(p => new PlayerView
            {
                Id = p.Id,
                Name = p.Name,
                Life = p.Life,
                HasLost = p.HasLost,
                LibraryCount = p.Library.Count,
                LibraryTop = p.Library.Count == 0 ? null
                    : (p.Id == viewer && MayLookAtLibraryTop(state, p.Id)) || state.GetCard(p.Library[0]).IsVisibleTo(viewer)
                      || RevealedByEffect(state, state.GetCard(p.Library[0]), viewer)
                        ? Card(state, p.Library[0], viewer, reveal: true)
                        : null,
                KnownLibrary = p.Library.Select((id, i) => (Id: id, Position: i))
                    .Where(x => state.GetCard(x.Id).IsVisibleTo(viewer) || RevealedByEffect(state, state.GetCard(x.Id), viewer))
                    .Select(x => new LibraryCardView(x.Position, View(x.Id))).ToList(),
                Hand = Views(p.Hand),
                Graveyard = Views(p.Graveyard),
                Exile = Views(p.Exile),
                Command = Views(p.Command),
                ManaPoolTotal = p.ManaPool.Total,
                CommanderDamage = new Dictionary<CardId, int>(p.CommanderDamageTaken),
                EnduringStory = p.HasEnduringStory,
                CitysBlessing = p.HasCitysBlessing,
                RingLevel = p.RingLevel,
                RingBearer = p.RingBearer is { } bearer && state.GetCard(bearer.Card) is { Zone: Zone.Battlefield } b && b.Version == bearer.Version ? bearer.Card : null,
                Protected = p.Protected,
                Poison = p.Poison,
                NoMaximumHandSize = p.NoMaximumHandSize,
                Emblems = state.Emblems.Select(state.GetCard).Where(e => e.Owner == p.Id)
                    .Select(e => new EmblemView(e.Name, e.Definition.OracleText, state.EmblemsUntilEndOfTurn.Contains(e.Id))).ToList(),
                ManaPool = Enum.GetValues<Mana.ManaType>().Where(t => p.ManaPool.AllOf(t) > 0).ToDictionary(t => t, t => p.ManaPool.AllOf(t)),
            }).ToList(),
            Battlefield = Views(state.Battlefield),
            Stack = state.Stack.Select(s => new StackItemView(
                View(s.SourceCard), s.Controller, (s as AbilityOnStack)?.Ability.Text, s.Targets.Select(t => t.Target).ToList(), s.Id)).ToList(),
            Attacks = state.Combat?.Attacks
                .Select(a => new AttackView(a.Attacker, a.Defender, a.Blockers.ToList(), a.IsBlocked, a.Planeswalker)).ToList()
                ?? (IReadOnlyList<AttackView>)Array.Empty<AttackView>(),
            IsGameOver = state.IsGameOver,
            Winner = state.Winner,
        };
    }

    /// <summary>
    /// A card an effect keeps revealed to <paramref name="viewer"/>: a hand while an opponent of its owner controls a permanent saying
    /// "your opponents play with their hands revealed" (revealed means to every player, rule 701.20a), or the top card of a library
    /// whose owner controls a permanent saying "play with the top card of your library revealed".
    /// </summary>
    public static bool RevealedByEffect(GameState state, Card card, PlayerId viewer) => card.Zone switch
    {
        Zone.Hand => state.OpponentsOf(card.Owner).Any(o => Revealing(state, o, Cards.Replacements.OpponentsPlayWithHandsRevealed)),
        Zone.Library => state.GetPlayer(card.Owner).Library is { Count: > 0 } library && library[0] == card.Id
                        && Revealing(state, card.Owner, Cards.Replacements.PlayWithTopCardRevealed),
        _ => false,
    };

    private static bool Revealing(GameState state, PlayerId player, Cards.Replacements rule) =>
        !state.GetPlayer(player).HasLost && state.PermanentsControlledBy(player).Any(c => (c.Definition.Replaces & rule) != 0 && !c.LosesAbilities && !c.LosesTextAbilities);

    /// <summary>"You may look at the top card of your library any time" (a permanent the player controls says so).</summary>
    public static bool MayLookAtLibraryTop(GameState state, PlayerId player) =>
        state.PermanentsControlledBy(player).Any(c => (c.Definition.Replaces & (Cards.Replacements.CreaturesFromLibraryTop
            | Cards.Replacements.CastCreaturesFromLibraryTop | Cards.Replacements.LookAtLibraryTop)) != 0);

    /// <summary>One card as <paramref name="viewer"/> may see it; <paramref name="reveal"/> shows it even if hidden.</summary>
    public static CardView Card(GameState state, CardId id, PlayerId viewer, bool reveal = false, int commanderTaxPerCast = 0)
    {
        var card = state.GetCard(id);
        bool visible = reveal || card.IsVisibleTo(viewer) || RevealedByEffect(state, card, viewer);
        if (!visible)
        {
            return new CardView
            {
                Id = card.Id, Owner = card.Owner, Controller = card.Controller, Zone = card.Zone, IsHidden = true,
            };
        }
        var face = new CardView
        {
            Id = card.Id,
            Owner = card.Owner,
            Controller = card.Controller,
            Zone = card.Zone,
            IsHidden = false,
            Name = card.Name,
            ManaCost = card.Definition.ManaCost.ToString(),
            Types = card.Types,
            Power = card.Definition.Power is null && card.Definition.PowerFrom is null ? null : card.Power,
            Toughness = card.Definition.Toughness is null && card.Definition.ToughnessFrom is null ? null : card.Toughness,
            Tapped = card.Tapped,
            Damage = card.Damage,
            SummoningSick = card.Zone == Zone.Battlefield && card.IsSummoningSick,
            Keywords = Enum.GetValues<Cards.Keyword>().Where(card.Has).Select(Cards.Keywords.DisplayName).ToList(),
            PlusOneCounters = card.CounterCount(Abilities.CounterKind.PlusOnePlusOne),
            MinusOneCounters = card.CounterCount(Abilities.CounterKind.MinusOneMinusOne),
            Loyalty = card.CounterCount(Abilities.CounterKind.Loyalty),
            LoreCounters = card.CounterCount(Abilities.CounterKind.Lore),
            FinalChapter = card.Definition.FinalChapter,
            AttacksEachCombat = card.Definition.AttacksEachCombat,
            IsToken = card.Definition.IsToken,
            OracleText = card.Definition.OracleText,
            Colors = card.Colors,
            ImageKey = card.Definition.ImageKey,
            Foil = card.Definition.Foil,
            AttachedTo = card.AttachedTo,
            IsCommander = card.IsCommander,
            CommanderTax = card.IsCommander ? commanderTaxPerCast * state.GetPlayer(card.Owner).CommanderCasts.GetValueOrDefault(card.Id) : 0,
            BasePower = card.Definition.Power,
            BaseToughness = card.Definition.Toughness,
            AbilityTexts = card.Abilities.Select(a => a.Text).ToList(),
            Supertypes = card.Supertypes,
            Subtypes = card.CurrentSubtypes,
            PrintedTypes = card.Definition.Types,
            PrintedSubtypes = card.Definition.Subtypes,
            PrintedColors = card.Definition.ColorList,
            PrintedKeywords = card.Definition.KeywordAbilities.Select(Cards.Keywords.DisplayName).ToList(),
            LostAllAbilities = card.LostAllAbilities,
            GainedAbilityTexts = card.GainedAbilities.Select(a => a.Text).Where(t => !string.IsNullOrEmpty(t)).ToList(),
            OtherCounters = card.Counters.Where(kv => kv.Value > 0 && kv.Key is not (Abilities.CounterKind.PlusOnePlusOne or Abilities.CounterKind.MinusOneMinusOne or Abilities.CounterKind.Loyalty))
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ChosenColor = card.ChosenColor,
            ChosenType = card.ChosenType,
            AdventureName = card.PrintedDefinition.Adventure?.Name,
            AdventureCost = card.PrintedDefinition.Adventure?.ManaCost.ToString(),
            AdventureText = card.PrintedDefinition.Adventure?.OracleText,
            OnAdventure = card.OnAdventure,
            SplitHalves = card.PrintedDefinition.SplitHalves?.Select(h => $"{h.Name} {h.ManaCost}").ToList(),
            Transformed = card.Transformed,
            IsBackFace = card.Transformed && !card.IsCopy && card.IsDoubleFaced,
        };
        // A double-faced card shows its other face on request; a copy of one has only the face it copied (rule 707.8).
        if (card.IsDoubleFaced && !card.IsCopy && card.PrintedDefinition.BackFace is { } back)
            face = face with { OtherFace = PrintedFace(card, card.Transformed ? card.PrintedDefinition : back, isBack: !card.Transformed) };
        return face;
    }

    /// <summary>One face of a double-faced card as printed (its own characteristics, without effects or counters).</summary>
    private static CardView PrintedFace(State.Card card, Cards.CardDefinition face, bool isBack) => new()
    {
        Id = card.Id,
        Owner = card.Owner,
        Controller = card.Controller,
        Zone = card.Zone,
        IsHidden = false,
        Name = face.Name,
        ManaCost = face.ManaCost.ToString(),
        Types = face.Types,
        Power = face.Power,
        Toughness = face.Toughness,
        BasePower = face.Power,
        BaseToughness = face.Toughness,
        Keywords = face.KeywordAbilities.Select(Cards.Keywords.DisplayName).ToList(),
        PrintedKeywords = face.KeywordAbilities.Select(Cards.Keywords.DisplayName).ToList(),
        Loyalty = face.Loyalty ?? 0,
        FinalChapter = face.FinalChapter,
        IsToken = card.PrintedDefinition.IsToken,
        OracleText = face.OracleText,
        Colors = face.ColorList,
        PrintedColors = face.ColorList,
        ImageKey = face.ImageKey ?? card.PrintedDefinition.ImageKey,
        Foil = card.PrintedDefinition.Foil,
        Supertypes = face.Supertypes,
        Subtypes = face.Subtypes,
        PrintedTypes = face.Types,
        PrintedSubtypes = face.Subtypes,
        AbilityTexts = face.Abilities.Select(a => a.Text).ToList(),
        Transformed = isBack,
        IsBackFace = isBack,
    };
}
