// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Arcanum.Net.Services;

namespace Arcanum.Client;

/// <summary>
/// The player's cloud files kept by the career service, for every device (the online services' own storage needs their
/// library, which some systems can't run). A file the service doesn't have yet is taken from <paramref name="earlier"/>
/// (where this computer kept it before) and copied to the service.
/// </summary>
public sealed class ServiceCloud(Func<Task<string?>> idToken, ICloudStorage? earlier) : ICloudStorage
{
    private static readonly HttpClient Http = new() { BaseAddress = Account.ServiceUrl, Timeout = TimeSpan.FromSeconds(60) };

    public bool IsReady => true;

    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Get, name, null, cancel);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancel);
        }
        if (earlier is not { IsReady: true } || await earlier.ReadAsync(name, cancel) is not { } kept) return null;
        await WriteAsync(name, kept, cancel);
        return kept;
    }

    public async Task WriteAsync(string name, byte[] data, CancellationToken cancel = default)
    {
        using var response = await SendAsync(HttpMethod.Put, name, data, cancel);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string name, byte[]? data, CancellationToken cancel)
    {
        var token = await idToken() ?? throw new InvalidOperationException("Not signed in to the online services.");
        using var request = new HttpRequestMessage(method, $"cloud/{Uri.EscapeDataString(name)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (data is not null) request.Content = new ByteArrayContent(data);
        return await Task.Run(() => Http.SendAsync(request, cancel), cancel); // not on the screen's thread: setting up a connection takes long on slow devices
    }
}
