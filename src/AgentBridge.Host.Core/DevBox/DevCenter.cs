using System.Net.Http.Headers;
using AgentBridge.Host.Core.Usage;

namespace AgentBridge.Host.Core.DevBox;

// One answer from Dev Center: its status and body. Body carries the service's error envelope,
// whose code says why far better than the status line does.
public sealed record DevCenterResponse(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

// Dev Center's developer API, faked in tests: nothing in the test suites reaches Dev Center, and
// no test can move a real Dev Box's schedule.
public interface IDevCenterHttp
{
    Task<DevCenterResponse> SendAsync(HttpMethod method, Uri uri, string token, CancellationToken cancellationToken = default);
}

public sealed class DevCenterHttp(TimeSpan timeout) : IDevCenterHttp, IDisposable
{
    readonly HttpClient http = new() { Timeout = timeout };

    public async Task<DevCenterResponse> SendAsync(HttpMethod method, Uri uri, string token, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // As Invoke-RestMethod sends a POST with no body: an empty one.
        if (method == HttpMethod.Post) { request.Content = new ByteArrayContent([]); }
        using var response = await http.SendAsync(request, cancellationToken);
        return new DevCenterResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public void Dispose() => http.Dispose();
}

// A Dev Center token for the signed-in user, faked in tests. Throws DevBoxException, saying what
// to do, when there is none.
public interface IDevCenterToken
{
    Task<string> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class DevBoxException(string message) : Exception(message);

// The Azure CLI's own login, as agent-ha-bridge's Get-BridgeDevBoxAccessToken takes it: the user
// is already signed in to it on a Dev Box, and the token must be the user's, since users/me is
// the only scope that can skip or delay their own Dev Box. az runs windowless (the runner's
// CreateNoWindow, through cmd.exe for az.cmd), and what it prints is the token: never logged.
public sealed class AzCliToken(ICommandRunner runner, Func<string?> findAz) : IDevCenterToken
{
    public const string Resource = "https://devcenter.azure.com";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        var az = findAz() ?? throw new DevBoxException("the Azure CLI (az) was not found; install it and run 'az login' as this user");
        var result = await runner.RunAsync(az, ["account", "get-access-token", "--resource", Resource, "--query", "accessToken", "-o", "tsv"], Timeout, cancellationToken);
        if (!result.Ran) { throw new DevBoxException($"the Azure CLI ({az}) could not be started"); }
        if (result.TimedOut) { throw new DevBoxException($"the Azure CLI did not answer within {Timeout.TotalSeconds:F0} s"); }
        var token = result.ExitCode == 0 ? result.Output.Trim() : "";
        if (token.Length == 0) { throw new DevBoxException("No Dev Center token. Run 'az login' as this user."); }
        return token;
    }

    // az on PATH (this process's, then the user's and the machine's, read afresh: a host started
    // at logon can have a PATH from before the CLI was installed), else where its installer puts
    // it. On Windows it is az.cmd, which only runs through cmd.exe.
    public static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "az.cmd", "az.exe", "az.bat" } : ["az"];
        var sources = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        foreach (var source in sources)
        {
            foreach (var raw in (source ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var dir = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
                // A relative entry would search wherever the host was started from.
                if (!Path.IsPathFullyQualified(dir)) { continue; }
                foreach (var name in names)
                {
                    try
                    {
                        var candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate)) { return candidate; }
                    }
                    catch (ArgumentException) { }
                }
            }
        }
        if (!OperatingSystem.IsWindows()) { return null; }
        return new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") }
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => Path.Combine(d!, "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"))
            .FirstOrDefault(File.Exists);
    }
}
