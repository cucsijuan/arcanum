// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Engine.State;

public enum Zone
{
    Library,
    Hand,
    Battlefield,
    Graveyard,
    Stack,
    Exile,
    Command,
}

public static class ZoneExtensions
{
    /// <summary>Public zones are visible to every player (rule 400.2).</summary>
    public static bool IsPublic(this Zone zone) => zone is not (Zone.Library or Zone.Hand);
}
