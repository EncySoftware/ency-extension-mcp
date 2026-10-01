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

    [Fact]
    public void TheTemplatesEmptyBlockIsNoAnswer() =>
        Assert.Null(Of("{\"reservedFunctionality\":{\"none\":false,\"domain\":\"\",\"entitlement\":\"\",\"confirmations\":[]}}"));

    [Fact]
    public void AListThatIsNoListIsRefused() =>
        Assert.Contains(ScheduleA.Findings(Of("{\"reservedDomains\":\"none\"}"), Before), f => f.Blocking);

    [Fact]
    public void AnEntryWithoutItsLicenceIsTold() =>
        Assert.Contains(ScheduleA.Findings(Of("{\"reservedDomains\":[{\"domain\":\"A-05\"}]}"), Before),
                        f => f.Text.Contains("entitlement"));
}
