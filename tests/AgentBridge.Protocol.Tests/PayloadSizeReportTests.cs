using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

public sealed class PayloadSizeReportTests(ITestOutputHelper output)
{
    [Fact]
    public void Report_lists_every_vector_message_type_with_byte_sizes()
    {
        var result = Run(VectorFiles.Folder);
        Assert.True(result.ExitCode == 0, result.Error);
        output.WriteLine(result.Text);
        var expected = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(VectorFiles.Folder, "*.json"))
        {
            var records = JsonNode.Parse(File.ReadAllText(file))!["records"]?.AsObject();
            if (records is null) { continue; }
            foreach (var (name, record) in records)
            {
                if (!expected.TryGetValue(name, out var sizes)) { expected[name] = sizes = []; }
                foreach (var kind in new[] { "valid", "refused" })
                {
                    foreach (var vector in record![kind]!.AsArray())
                    {
                        sizes.Add(Encoding.UTF8.GetByteCount(vector!["json"]!.ToJsonString(ProtocolJson.Options)));
                    }
                }
            }
        }
        var rows = Rows(result.Text);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), rows.Keys);
        foreach (var (name, sizes) in expected)
        {
            Assert.Equal((sizes.Min(), sizes.Max(), sizes.Count), rows[name]);
        }

        using var sample = new VectorFolder();
        sample.Write("a.json", """
            {"records":{"Example":{"valid":[{"json":{"text":"é"}}],
                                  "refused":[{"json":{"text":"longer"}}]}}}
            """);
        sample.Write("b.json", """
            {"records":{"Example":{"valid":[{"json":{}}],"refused":[]}}}
            """);
        var measured = Run(sample.Path);
        Assert.True(measured.ExitCode == 0, measured.Error);
        Assert.Equal((2, 17, 3), Rows(measured.Text)["Example"]);
    }

    [Fact]
    public void Report_is_deterministic_for_the_same_vectors()
    {
        using var sample = new VectorFolder();
        const string alpha = """{"records":{"Alpha":{"valid":[{"json":{"text":"é"}}],"refused":[]}}}""";
        const string zulu = """{"records":{"Zulu":{"valid":[{"json":{}}],"refused":[]}}}""";
        sample.Write("first.json", zulu);
        sample.Write("last.json", alpha);
        var first = Run(sample.Path);
        sample.Write("first.json", alpha);
        sample.Write("last.json", zulu);
        var second = Run(sample.Path);
        Assert.True(first.ExitCode == 0, first.Error);
        Assert.True(second.ExitCode == 0, second.Error);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(new[] { "Alpha", "Zulu" }, Rows(first.Text).Keys);
        Assert.Equal((13, 13, 1), Rows(first.Text)["Alpha"]);
    }

    [Fact]
    public void Report_fails_clearly_on_a_vector_that_cannot_be_parsed()
    {
        using var sample = new VectorFolder();
        foreach (var malformed in new[] { "{broken", """{"records":{"Broken":{"valid":[{}],"refused":[]}}}""" })
        {
            sample.Write("broken.json", malformed);
            var result = Run(sample.Path);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("broken.json", result.Error);
            Assert.Contains("Cannot measure vector file", result.Error);
            Assert.Empty(result.Text);
        }
    }

    static Dictionary<string, (int Min, int Max, int Count)> Rows(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("MessageType\tMinBytes\tMaxBytes\tCount", lines[0]);
        return lines.Skip(1).Select(line => line.Split('\t')).ToDictionary(
            row => row[0],
            row => (int.Parse(row[1], CultureInfo.InvariantCulture),
                    int.Parse(row[2], CultureInfo.InvariantCulture),
                    int.Parse(row[3], CultureInfo.InvariantCulture)),
            StringComparer.Ordinal);
    }

    static (int ExitCode, string Text, string Error) Run(string folder)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            System.IO.Path.Combine(VectorFiles.Folder, "report-payload-sizes.ps1"), "-VectorPath", folder })
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Payload size report did not exit within 30 seconds.");
        }
        return (process.ExitCode, text.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    sealed class VectorFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "payload-report-" + Guid.NewGuid().ToString("N"));
        public VectorFolder() => Directory.CreateDirectory(Path);
        public void Write(string name, string text) => File.WriteAllText(System.IO.Path.Combine(Path, name), text);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
