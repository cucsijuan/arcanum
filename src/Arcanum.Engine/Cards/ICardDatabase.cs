// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.Cards;

public interface ICardDatabase
{
    bool TryGet(string name, out CardDefinition definition);

    CardDefinition Get(string name) =>
        TryGet(name, out var def) ? def : throw new KeyNotFoundException($"Unknown card '{name}'.");
}
