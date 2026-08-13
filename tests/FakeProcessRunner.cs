using EncyExtensionMcp;

/// <summary>Scripted process runner: match by "file args-prefix", record every call.</summary>
public class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string Prefix, ProcResult Result)> _rules = new();
    public List<string> Calls { get; } = new();

    public FakeProcessRunner On(string prefix, int exit = 0, string stdout = "", string stderr = "")
    {
        _rules.Add((prefix, new ProcResult(exit, stdout, stderr)));
        return this;
    }

    public Task<ProcResult> Run(string fileName, string arguments, string? workingDir = null,
        IDictionary<string, string>? env = null, int timeoutSeconds = 120)
    {
        var call = $"{fileName} {arguments}";
        Calls.Add(call);
        foreach (var (prefix, result) in _rules)
            if (call.StartsWith(prefix)) return Task.FromResult(result);
        return Task.FromResult(new ProcResult(1, "", $"no fake rule for: {call}"));
    }
}

public class FakeStoreClient : IStoreClient
{
    public StoreCard? Card { get; set; }
    /** Set to a message to make the claim fail, as the store would when the name is taken. */
    public string? ClaimFailure { get; set; }
    public List<(string PackageId, string Repository)> Claims { get; } = new();

    public string StoreBaseUrl => "https://store.test";
    public List<StoreCategory> Categories { get; } = new()
    {
        new StoreCategory("other", "Other"),
        new StoreCategory("analyzer", "Analyzer"),
        new StoreCategory("operation", "Operation"),
    };

    public Task<StoreCard?> GetCard(string slugOrPackageId) => Task.FromResult(Card);
    public Task<IReadOnlyList<StoreCategory>> GetCategories() =>
        Task.FromResult<IReadOnlyList<StoreCategory>>(Categories);

    public Task<string?> ClaimPackage(string packageId, string repository, string accessToken)
    {
        Claims.Add((packageId, repository));
        return Task.FromResult(ClaimFailure);
    }
}
