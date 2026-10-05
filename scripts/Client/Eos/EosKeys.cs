// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Client.Eos;

/// <summary>
/// The Epic Online Services product's keys. They are not in the repository: tools/eos_keys.py writes them into
/// EosKeys.g.cs at build time (from a local file, or from the release workflow's secrets). Without them there are no
/// internet lobbies, only the local network.
/// </summary>
internal sealed partial record EosKeys(string ProductId, string SandboxId, string DeploymentId, string ClientId, string ClientSecret)
{
    public static EosKeys? Current
    {
        get
        {
            EosKeys? keys = null;
            Load(ref keys);
            return keys;
        }
    }

    static partial void Load(ref EosKeys? keys);
}
