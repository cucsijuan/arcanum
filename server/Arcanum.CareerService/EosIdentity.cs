// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arcanum.CareerService;

public sealed class EosOptions
{
    /// <summary>The product and deployment a player's ID token must be for (from the developer portal).</summary>
    public string ProductId { get; set; } = "";
    public string DeploymentId { get; set; } = "";

    /// <summary>EOS Connect's public keys.</summary>
    public string JwksUrl { get; set; } = "https://api.epicgames.dev/auth/v1/oauth/jwks";
}

/// <summary>
/// Who is asking: the game sends the player's EOS Connect ID token (<c>Authorization: Bearer …</c>), a JWT signed by EOS
/// (RS256). Its signature, expiry, product and deployment are checked here; the player is its subject (the product user
/// id), never something the request says.
/// </summary>
public sealed class EosIdentity(HttpClient http, Microsoft.Extensions.Options.IOptions<EosOptions> options, ILogger<EosIdentity> log)
{
    private readonly EosOptions _options = options.Value;
    private static Dictionary<string, RSA> _keys = new();
    private static DateTime _keysLoaded;
    private static readonly SemaphoreSlim KeysLock = new(1, 1);

    public bool Configured => _options.ProductId.Length > 0 && _options.DeploymentId.Length > 0;

    /// <summary>The player's product user id, or null when the request carries no valid token.</summary>
    public async Task<string?> PlayerAsync(HttpRequest request, CancellationToken cancel)
    {
        if (!Configured) return null;
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var parts = header[7..].Trim().Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var head = JsonDocument.Parse(Base64Url(parts[0]));
            if (head.RootElement.GetProperty("alg").GetString() != "RS256") return null;
            var kid = head.RootElement.TryGetProperty("kid", out var k) ? k.GetString() ?? "" : "";
            var key = await KeyAsync(kid, cancel);
            if (key is null) return null;
            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            if (!key.VerifyData(signed, Base64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;

            using var body = JsonDocument.Parse(Base64Url(parts[1]));
            var claims = body.RootElement;
            string? Claim(string name) => claims.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long exp = claims.TryGetProperty("exp", out var e) && e.TryGetInt64(out var x) ? x : 0;
            if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow.AddSeconds(-60)) return null;
            if (Claim("iss") is not { } iss || !iss.StartsWith("https://api.epicgames.dev/", StringComparison.Ordinal)) return null;
            if (Claim("pfpid") != _options.ProductId || Claim("pfdid") != _options.DeploymentId) return null;
            return Claim("sub") is { Length: > 0 } player ? player : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The key a token names; the key list is fetched again when it doesn't have it (rotated keys), at most every minute.</summary>
    private async Task<RSA?> KeyAsync(string kid, CancellationToken cancel)
    {
        if (_keys.TryGetValue(kid, out var key)) return key;
        await KeysLock.WaitAsync(cancel);
        try
        {
            if (_keys.TryGetValue(kid, out key)) return key;
            if (DateTime.UtcNow - _keysLoaded < TimeSpan.FromMinutes(1)) return null;
            _keysLoaded = DateTime.UtcNow;
            using var jwks = JsonDocument.Parse(await http.GetStringAsync(_options.JwksUrl, cancel));
            var keys = new Dictionary<string, RSA>();
            foreach (var jwk in jwks.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (jwk.GetProperty("kty").GetString() != "RSA") continue;
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = Base64Url(jwk.GetProperty("n").GetString()!), Exponent = Base64Url(jwk.GetProperty("e").GetString()!) });
                keys[jwk.TryGetProperty("kid", out var id) ? id.GetString() ?? "" : ""] = rsa;
            }
            _keys = keys;
            return keys.GetValueOrDefault(kid);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log.LogWarning("EOS keys couldn't be fetched: {Message}", ex.Message);
            return null;
        }
        finally
        {
            KeysLock.Release();
        }
    }

    private static byte[] Base64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
