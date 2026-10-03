// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Engine.Core;
using Arcanum.Engine.Mana;

namespace Arcanum.Engine.State;

public sealed class Player
{
    public PlayerId Id { get; }
    public string Name { get; }
    public int Life { get; set; }

    /// <summary>Index 0 is the top of the library.</summary>
    public List<CardId> Library { get; } = new();
    public List<CardId> Hand { get; } = new();
    public List<CardId> Graveyard { get; } = new();
    public List<CardId> Exile { get; } = new();
    public List<CardId> Command { get; } = new();

    public ManaPool ManaPool { get; } = new();
    public int LandsPlayedThisTurn { get; set; }

    /// <summary>Set when the player tried to draw from an empty library; checked as a state-based action.</summary>
    public bool AttemptedDrawFromEmptyLibrary { get; set; }

    public bool HasLost { get; set; }

    /// <summary>Times each of this player's commanders has been cast from the command zone (commander tax).</summary>
    public Dictionary<CardId, int> CommanderCasts { get; } = new();

    /// <summary>Combat damage this player has taken from each commander (rule 903.10a).</summary>
    public Dictionary<CardId, int> CommanderDamageTaken { get; } = new();

    public Player(PlayerId id, string name, int life)
    {
        Id = id;
        Name = name;
        Life = life;
    }

    public List<CardId> GetZone(Zone zone) => zone switch
    {
        Zone.Library => Library,
        Zone.Hand => Hand,
        Zone.Graveyard => Graveyard,
        Zone.Exile => Exile,
        Zone.Command => Command,
        _ => throw new ArgumentException($"{zone} is not a per-player zone.", nameof(zone)),
    };

    public override string ToString() => $"{Name} ({Id})";
}
