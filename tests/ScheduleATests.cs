using EncyExtensionMcp;
using Xunit;

namespace EncyExtensionMcp.Tests;

/**
 * The Reserved Functionality declaration in package.info.json: `reservedDomains` (Schedule B §B.3.2),
 * with the earlier `reservedFunctionality` block read as the old form. The store decides — it holds
 * the areas and their licences, and the author confirms the answer in the browser — so what is
 * checked here is only what the file itself shows, before it costs a red build.
 */
public class ScheduleATests
{
    private static readonly DateOnly Before = new(2026, 10, 15);
    private static readonly DateOnly After = new(2026, 11, 2);

    private static ScheduleADeclaration? Of(string json) => ScheduleA.ReadJson(json);

    [Fact]
    public void NoListIsAWarningUntilTheDocumentsTakeEffect()
    {
        var d = Of("{\"packageId\":\"MyExt\"}");
        Assert.Null(d);
        var before = ScheduleA.Findings(d, Before).Single();
        Assert.False(before.Blocking);
        Assert.Contains("reservedDomains", before.Text);
        Assert.Contains("ask them, do not answer it for them", before.Text);
        Assert.True(ScheduleA.Findings(d, After).Single().Blocking);
    }

    [Fact]
    public void AnEmptyListIsTheAnswerNone() => Assert.Empty(ScheduleA.Findings(Of("{\"reservedDomains\":[]}"), After));

    [Fact]
    public void EntriesAreRead()
    {
        var d = Of("{\"reservedDomains\":[{\"domain\":\"A-05\",\"entitlement\":\"ENCY Nesting\",\"capabilities\":[\"nesting.layout\"]}]}");
        Assert.NotNull(d);
        Assert.Equal("A-05", d!.Domains[0].Domain);
        Assert.Equal(new[] { "nesting.layout" }, d.Domains[0].Capabilities);
        Assert.Empty(ScheduleA.Findings(d, After));
    }

    [Fact]
    public void TheEarlierBlockIsReadAndNamedAsTheOldForm()
    {
        var d = Of("{\"reservedFunctionality\":{\"none\":true}}");
        Assert.True(d!.Legacy);
        var before = ScheduleA.Findings(d, Before).Single();
        Assert.False(before.Blocking);
        Assert.Contains("reservedDomains", before.Text);
        Assert.True(ScheduleA.Findings(d, After).Single().Blocking);
    }

    /**
     * The deadline itself, as UTC dates: the last day of October still only warns, the first of
     * November refuses. Before and After sit weeks and a day away from it, so a RefusedFrom that slipped
     * by a day, or ">" written for ">=", would pass them.
     */
    [Theory]
    [InlineData("{\"packageId\":\"MyExt\"}")]                        // no list at all
    [InlineData("{\"reservedFunctionality\":{\"none\":true}}")]      // the earlier block
    public void TheLastDayOfOctoberWarnsAndTheFirstOfNovemberRefuses(string manifest)
    {
        var d = Of(manifest);
        Assert.False(Assert.Single(ScheduleA.Findings(d, new DateOnly(2026, 10, 31))).Blocking);
        Assert.True(Assert.Single(ScheduleA.Findings(d, new DateOnly(2026, 11, 1))).Blocking);
    }

    [Fact]
    public void TheTemplatesEmptyBlockIsNoAnswer() =>
        Assert.Null(Of("{\"reservedFunctionality\":{\"none\":false,\"domain\":\"\",\"entitlement\":\"\",\"confirmations\":[]}}"));

    [Fact]
    public void AListThatIsNoListIsRefused() =>
        Assert.Contains(ScheduleA.Findings(Of("{\"reservedDomains\":\"none\"}"), Before), f => f.Blocking);

    /** The store refuses such an entry on every date, so it stops the publish here too. */
    [Fact]
    public void AnEntryWithoutItsLicenceIsTold() =>
        Assert.Contains(ScheduleA.Findings(Of("{\"reservedDomains\":[{\"domain\":\"A-05\"}]}"), Before),
                        f => f.Blocking && f.Text.Contains("entitlement"));

    [Fact]
    public void AnEntryWithoutItsAreaIsRefused() =>
        Assert.Contains(ScheduleA.Findings(Of("{\"reservedDomains\":[{\"entitlement\":\"ENCY Nesting\"}]}"), Before),
                        f => f.Blocking && f.Text.Contains("domain"));

    /** Written, "capabilities" is a list of identifiers as strings; the store refuses anything else on every date. */
    [Theory]
    [InlineData("\"nesting.layout\"")]
    [InlineData("[1]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"  \"]")]
    public void CapabilitiesThatAreNotAListOfIdentifiersAreRefused(string capabilities)
    {
        var f = Assert.Single(ScheduleA.Findings(Of(
            "{\"reservedDomains\":[{\"domain\":\"A-05\",\"entitlement\":\"ENCY Nesting\",\"capabilities\":" + capabilities + "}]}"), Before));
        Assert.True(f.Blocking);
        Assert.Contains("\"capabilities\"", f.Text);
        Assert.Contains("reservedDomains", f.Text);
    }

    /** Left out or null, "capabilities" is simply none - as the store reads it. */
    [Fact]
    public void CapabilitiesLeftOutOrNullAreNone()
    {
        Assert.Empty(ScheduleA.Findings(Of("{\"reservedDomains\":[{\"domain\":\"A-05\",\"entitlement\":\"ENCY Nesting\"}]}"), After));
        Assert.Empty(ScheduleA.Findings(Of("{\"reservedDomains\":[{\"domain\":\"A-05\",\"entitlement\":\"ENCY Nesting\",\"capabilities\":null}]}"), After));
    }
}
