using EncyExtensionMcp;
using Xunit;

/**
 * Whether a version tag push starts a workflow, read from the structure of its `on:` block the way
 * GitHub reads it — not from a `tags:` line anywhere in the file, which a docker step's `with:` has too,
 * and which a bare `on: push` lacks while it runs on every tag (review, 09.10.2026).
 */
public class WorkflowTriggerTests
{
    private const string Job = "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n";

    [Theory]
    // a string, a list, a list in block form
    [InlineData("on: push\n", true)]
    [InlineData("on: [push, workflow_dispatch]\n", true)]
    [InlineData("on:\n  - pull_request\n  - push\n", true)]
    [InlineData("on: workflow_dispatch\n", false)]
    [InlineData("on: [pull_request]\n", false)]
    // push with nothing under it, or only path filters: path filters are not applied to tag pushes
    [InlineData("on:\n  push:\n  workflow_dispatch:\n", true)]
    [InlineData("on:\n  push: {}\n", true)]
    [InlineData("on:\n  push:\n    paths: ['src/**']\n", true)]
    [InlineData("on:\n  push:\n    paths-ignore:\n      - docs/**\n", true)]
    // tags decide by matching the tag
    [InlineData("on:\n  push:\n    tags: ['v*']\n", true)]
    [InlineData("on:\n  push:\n    tags:\n      - 'v*'\n  workflow_dispatch:\n", true)]
    [InlineData("on:\n  push:\n    tags:\n    - v[0-9]+.[0-9]+.[0-9]+\n", true)]
    [InlineData("on:\n  push:\n    tags: v*\n", true)]
    [InlineData("on:\n  push:\n    tags: ['release-*']\n", false)]
    [InlineData("on:\n  push:\n    tags: ['v*', '!v*']\n", false)]
    [InlineData("on:\n  push:\n    branches: [main]\n    tags: ['v*']\n", true)]
    // tags-ignore: every tag it does not match
    [InlineData("on:\n  push:\n    tags-ignore: ['v*']\n", false)]
    [InlineData("on:\n  push:\n    tags-ignore: ['nightly-*']\n", true)]
    // only branches: never a tag
    [InlineData("on:\n  push:\n    branches: [main]\n", false)]
    [InlineData("on:\n  push:\n    branches-ignore:\n      - 'dependabot/**'\n", false)]
    [InlineData("on:\n  push:\n    branches: [main]\n    paths: ['src/**']\n", false)]
    // create runs for every new tag
    [InlineData("on: [create]\n", true)]
    // flow maps, a quoted key, comments
    [InlineData("on: {push: {tags: ['v*']}}\n", true)]
    [InlineData("on: {push: {branches: [main]}}\n", false)]
    [InlineData("\"on\":\n  push:\n    tags: [\"v*\"]\n", true)]
    [InlineData("on:   # what starts it\n  push:   # tags below\n    tags: ['v*']   # versions\n", true)]
    [InlineData("# on: push\non: workflow_dispatch\n", false)]
    public void A_version_tag_push_starts_a_workflow_by_the_structure_of_its_on_block(string on, bool starts) =>
        Assert.Equal(starts, StoreWorkflow.StartsOnVersionTag("name: x\n" + on + Job));

    /** The case the line search got wrong both ways: a docker step's tags, and a bare push. */
    [Fact]
    public void A_tags_key_outside_the_on_block_is_no_trigger_and_a_bare_push_is_one()
    {
        const string docker = "name: image\non: workflow_dispatch\njobs:\n  image:\n    runs-on: ubuntu-latest\n    steps:\n"
                            + "      - uses: docker/build-push-action@v6\n        with:\n          push: true\n          tags: acme/app:latest\n";
        Assert.False(StoreWorkflow.StartsOnVersionTag(docker));
        Assert.True(StoreWorkflow.StartsOnVersionTag("name: ci\non:\n  push:\n" + Job));
    }

    /** The other workflows of DirectCladdingOperation (v3/main), as they are. */
    [Fact]
    public void The_nuget_workflows_of_a_real_repository_start_on_a_version_tag()
    {
        const string prod = "name: Publish to NuGet\r\n\r\non:\r\n  push:\r\n    tags:\r\n      - 'v*'\r\n  workflow_dispatch:\r\n\r\n"
                          + "jobs:\r\n  publish:\r\n    runs-on: windows-latest\r\n    defaults:\r\n      run:\r\n        working-directory: ./.stbuild\r\n";
        Assert.True(StoreWorkflow.StartsOnVersionTag(prod));
        Assert.True(StoreWorkflow.StartsOnVersionTag(StoreWorkflow.Render("a/b.csproj", "10.0.x")));
    }

    [Fact]
    public void What_cannot_be_read_is_no_claim()
    {
        Assert.False(StoreWorkflow.StartsOnVersionTag(""));
        Assert.False(StoreWorkflow.StartsOnVersionTag("name: x\njobs: {}\n"));
        Assert.False(StoreWorkflow.StartsOnVersionTag("on:\n  push:\n    tags: ['v*'\n"));   // never closed
        // ...and told apart from "no": a workflow without on: runs for nothing, one this reader cannot take apart is unknown.
        Assert.Equal(false, StoreWorkflow.TagTrigger("name: x\njobs: {}\n"));
        Assert.Equal(false, StoreWorkflow.TagTrigger("on: workflow_dispatch\n"));
        Assert.Null(StoreWorkflow.TagTrigger("on:\n  push:\n    tags: ['v*'\n"));
    }

    /**
     * YAML's implicit one-pair map in a flow list — `schedule: [cron: '0 6 * * 1']` is valid GitHub — held
     * the reader at the ':' and grew its list until the process ran out of memory; connect_to_store never
     * answered (review, 09.10.2026). Each must come back, and at once.
     */
    [Theory]
    [InlineData("on:\n  schedule: [cron: '0 6 * * 1']\n  workflow_dispatch:\n", false)]
    [InlineData("on: [a: b]\n", false)]
    [InlineData("on:\n  push:\n    branches: [main, release: x]\n", false)]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      env:\n        options: [prod: live, test]\n  push:\n    tags: ['v*']\n", true)]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      env:\n        default: [a: b]\n", false)]
    [InlineData("on: [push, {pull_request: x}, c: d]\n", true)]
    [InlineData("on:\n  push:\n    tags: {a: b: c}\n", false)]
    public void A_pair_inside_a_flow_list_is_read_and_never_holds_the_reader(string on, bool starts)
    {
        var read = Task.Run(() => StoreWorkflow.StartsOnVersionTag("name: x\n" + on + Job));
        Assert.True(read.Wait(TimeSpan.FromSeconds(10)), "the reader did not come back");
        Assert.Equal(starts, read.Result);
    }

    /**
     * Read past, not stopped at: a block scalar (a workflow_dispatch input's description over several
     * lines), an anchor, a tab after the colon, a value on the line under its key, plain and quoted
     * scalars over several lines. Each used to read as "no tag trigger".
     */
    [Theory]
    [InlineData("on:\n  push:\n    tags: ['v*']\n  workflow_dispatch:\n    inputs:\n      v:\n        description: |\n          multi\n          line\n        required: true\n")]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      v:\n        description: >-\n          folded: text\n          - not a list\n  push:\n    tags: ['v*']\n")]
    [InlineData("on:\n  push:\n    tags: &versions ['v*']\n")]
    [InlineData("on:\n  push: &p\n    tags: ['v*']\n")]
    [InlineData("on:\n  push:\n    tags:\t['v*']\n")]
    [InlineData("on:\n  push\n")]
    [InlineData("on:\n  [push, workflow_dispatch]\n")]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      v:\n        description: a description that goes on\n          over the next line\n  push:\n    tags: ['v*']\n")]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      v:\n        description: 'a quoted one that goes on\n          over the next line'\n  push:\n    tags: ['v*']\n")]
    public void Block_scalars_anchors_tabs_and_values_over_lines_are_read_past(string on) =>
        Assert.Equal(true, StoreWorkflow.TagTrigger("name: x\n" + on + Job));

    /** An alias is not followed: where it decides, nothing is claimed; where it does not, it is no obstacle. */
    [Fact]
    public void An_alias_decides_nothing()
    {
        Assert.Null(StoreWorkflow.TagTrigger("on:\n  push:\n    tags: *versions\n" + Job));
        Assert.Null(StoreWorkflow.TagTrigger("on:\n  push: *p\n" + Job));
        Assert.Null(StoreWorkflow.TagTrigger("on:\n  push:\n    tags: ['v1*', *more]\n" + Job));
        Assert.Equal(true, StoreWorkflow.TagTrigger("on:\n  workflow_dispatch:\n    inputs:\n      v:\n        default: *d\n  push:\n    tags: ['v*']\n" + Job));
        Assert.Null(StoreWorkflow.TagTrigger("on:\n  push:\n    tags: |\n      v*\n" + Job));   // a block scalar where the patterns go
    }

    /** Run workflow is offered for workflow_dispatch only. */
    [Theory]
    [InlineData("on: workflow_dispatch\n", true)]
    [InlineData("on:\n  push:\n    tags: ['v*']\n  workflow_dispatch:\n    inputs: {}\n", true)]
    [InlineData("on: push\n", false)]
    [InlineData("on: [push, *x]\n", null)]
    public void Run_workflow_is_offered_for_workflow_dispatch(string on, bool? byHand) =>
        Assert.Equal(byHand, StoreWorkflow.RunsByHand("name: x\n" + on + Job));

    /** The Actions tab lists a workflow by its name, and how it starts is said from its on: block. */
    [Fact]
    public void A_workflow_is_named_and_its_start_said_the_way_GitHub_shows_them()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mcp-wf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string File(string name, string yaml) { string f = Path.Combine(dir, name); System.IO.File.WriteAllText(f, yaml); return f; }
            string both = File("both.yml", "name: Release to the store   # shown in Actions\r\non:\r\n  push:\r\n    tags: ['v*']\r\n  workflow_dispatch:\r\n" + Job);
            string tag = File("tag.yml", "on: push\n" + Job);
            string hand = File("hand.yml", "name: 'By hand'\non: workflow_dispatch\n" + Job);
            string neither = File("neither.yml", "on: [pull_request]\n" + Job);

            Assert.Equal("Release to the store", StoreWorkflow.DisplayName(both));
            Assert.Equal("tag.yml", StoreWorkflow.DisplayName(tag));
            Assert.Equal("By hand", StoreWorkflow.DisplayName(hand));
            Assert.Equal("a version tag (vX.Y.Z), or Actions -> \"Release to the store\" -> Run workflow on GitHub", StoreWorkflow.HowItStarts(both));
            Assert.Equal("a version tag (vX.Y.Z)", StoreWorkflow.HowItStarts(tag));
            Assert.Equal("Actions -> \"By hand\" -> Run workflow on GitHub", StoreWorkflow.HowItStarts(hand));
            Assert.Equal("the way its on: block says", StoreWorkflow.HowItStarts(neither));
            Assert.Equal(new[] { "both.yml", "tag.yml" }, StoreWorkflow.TagWorkflows(dir).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("v*", "v1.2.3", true)]
    [InlineData("v*", "release/v1", false)]       // * stops at '/'
    [InlineData("**", "release/v1", true)]
    [InlineData("v1.?0", "v10", true)]            // ? is "zero or one of the character before", here '.'
    [InlineData("v1.?0", "v1..0", false)]
    [InlineData("v[0-9]+", "v123", true)]
    [InlineData("v[0-9]+", "v1a", false)]
    [InlineData("v\\*", "v*", true)]
    [InlineData("v\\*", "v1", false)]
    public void Filter_patterns_match_the_way_GitHub_documents_them(string pattern, string name, bool matches) =>
        Assert.Equal(matches, StoreWorkflow.FilterMatches(new[] { pattern }, name));

    [Fact]
    public void A_negative_pattern_takes_back_what_a_positive_one_before_it_matched()
    {
        Assert.False(StoreWorkflow.FilterMatches(new[] { "v*", "!v*-rc*" }, "v1.0.0-rc1"));
        Assert.True(StoreWorkflow.FilterMatches(new[] { "v*", "!v*-rc*" }, "v1.0.0"));
        Assert.True(StoreWorkflow.FilterMatches(new[] { "!v*", "v1*" }, "v1.0.0"));   // a later positive one wins
    }
}
