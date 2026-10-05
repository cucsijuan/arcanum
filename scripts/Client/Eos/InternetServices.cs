// SPDX-License-Identifier: AGPL-3.0-or-later
using Arcanum.Net.Services;

namespace Arcanum.Client.Eos;

/// <summary>The internet service this build can use, if any (it needs the online services SDK and the product's keys).</summary>
public static class InternetServices
{
    public static IOnlineServices? Create(string version, Action<string> log)
    {
#if ARCANUM_EOS
        if (EosKeys.Current is not { } keys) return null;
        var eos = new EosOnlineServices(keys, version);
        eos.Log += log;
        return eos;
#else
        return null;
#endif
    }
}
