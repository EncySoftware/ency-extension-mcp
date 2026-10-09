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
