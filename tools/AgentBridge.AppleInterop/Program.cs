using System.Security.Cryptography.X509Certificates;

namespace AgentBridge.AppleInterop;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Arguments.Parse(args);
            // This executable owns its process. Never send its loopback-only test traffic
            // through a developer's inherited HTTP(S)/ALL_PROXY configuration.
            HttpClient.DefaultProxy = new System.Net.WebProxy();
            var password = await File.ReadAllTextAsync(options.PasswordFile);
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificateFile, password.TrimEnd('\r', '\n'));
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
            { throw new ArgumentException("the development certificate must have a private key and be currently valid"); }
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await using var stack = await InteropStack.StartAsync(options.ManifestFile, certificate, stop.Token);
            Console.WriteLine("ready: isolated product relay and host; synthetic transcripts; no agent typing");
            while (!stop.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(stop.Token);
                if (line is null) { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); break; }
                if (line.Trim() == "quit") { break; }
                await stack.CommandAsync(line.Trim(), stop.Token);
                Console.WriteLine("fixture command completed");
            }
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception e)
        {
            // Certificate/provider/HTTP exceptions can contain paths, secrets or request URLs.
            // Keep stdout/stderr suitable for CI; the public exception type identifies the step.
            Console.Error.WriteLine($"interop startup or command failed ({e.GetType().Name}); check the supplied certificate, private password file and unused manifest path");
            return 1;
        }
    }
}

internal sealed record Arguments(string CertificateFile, string PasswordFile, string ManifestFile)
{
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--certificate" or "--certificate-password-file" or "--manifest") || !values.TryAdd(args[i], args[i + 1]))
            { throw new ArgumentException("usage: --certificate <PFX> --certificate-password-file <private file> --manifest <new file>"); }
        }
        if (values.Count != 3) { throw new ArgumentException("certificate, password file and manifest are required"); }
        return new(Path.GetFullPath(values["--certificate"]), Path.GetFullPath(values["--certificate-password-file"]), Path.GetFullPath(values["--manifest"]));
    }
}
