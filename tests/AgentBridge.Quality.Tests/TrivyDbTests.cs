using System.Diagnostics;

namespace AgentBridge.Quality.Tests;

// .github/actions/install-trivy/download-db.ps1, which install-trivy runs before ci.yml's relay
// image scan, against a stand-in for Trivy. It starts pwsh, so ring 0 leaves it to ring 1.
[Trait("Category", "Process")]
public class TrivyDbTests
{
    const string Mirror = "mirror.gcr.io/aquasec/trivy-db:2";
    const string Ghcr = "ghcr.io/aquasecurity/trivy-db:2";

    // Trivy's --download-db-only: it records the repository it is given, then either fails the
    // way mirror.gcr.io did on main (run 42031392354) or writes the database into the cache.
    const string FakeTrivy = """
        $repo = $args[[array]::IndexOf($args, '--db-repository') + 1]
        $cache = $args[[array]::IndexOf($args, '--cache-dir') + 1]
        Add-Content -LiteralPath $env:FAKE_TRIVY_CALLS -Value ($args -join ' ')
        if (($env:FAKE_TRIVY_FAIL -split ',') -contains $repo) {
            [Console]::Error.WriteLine("FATAL failed to download artifact from ${repo}: oci download error: failed to fetch the layer: unexpected status code 404 Not Found")
            exit 1
        }
        New-Item -ItemType Directory -Force (Join-Path $cache 'db') | Out-Null
        Set-Content -LiteralPath (Join-Path $cache 'db' 'trivy.db') -Value 'db'
        # Like a native Trivy's exit status: a script that ends without exit leaves LASTEXITCODE as it was.
        exit 0
        """;

    [Fact]
    public void The_database_comes_from_mirror_gcr_io_when_it_serves_it()
    {
        var (exit, error, repositories, saved) = Download();

        Assert.True(exit == 0, error);
        Assert.Equal([Mirror], repositories);
        Assert.True(saved);
    }

    [Fact]
    public void A_layer_404_from_mirror_gcr_io_falls_back_to_ghcr_io()
    {
        var (exit, error, repositories, saved) = Download(Mirror);

        Assert.True(exit == 0, error);
        Assert.Equal([Mirror, Ghcr], repositories);
        Assert.True(saved);
    }

    [Fact]
    public void With_no_repository_serving_the_database_it_fails_and_leaves_none_to_scan_with()
    {
        var (exit, error, repositories, saved) = Download(Mirror, Ghcr);

        Assert.NotEqual(0, exit);
        Assert.Contains("No vulnerability database from any of", error);
        Assert.Equal([Mirror, Ghcr], repositories);
        Assert.False(saved);
    }

    static (int Exit, string Error, string[] Repositories, bool Saved) Download(params string[] failing)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), ".github", "actions", "install-trivy", "download-db.ps1");
            var trivy = Path.Combine(folder, "trivy.ps1");
            File.WriteAllText(trivy, FakeTrivy);
            var calls = Path.Combine(folder, "calls.txt");
            var cache = Path.Combine(folder, "cache");
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
            // Through the environment, so no value needs quoting for PowerShell.
            start.Environment["TRIVY_DB_TEST_SCRIPT"] = script;
            start.Environment["TRIVY_DB_TEST_TRIVY"] = trivy;
            start.Environment["TRIVY_DB_TEST_CACHE"] = cache;
            start.Environment["FAKE_TRIVY_CALLS"] = calls;
            start.Environment["FAKE_TRIVY_FAIL"] = string.Join(',', failing);
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         "$ErrorActionPreference = 'Stop'; . $env:TRIVY_DB_TEST_SCRIPT; Save-TrivyDb -Trivy $env:TRIVY_DB_TEST_TRIVY -CacheDir $env:TRIVY_DB_TEST_CACHE" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var lines = File.Exists(calls) ? File.ReadAllLines(calls) : [];
            foreach (var line in lines)
                Assert.Contains("--download-db-only", line);
            var repositories = lines.Select(l => l.Split(' ')).Select(a => a[Array.IndexOf(a, "--db-repository") + 1]).ToArray();
            return (process.ExitCode, error.Result, repositories, File.Exists(Path.Combine(cache, "db", "trivy.db")));
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }
}
