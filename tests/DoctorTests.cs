using System.Text;
using EncyExtensionMcp;
using Xunit;

/** doctor: six lines, each either fine or naming the command that fixes it. */
public class DoctorTests
{
    /** A JWT the way Keycloak shapes it, unsigned — doctor reads the claims, it does not verify them. */
    private static string Jwt(string user, string client)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("{\"alg\":\"RS256\"}") + "." + B64($"{{\"preferred_username\":\"{user}\",\"azp\":\"{client}\",\"exp\":1}}") + ".sig";
    }

    private static string McpJson(bool withServer)
    {
        string path = Path.Combine(Path.GetTempPath(), "mcp-doc-" + Guid.NewGuid().ToString("N"), "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, withServer ? "{\"mcpServers\":{\"ency-extension-store\":{\"command\":\"ency-extension-mcp\"}}}" : "{\"mcpServers\":{}}");
        return path;
    }

    private static FakeProcessRunner Healthy() => new FakeProcessRunner()
        .On("dotnet --version", stdout: "8.0.404\n")
        .On("git --version", stdout: "git version 2.46.0.windows.1\n")
        .On("gh --version", stdout: "gh version 2.58.0 (2024-10-29)\nhttps://github.com/cli/cli/releases/tag/v2.58.0\n")
        .On("gh auth status", stdout: "Logged in to github.com account someone\n");

    [Fact]
    public async Task A_healthy_machine_reads_as_six_fine_lines()
    {
        var report = await Doctor.Report(Healthy(), new FakeStoreClient(),
            new FakeStoreAuth { Token = Jwt("someone@example.com", "extension-store") },
            new NoUpdateCheck(), McpJson(true), "0.2.14");

        Assert.Contains("tool:     ency-extension-mcp 0.2.14", report);
        Assert.Contains("dotnet:   SDK 8.0.404", report);
        Assert.Contains("git:      git version 2.46.0", report);
        Assert.Contains("gh:       gh version 2.58.0 (2024-10-29), signed in", report);
        Assert.Contains("store:    https://store.test — reachable (3 categories)", report);
        Assert.Contains("sign-in:  someone@example.com via client extension-store", report);
        Assert.Contains("lists ency-extension-store", report);
        Assert.DoesNotContain("NOT", report);
    }

    [Fact]
    public async Task Each_missing_piece_names_its_fix_and_who_needs_it()
    {
        var proc = new FakeProcessRunner().On("dotnet --version", stdout: "8.0.404\n"); // git and gh: no rule = not found
        var report = await Doctor.Report(proc, new FakeStoreClient(), new FakeStoreAuth { Token = null },
            new NoUpdateCheck(), McpJson(false), "0.2.14");

        Assert.Contains("git:      not found — needed by create_extension_repo and publish_extension only", report);
        Assert.Contains("gh:       not found — needed by create_extension_repo only", report);
        Assert.Contains("sign-in:  not signed in — run `ency-extension-mcp login`", report);
        Assert.Contains("does not list ency-extension-store — run `ency-extension-mcp setup`", report);
    }

    [Fact]
    public async Task An_expired_sign_in_and_a_newer_tool_are_said_plainly()
    {
        var auth = new ThrowingAuth("Store login expired or was revoked - run `ency-extension-mcp login` again.");
        var report = await Doctor.Report(Healthy(), new FakeStoreClient(), auth, new FixedNote("Tool 0.2.13; 0.2.14 is available: dotnet tool update -g EncySoftware.ExtensionStoreMcp --no-cache"),
            McpJson(true), "0.2.13");

        Assert.Contains("sign-in:  EXPIRED — Store login expired", report);
        Assert.Contains("tool:     ency-extension-mcp 0.2.13 — 0.2.14 is available: dotnet tool update", report);
    }

    [Fact]
    public void Identity_is_read_off_the_token_and_survives_garbage()
    {
        Assert.Equal(("someone@example.com", "extension-store"), Doctor.Identity(Jwt("someone@example.com", "extension-store")));
        Assert.Equal((null, null), Doctor.Identity("not-a-jwt"));
    }

    private sealed class ThrowingAuth(string message) : IStoreAuth
    {
        public Task<string?> GetAccessToken() => throw new InvalidOperationException(message);
        public Task<bool> LoginBrowser(Action<string> log) => Task.FromResult(false);
    }

    private sealed class FixedNote(string? note) : IUpdateCheck
    {
        public Task<string?> Note() => Task.FromResult(note);
    }
}
