using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// Linux OpenCode CLI discovery and the CLI resolution cache.
///
///   LinuxOpenCodeDiscovery.FindCli(string home, IReadOnlyList&lt;string&gt; pathDirectories,
///                                  ILinuxCliHost? host = null) -&gt; string?
///
/// <c>ILinuxCliHost</c> is the OS seam: environment lookup, ordinary-executable validation and a
/// bounded directory listing. Tests inject a fixture so no real HOME or install is touched.
/// </summary>
public class LinuxOpenCodeDiscoveryTests
{
    private const string Home = "/home/u";
    private const string BundledCli = "/opt/OpenCode/resources/opencode-cli";

    // ── FindCli ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindCliFindsAnExecutableOnPath()
    {
        var host = new FakeHost();
        host.Executables["/usr/local/bin/opencode"] = "/usr/local/bin/opencode";

        // /usr/bin has nothing; /usr/local/bin supplies opencode.
        var found = LinuxOpenCodeDiscovery.FindCli(Home, new[] { "/usr/bin", "/usr/local/bin" }, host);

        Assert.Equal("/usr/local/bin/opencode", found);
    }

    [Fact]
    public void FindCliAcceptsTheOpencodeCliAliasOnPath()
    {
        var host = new FakeHost();
        host.Executables["/usr/local/bin/opencode-cli"] = "/usr/local/bin/opencode-cli";

        var found = LinuxOpenCodeDiscovery.FindCli(Home, new[] { "/usr/local/bin" }, host);

        Assert.Equal("/usr/local/bin/opencode-cli", found);
    }

    [Fact]
    public void FindCliPrefersPathOverAUserBin()
    {
        var host = new FakeHost();
        host.Executables["/usr/bin/opencode"] = "/usr/bin/opencode";
        host.Executables["/home/u/.local/bin/opencode"] = "/home/u/.local/bin/opencode";

        Assert.Equal("/usr/bin/opencode", LinuxOpenCodeDiscovery.FindCli(Home, new[] { "/usr/bin" }, host));
    }

    [Fact]
    public void FindCliFallsBackToTheUserBinWhenPathHasNothing()
    {
        var host = new FakeHost();
        host.Executables["/home/u/.opencode/bin/opencode"] = "/home/u/.opencode/bin/opencode";

        Assert.Equal(
            "/home/u/.opencode/bin/opencode",
            LinuxOpenCodeDiscovery.FindCli(Home, new[] { "/usr/bin", "/bin" }, host));
    }

    /// <summary>A candidate that exists but is not an ordinary executable (missing exec bit, a
    /// directory, ...) must be skipped, never returned as the CLI.</summary>
    [Fact]
    public void FindCliSkipsCandidatesThatAreNotOrdinaryExecutables()
    {
        var host = new FakeHost();
        // /usr/bin/opencode exists but ResolveExecutable reports it is not an ordinary executable
        // (so it is absent from Executables); the next PATH directory supplies a usable one.
        host.Executables["/opt/tools/opencode"] = "/opt/tools/opencode";

        Assert.Equal("/opt/tools/opencode", LinuxOpenCodeDiscovery.FindCli(Home, new[] { "/usr/bin", "/opt/tools" }, host));
    }

    /// <summary>An npm/package-manager symlink must be accepted, but the validated *final* target
    /// is returned (the seam resolves the link and confirms a regular executable).</summary>
    [Fact]
    public void FindCliReturnsTheResolvedFinalTargetForASymlink()
    {
        var host = new FakeHost();
        host.Executables["/home/u/.local/bin/opencode"] = "/opt/npm/opencode/1.2.3/opencode-cli";

        var found = LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host);

        Assert.Equal("/opt/npm/opencode/1.2.3/opencode-cli", found);
    }

    [Fact]
    public void FindCliSelectsTheHighestVersionedXdgCacheCli()
    {
        var host = new FakeHost();
        var root = "/home/u/.local/share/ai.opencode.desktop/cli";
        host.Directories[root] = new[] { "1.0.0", "2.0.24" };
        host.Executables[root + "/2.0.24/opencode-cli"] = root + "/2.0.24/opencode-cli";

        var found = LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host);

        Assert.Equal(root + "/2.0.24/opencode-cli", found);
    }

    /// <summary>A newest cache entry whose file was deleted/updated away must not stick: discovery
    /// falls through to the next still-usable version.</summary>
    [Fact]
    public void FindCliFallsBackToAnOlderVersionWhenTheNewestCacheEntryIsInvalid()
    {
        var host = new FakeHost();
        var root = "/home/u/.config/ai.opencode.desktop/cli";
        host.Directories[root] = new[] { "1.0.0", "2.0.24" };
        // 2.0.24 is not mapped, i.e. its opencode-cli no longer resolves to a regular executable.
        host.Executables[root + "/1.0.0/opencode-cli"] = root + "/1.0.0/opencode-cli";

        var found = LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host);

        Assert.Equal(root + "/1.0.0/opencode-cli", found);
    }

    /// <summary>Review #5: the highest version must be chosen across *both* XDG roots, not per root.
    /// A stale data-root CLI must not win over a newer config-root CLI.</summary>
    [Fact]
    public void FindCliChoosesTheHighestVersionAcrossAllXdgRoots()
    {
        var host = new FakeHost();
        var dataRoot = "/home/u/.local/share/ai.opencode.desktop/cli";
        var configRoot = "/home/u/.config/ai.opencode.desktop/cli";
        host.Directories[dataRoot] = new[] { "1.0.0" };
        host.Directories[configRoot] = new[] { "2.0.0" };
        host.Executables[dataRoot + "/1.0.0/opencode-cli"] = dataRoot + "/1.0.0/opencode-cli";
        host.Executables[configRoot + "/2.0.0/opencode-cli"] = configRoot + "/2.0.0/opencode-cli";

        var found = LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host);

        Assert.Equal(configRoot + "/2.0.0/opencode-cli", found);
    }

    [Fact]
    public void FindCliFindsTheBundledResourceCli()
    {
        var host = new FakeHost();
        host.Executables[BundledCli] = BundledCli;

        Assert.Equal(BundledCli, LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host));
    }

    [Fact]
    public void FindCliReturnsNullWhenNoCandidateIsAUsableExecutable()
    {
        var host = new FakeHost();
        host.Directories["/home/u/.local/share/ai.opencode.desktop/cli"] = new[] { "2.0.24" };

        Assert.Null(LinuxOpenCodeDiscovery.FindCli(Home, new[] { "/usr/bin" }, host));
    }

    /// <summary>An XDG_DATA_HOME override must be honored through the host seam, without mutating
    /// the process-global environment from a test.</summary>
    [Fact]
    public void FindCliHonorsXdgDataHomeThroughTheHost()
    {
        var host = new FakeHost();
        var root = "/custom/data/ai.opencode.desktop/cli";
        host.Environment["XDG_DATA_HOME"] = "/custom/data";
        host.Directories[root] = new[] { "3.1.4" };
        host.Executables[root + "/3.1.4/opencode-cli"] = root + "/3.1.4/opencode-cli";

        Assert.Equal(root + "/3.1.4/opencode-cli", LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host));
    }

    /// <summary>Review #5: only *absolute* XDG overrides are honored. A relative override must be
    /// ignored entirely, falling back to the home default.</summary>
    [Fact]
    public void FindCliIgnoresARelativeXdgDataHome()
    {
        var host = new FakeHost();
        host.Environment["XDG_DATA_HOME"] = "relative/data";
        host.Directories["relative/data/ai.opencode.desktop/cli"] = new[] { "9.9.9" };
        host.Executables["relative/data/ai.opencode.desktop/cli/9.9.9/opencode-cli"] =
            "relative/data/ai.opencode.desktop/cli/9.9.9/opencode-cli";

        var homeDefault = "/home/u/.local/share/ai.opencode.desktop/cli";
        host.Directories[homeDefault] = new[] { "1.0.0" };
        host.Executables[homeDefault + "/1.0.0/opencode-cli"] = homeDefault + "/1.0.0/opencode-cli";

        Assert.Equal(homeDefault + "/1.0.0/opencode-cli", LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host));
    }

    [Fact]
    public void FindCliIgnoresARelativeXdgConfigHome()
    {
        var host = new FakeHost();
        host.Environment["XDG_CONFIG_HOME"] = "relative/config";
        host.Directories["relative/config/ai.opencode.desktop/cli"] = new[] { "9.9.9" };
        host.Executables["relative/config/ai.opencode.desktop/cli/9.9.9/opencode-cli"] =
            "relative/config/ai.opencode.desktop/cli/9.9.9/opencode-cli";

        var homeDefault = "/home/u/.config/ai.opencode.desktop/cli";
        host.Directories[homeDefault] = new[] { "1.0.0" };
        host.Executables[homeDefault + "/1.0.0/opencode-cli"] = homeDefault + "/1.0.0/opencode-cli";

        Assert.Equal(homeDefault + "/1.0.0/opencode-cli", LinuxOpenCodeDiscovery.FindCli(Home, Array.Empty<string>(), host));
    }

    /// <summary>Test double for the production OS boundary <c>ILinuxCliHost</c>.</summary>
    private sealed class FakeHost : ILinuxCliHost
    {
        public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Executables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<string>?> Directories { get; } = new(StringComparer.Ordinal);

        public string? GetEnvironmentVariable(string name)
            => Environment.TryGetValue(name, out var value) ? value : null;

        public string? ResolveExecutable(string path)
            => Executables.TryGetValue(path, out var resolved) ? resolved : null;

        public IReadOnlyList<string>? ListDirectory(string directory)
            => Directories.TryGetValue(directory, out var entries) ? entries : null;
    }
}

/// <summary>
/// The CLI resolution cache must not keep returning a path that disappeared
/// (or that a package manager replaced), while a healthy cached path is not re-resolved.
///
/// Seam under test (see task-3-report.md):
///   internal interface IOpenCodeCliLocator { string? Locate(); bool IsUsable(string path); }
///   internal OpenCodeCli(OpenCodeCliOptions, SecretRegistry, AppLog?, IOpenCodeCliLocator?)
/// </summary>
public class OpenCodeCliResolutionTests
{
    [Fact]
    public void HealthyCachedCliIsNotReResolved()
    {
        var locator = new FakeLocator("/opt/a/opencode", "/opt/b/opencode");
        var cli = new OpenCodeCli(new OpenCodeCliOptions(), new SecretRegistry(false), null, locator);

        Assert.Equal("/opt/a/opencode", cli.ResolveCli());
        Assert.Equal("/opt/a/opencode", cli.ResolveCli());
        Assert.Equal(1, locator.Locates);
    }

    [Fact]
    public void DeletedCachedCliIsReResolvedOnTheNextCall()
    {
        var locator = new FakeLocator("/opt/a/opencode", "/opt/b/opencode");
        var cli = new OpenCodeCli(new OpenCodeCliOptions(), new SecretRegistry(false), null, locator);
        Assert.Equal("/opt/a/opencode", cli.ResolveCli());

        // The cached path is gone (uninstalled / replaced by an update).
        locator.Usable = path => !string.Equals(path, "/opt/a/opencode", StringComparison.Ordinal);

        Assert.Equal("/opt/b/opencode", cli.ResolveCli());
        Assert.Equal(2, locator.Locates);
    }

    [Fact]
    public void ClearingTheCliOverrideDropsTheCacheAndReResolves()
    {
        var locator = new FakeLocator("/opt/auto/opencode");
        var cli = new OpenCodeCli(new OpenCodeCliOptions(), new SecretRegistry(false), null, locator);

        cli.SetCliPath("/opt/override/opencode");
        Assert.Equal("/opt/override/opencode", cli.ResolveCli());

        cli.SetCliPath(null);
        Assert.Equal("/opt/auto/opencode", cli.ResolveCli());
    }

    [Fact]
    public void AnOverrideThatDisappearedFailsClosedInsteadOfUsingAStalePath()
    {
        var locator = new FakeLocator("/opt/auto/opencode");
        locator.Usable = path => !string.Equals(path, "/opt/override/opencode", StringComparison.Ordinal);
        var cli = new OpenCodeCli(new OpenCodeCliOptions { CliPath = "/opt/override/opencode" }, new SecretRegistry(false), null, locator);

        Action act = () => cli.ResolveCli();
        Assert.Throws<OpenCodeException>(act);
        Assert.Equal(0, locator.Locates);
    }

    private sealed class FakeLocator : IOpenCodeCliLocator
    {
        private readonly Queue<string?> _paths;
        public FakeLocator(params string?[] paths) => _paths = new Queue<string?>(paths);
        public Func<string, bool> Usable { get; set; } = _ => true;
        public int Locates { get; private set; }

        public string? Locate()
        {
            Locates++;
            return _paths.Count > 0 ? _paths.Dequeue() : null;
        }

        public bool IsUsable(string path) => Usable(path);
    }
}
