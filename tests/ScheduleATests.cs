using EncyExtensionMcp;
using Xunit;

namespace EncyExtensionMcp.Tests;

/**
 * The Schedule A declaration in package.info.json (legal brief of 18.09.2026). The store decides —
 * it holds the domain codes and the Entitlement names — so what is checked here is only what the
 * file itself shows: no block, or the empty one the template ships, before it costs a red build.
 */
public class ScheduleATests
{
    private static readonly DateOnly Before = new(2026, 10, 15);
    private static readonly DateOnly After = new(2026, 11, 1);

    private static ScheduleADeclaration? Read(string block) =>
        ScheduleA.ReadJson($"{{\"packageId\":\"MyExt\",\"reservedFunctionality\":{block}}}");

    [Fact]
    public void AManifestWithoutTheBlockIsAWarningUntilTheDocumentsTakeEffect()
    {
        var d = ScheduleA.ReadJson("{\"packageId\":\"MyExt\"}");
        Assert.Null(d);

        var before = ScheduleA.Findings(d, Before).ToList();
        Assert.Single(before);
        Assert.False(before[0].Blocking);
        Assert.Contains("2026-11-01", before[0].Text);
        Assert.Contains("ask them, do not answer it for them", before[0].Text);

        var after = ScheduleA.Findings(d, After).ToList();
        Assert.True(after[0].Blocking, "from the effective date the store refuses it, and so must we");
    }

    [Fact]
    public void TheTemplatesEmptyBlockIsNotAnAnswer()
    {
        var d = Read("""{"see":"https://x","none":false,"domain":"","entitlement":"","confirmations":[]}""");
        Assert.NotNull(d);
        Assert.True(d!.Unanswered);
        Assert.Contains("still unanswered", ScheduleA.Findings(d, Before).Single().Text);
        Assert.True(ScheduleA.Findings(d, After).Single().Blocking);
    }

    [Fact]
    public void BothAnswersAreAccepted()
    {
        var none = Read("""{"none":true,"confirmations":["4.2","4.9","4.6"]}""");
        Assert.True(none!.None);
        Assert.False(none.Unanswered);
        Assert.Empty(ScheduleA.Findings(none, After));

        var word = Read("""{"domain":"none","confirmations":["4.2","4.9","4.6"]}""");
        Assert.True(word!.None);
        Assert.Null(word.Domain);
        Assert.Empty(ScheduleA.Findings(word, After));

        var domain = Read("""{"domain":"A-05","entitlement":"Nesting","confirmations":["4.2","4.9","4.6"]}""");
        Assert.Equal("A-05", domain!.Domain);
        Assert.Equal("Nesting", domain.Entitlement);
        Assert.Empty(ScheduleA.Findings(domain, After));
    }

    /** Started answering is answered: the gaps get their own sentence, never "declare something". */
    [Fact]
    public void AnIncompleteAnswerIsToldWhatItLacks()
    {
        var noEntitlement = Read("""{"domain":"A-05","confirmations":["4.2","4.9","4.6"]}""");
        var says = ScheduleA.Findings(noEntitlement, After).ToList();
        Assert.Single(says);
        Assert.False(says[0].Blocking, "the store has the last word on domains and entitlements");
        Assert.Contains("names domain A-05 and no `entitlement`", says[0].Text);

        var twoOfThree = Read("""{"none":true,"confirmations":["4.2","4.9"]}""");
        Assert.Contains("does not confirm Policy paragraph 4.6", ScheduleA.Findings(twoOfThree, Before).Single().Text);

        var noneOfThree = Read("""{"none":true,"confirmations":[]}""");
        Assert.Contains("does not confirm Policy paragraphs 4.2, 4.9, 4.6",
                        ScheduleA.Findings(noneOfThree, Before).Single().Text);
    }
}
