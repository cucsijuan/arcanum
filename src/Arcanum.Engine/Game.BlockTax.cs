// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Players;

namespace Arcanum.Engine;

public sealed partial class Game
{
    /// <summary>
    /// What blocking costs for each blocking creature: "creatures can't block unless their controller pays {1} for each of
    /// those creatures" from every permanent whose condition holds ("as long as this creature is attacking"), added up;
    /// null when blocking is free.
    /// </summary>
    private Mana.ManaCost? BlockTax()
    {
        var total = Mana.ManaCost.Zero;
        foreach (var c in State.Battlefield.Select(State.GetCard))
            if (c.Definition.BlockTax is { } tax && !c.LosesAbilities && (c.Definition.BlockTaxIf is not { } cond || Holds(cond, c.Controller, c)))
                total = total.Plus(tax);
        return total.ManaValue > 0 ? total : null;
    }

    /// <summary>
    /// A block declaration with a block tax (rule 509.1c): a requirement that could only be obeyed by paying a cost is not
    /// required, and every block costs something, so no requirement has to be obeyed (blocking as a requirement says stays
    /// allowed). The request also tells the player what each blocking creature costs and how many they can pay for now.
    /// </summary>
    private BlockRequest WithBlockTax(BlockRequest request, Core.PlayerId defender)
    {
        if (BlockTax() is not { } tax) return request;
        int affordable = 0;
        var cost = Mana.ManaCost.Zero;
        while (affordable < request.Blockers.Count && Payable(defender, cost.Plus(tax), null)) { cost = cost.Plus(tax); affordable++; }
        return request with
        {
            MustBeBlocked = Array.Empty<Core.CardId>(), Lures = Array.Empty<Core.CardId>(), TaxPerBlocker = tax.ToString(), AffordableBlockers = affordable,
        };
    }

    /// <summary>
    /// Asks the defending player for blocks and has them pay the block tax for each blocking creature as the blockers are
    /// declared (rules 509.1d, 509.1h-i; mana abilities may be activated, 509.1f). A declaration whose costs aren't paid is
    /// made again; a player who keeps declaring blocks they don't pay for doesn't block.
    /// </summary>
    private async Task<IReadOnlyList<BlockDeclaration>> DeclarePaidBlocksAsync(Core.PlayerId defender, BlockRequest request)
    {
        for (int attempt = 0; ; attempt++)
        {
            var declared = await ControllerOf(defender).DeclareBlockersAsync(ViewFor(defender), request);
            Require(request.IsLegal(declared, out var reason), reason ?? "Illegal blocks.");
            if (declared.Count == 0 || BlockTax() is not { } tax) return declared;
            var total = Mana.ManaCost.Zero;
            var blockers = declared.Select(b => b.Blocker).Distinct().ToList();
            foreach (var _ in blockers) total = total.Plus(tax);
            if (Payable(defender, total, null) && await PayManaAsync(State.GetPlayer(defender), blockers[0], total, null)) return declared;
            if (attempt >= 4) return Array.Empty<BlockDeclaration>();
        }
    }
}
