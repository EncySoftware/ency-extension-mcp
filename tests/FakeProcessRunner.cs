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

    /** Что стор советует собирать; null = стор не ответил. */
    public string? RecommendedSdk { get; set; } = "3.0.8";

    public Task<StoreCard?> GetCard(string slugOrPackageId) => Task.FromResult(Card);
    public Task<string?> GetRecommendedSdk() => Task.FromResult(RecommendedSdk);
    public Task<IReadOnlyList<StoreCategory>> GetCategories() =>
        Task.FromResult<IReadOnlyList<StoreCategory>>(Categories);

    public Task<string?> ClaimPackage(string packageId, string repository, string accessToken)
    {
        Claims.Add((packageId, repository));
        return Task.FromResult(ClaimFailure);
    }

    // ---- the GitHub App route, scripted: queues answer in order, then the default holds
    public AppStatus AppStatusDefault { get; set; } = new(true, new List<string> { "andrew-l" }, new List<AppRepo>());
    public Queue<AppStatus> AppStatuses { get; } = new();
    public string InstallUrl { get; set; } = "https://github.com/apps/ency-extension-store/installations/new?state=s";
    public int InstallUrlAsked { get; private set; }
    public string? CreateRepoFailure { get; set; }
    public List<string> CreatedRepos { get; } = new();
    public RepoState RepoStateDefault { get; set; } = new("ready", false, "main");
    public Queue<RepoState> RepoStates { get; } = new();
    public string? UploadFailure { get; set; }
    public List<(string PackageId, IReadOnlyList<SourceFile> Files)> Uploads { get; } = new();
    public string? RunFailure { get; set; }
    public List<string> Runs { get; } = new();
    public IReadOnlyList<BuildReport> BuildsDefault { get; set; } = Array.Empty<BuildReport>();
    public Queue<IReadOnlyList<BuildReport>> Builds { get; } = new();

    public Task<AppStatus> GetAppStatus(string accessToken) =>
        Task.FromResult(AppStatuses.Count > 0 ? AppStatuses.Dequeue() : AppStatusDefault);
    public Task<string> GetAppInstallUrl(string accessToken) { InstallUrlAsked++; return Task.FromResult(InstallUrl); }
    public Task<AppRepo> CreateRepository(string packageId, string accessToken)
    {
        if (CreateRepoFailure != null) throw new StoreApiException(409, CreateRepoFailure);
        CreatedRepos.Add(packageId);
        return Task.FromResult(new AppRepo(packageId, "andrew-l/" + packageId, "https://github.com/andrew-l/" + packageId));
    }
    public Task<RepoState> GetRepoState(string packageId, string accessToken) =>
        Task.FromResult(RepoStates.Count > 0 ? RepoStates.Dequeue() : RepoStateDefault);
    public Task<SourcesUploaded> UploadSources(string packageId, IReadOnlyList<SourceFile> files, string accessToken)
    {
        if (UploadFailure != null) throw new StoreApiException(400, UploadFailure);
        Uploads.Add((packageId, files));
        return Task.FromResult(new SourcesUploaded("andrew-l/" + packageId, "c0ffee1234567",
            "https://github.com/andrew-l/" + packageId + "/commit/c0ffee1234567", files.Count));
    }
    public Task<RunStarted> StartRun(string packageId, string accessToken)
    {
        if (RunFailure != null) throw new StoreApiException(502, RunFailure);
        Runs.Add(packageId);
        return Task.FromResult(new RunStarted("andrew-l/" + packageId, "https://github.com/andrew-l/" + packageId + "/actions"));
    }
    public Task<IReadOnlyList<BuildReport>> GetMyBuilds(string accessToken) =>
        Task.FromResult(Builds.Count > 0 ? Builds.Dequeue() : BuildsDefault);
}

/** Sign-in as a test sees it: a token or none, and a browser login that either works or does not. */
public class FakeStoreAuth : IStoreAuth
{
    public string? Token { get; set; } = "tok";
    public bool LoginSucceeds { get; set; } = true;
    public int LoginCalls { get; private set; }
    public Task<string?> GetAccessToken() => Task.FromResult(Token);
    public Task<bool> LoginBrowser(Action<string> log)
    {
        LoginCalls++;
        if (LoginSucceeds) Token = "tok-after-login";
        return Task.FromResult(LoginSucceeds);
    }
}
