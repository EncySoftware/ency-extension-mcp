using System.Text.Json;

namespace EncyExtensionMcp;

/// <summary>
/// Store tokens without DevTools: `ency-extension-mcp login` performs a Keycloak password
/// grant once (scope offline_access) and stores ONLY the refresh token under %APPDATA%;
/// afterwards fresh access tokens are minted on demand. The ENCY_STORE_TOKEN env var, when
/// set, overrides everything (CI/debug escape hatch).
/// </summary>
/** What the tools need from the sign-in: a token, and a way to get one without touching stdout. */
public interface IStoreAuth
{
    Task<string?> GetAccessToken();
    /** Browser sign-in that writes only through `log` — inside the MCP server stdout is the protocol. */
    Task<bool> LoginBrowser(Action<string> log);
}

public class StoreTokenProvider : IStoreAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _tokenEndpoint =
        Environment.GetEnvironmentVariable("ENCY_KEYCLOAK_TOKEN_ENDPOINT")
        ?? "https://webservices.encycam.com/keycloak/realms/licsys/protocol/openid-connect/token";

    /** The console password flow needs Direct Access Grants, which only digital-twins has. */
    private readonly string _clientId =
        Environment.GetEnvironmentVariable("ENCY_STORE_CLIENT_ID") ?? "digital-twins";

    /**
     * The browser flow needs no Direct Access Grants — that is the point of it — but it does need
     * Standard Flow and a loopback redirect URI on the client.
     *
     * <para>Checked against the live realm on 2026-07-26: there is no `extension-store` client
     * ("Client not found"), while `digital-twins` serves the sign-in page for a
     * http://localhost:PORT/callback redirect, so this works today. It works because that client's
     * valid redirect URIs are wildcard-permissive, which is somebody else's bug to fix — the moment
     * it is tightened, the loopback URI has to be added explicitly or this breaks. The proper end
     * state is a dedicated public client; when it exists, set ENCY_STORE_BROWSER_CLIENT_ID.</para>
     */
    private readonly string _browserClientId =
        Environment.GetEnvironmentVariable("ENCY_STORE_BROWSER_CLIENT_ID")
        ?? Environment.GetEnvironmentVariable("ENCY_STORE_CLIENT_ID")
        ?? "digital-twins";

    private string? _cachedAccess;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    public static string AuthFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ency-extension-mcp", "auth.json");

    /// <summary>Fresh access token: env override → cached → minted from the stored refresh token.
    /// Null when the user never logged in (callers should point at `ency-extension-mcp login`).</summary>
    public async Task<string?> GetAccessToken()
    {
        var env = Environment.GetEnvironmentVariable("ENCY_STORE_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        if (_cachedAccess != null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedAccess;

        var stored = ReadStored();
        if (stored.Refresh == null) return null;

        // Refresh with the client that ISSUED the token: a refresh token belongs to its client, so
        // signing in through the browser (extension-store) and refreshing as digital-twins would
        // fail. Tokens saved before this was recorded predate the browser flow, hence the fallback.
        var resp = await Http.PostAsync(_tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = stored.ClientId ?? _clientId,
            ["refresh_token"] = stored.Refresh,
        }));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "Store login expired or was revoked - run `ency-extension-mcp login` again. (" + Trim(json) + ")");
        using var doc = JsonDocument.Parse(json);
        _cachedAccess = doc.RootElement.GetProperty("access_token").GetString();
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;
        _cachedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
        // Keycloak rotates refresh tokens - keep the newest one.
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } newRefresh)
            SaveRefreshToken(newRefresh, stored.ClientId ?? _clientId);
        return _cachedAccess;
    }

    /**
     * Sign in through the ENCY sign-in page in a browser — the default, because this tool has no
     * business seeing anyone's password, and because two-factor and SSO only work there.
     */
    public async Task<int> LoginBrowser()
    {
        if (!await LoginBrowser(Console.Error.WriteLine)) return 1;
        Console.WriteLine("Signed in. Tokens are minted automatically from now on (stored: " + AuthFilePath + ").");
        return 0;
    }

    public async Task<bool> LoginBrowser(Action<string> log)
    {
        string? refresh = await BrowserLogin.SignIn(_tokenEndpoint, _browserClientId, OpenInBrowser, log, Http);
        if (refresh == null) return false;
        SaveRefreshToken(refresh, _browserClientId);
        _cachedAccess = null;   // the next GetAccessToken mints from the fresh refresh token
        return true;
    }

    private static Task OpenInBrowser(string url)
    {
        try
        {
            // UseShellExecute is what hands the URL to the default browser rather than trying to
            // execute it; without it this throws on Windows.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            // Not fatal: the address was printed, and pasting it by hand works the same.
            Console.Error.WriteLine("Could not open the browser (" + e.Message + ") — open the address above.");
        }
        return Task.CompletedTask;
    }

    /**
     * `ency-extension-mcp login --password`: the old console flow, kept for the case the browser
     * one cannot work — no loopback redirect allowed on the Keycloak client yet, or no browser on
     * the machine at all. It needs Direct Access Grants, which is why it uses the other client.
     */
    public async Task<int> LoginInteractive()
    {
        Console.Write("ENCY store login (licsys email): ");
        string? user = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(user)) { Console.Error.WriteLine("no login given"); return 1; }
        Console.Write("Password (hidden): ");
        string pass = ReadHidden();
        Console.WriteLine();

        var resp = await Http.PostAsync(_tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _clientId,
            ["username"] = user.Trim(),
            ["password"] = pass,
            ["scope"] = "openid offline_access",
        }));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine("Login failed: " + Trim(json));
            return 1;
        }
        using var doc = JsonDocument.Parse(json);
        var refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
        {
            Console.Error.WriteLine("Keycloak returned no refresh token - cannot stay logged in.");
            return 1;
        }
        SaveRefreshToken(refresh, _clientId);
        Console.WriteLine("Logged in. Tokens are minted automatically from now on (stored: " + AuthFilePath + ").");
        return 0;
    }

    /** clientId is null for files written before the browser flow existed. */
    private record Stored(string? Refresh, string? ClientId);

    private static Stored ReadStored()
    {
        try
        {
            if (!File.Exists(AuthFilePath)) return new Stored(null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(AuthFilePath));
            var root = doc.RootElement;
            return new Stored(
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                root.TryGetProperty("client_id", out var cid) ? cid.GetString() : null);
        }
        catch (Exception) { return new Stored(null, null); }
    }

    private static void SaveRefreshToken(string refresh, string clientId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuthFilePath)!);
        File.WriteAllText(AuthFilePath,
            JsonSerializer.Serialize(new { refresh_token = refresh, client_id = clientId }));
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
