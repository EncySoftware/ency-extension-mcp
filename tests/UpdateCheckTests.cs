using EncyExtensionMcp;
using Xunit;

/** The version note: right about newer/older, asked of nuget.org once a day, silent when anything fails. */
public class UpdateCheckTests
{
    private static string TempCache() =>
        Path.Combine(Path.GetTempPath(), "mcp-upd-" + Guid.NewGuid().ToString("N"), "update-check.json");

    [Fact]
    public void Newer_means_strictly_higher_numbers()
    {
        Assert.True(UpdateCheck.IsNewer("0.2.14", "0.2.13"));
        Assert.True(UpdateCheck.IsNewer("1.0.0", "0.9.9"));
        Assert.False(UpdateCheck.IsNewer("0.2.13", "0.2.13"));
        Assert.False(UpdateCheck.IsNewer("0.2.12", "0.2.13"));
        Assert.False(UpdateCheck.IsNewer("garbage", "0.2.13"));
    }

    [Fact]
    public void The_index_yields_the_highest_stable_version()
    {
        Assert.Equal("0.2.13", UpdateCheck.LatestOf("""{"versions":["0.1.7","0.2.9","0.2.13","0.3.0-beta.1","0.2.10"]}"""));
        Assert.Null(UpdateCheck.LatestOf("""{"versions":[]}"""));
    }

    [Fact]
    public async Task Says_the_update_command_when_a_newer_version_exists()
    {
        var check = new UpdateCheck(() => Task.FromResult<string?>("0.2.14"), TempCache(), current: "0.2.13");
        string? note = await check.Note();
        Assert.NotNull(note);
        Assert.Contains("0.2.14 is available", note);
        Assert.Contains("dotnet tool update -g EncySoftware.ExtensionStoreMcp --no-cache", note);
    }

    [Fact]
    public async Task Keeps_quiet_when_current_is_the_latest_or_nothing_is_known()
    {
        Assert.Null(await new UpdateCheck(() => Task.FromResult<string?>("0.2.13"), TempCache(), current: "0.2.13").Note());
        Assert.Null(await new UpdateCheck(() => Task.FromResult<string?>(null), TempCache(), current: "0.2.13").Note());
        Assert.Null(await new UpdateCheck(() => throw new HttpRequestException("offline"), TempCache(), current: "0.2.13").Note());
    }

    [Fact]
    public async Task Asks_nuget_once_a_day_and_the_cache_in_between()
    {
        int fetches = 0;
        var clock = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        string cache = TempCache();
        UpdateCheck Check() => new(() => { fetches++; return Task.FromResult<string?>("0.2.14"); }, cache, () => clock, "0.2.13");

        Assert.NotNull(await Check().Note());
        Assert.NotNull(await Check().Note());
        Assert.Equal(1, fetches);                       // the second call read the cache

        clock = clock.AddHours(25);
        Assert.NotNull(await Check().Note());
        Assert.Equal(2, fetches);                       // a day later it asks again
    }

    /** A failed fetch is stamped too — an offline machine must not retry on every tool call. */
    [Fact]
    public async Task A_failed_fetch_is_not_retried_within_the_day()
    {
        int fetches = 0;
        string cache = TempCache();
        UpdateCheck Check() => new(() => { fetches++; throw new HttpRequestException("offline"); }, cache, current: "0.2.13");
        await Check().Note();
        await Check().Note();
        Assert.Equal(1, fetches);
    }

    [Fact]
    public void The_running_version_reads_like_a_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", UpdateCheck.Current);
    }
}
