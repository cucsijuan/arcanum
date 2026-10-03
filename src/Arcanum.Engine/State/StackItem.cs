// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;

namespace Arcanum.Engine.State;

/// <summary>An object on the stack. Abilities on the stack join spells in M4.</summary>
public abstract record StackItem(PlayerId Controller);

public sealed record SpellOnStack(CardId Card, PlayerId Controller) : StackItem(Controller);
