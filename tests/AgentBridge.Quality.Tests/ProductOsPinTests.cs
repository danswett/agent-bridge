namespace AgentBridge.Quality.Tests;

// The product-os pin (#4104, R8b on #4306): quality/product-os-pin.ps1 reads plan.ps1 with the PowerShell parser and
// allow-lists the one safe write; RunnerRouting.ProductOsProblems holds the map to ProductRunners. Each shape below,
// put into the real plan.ps1, writes product-os, the map or the output file some other way, and must be caught. It
// starts pwsh, so ring 0 leaves it to ring 1.
[Trait("Category", "Process")]
public class ProductOsPinTests
{
    const string AfterWrites = "if ($productOs.Count -eq 0 -and $productIds.Count -gt 0)";
    const string AfterOutput = "$outputs['product-os'] = ConvertTo-JsonArray $productOs";

    [Fact]
    public void The_real_plan_passes() => Assert.Empty(Pin(Plan()));

    [Theory]
    [InlineData(AfterWrites, "$productOs += 'ab-ci'")]
    [InlineData(AfterWrites, "$productOs.Add('ab-ci')")]
    [InlineData(AfterWrites, "$productOs[0] = 'ab-ci'")]
    [InlineData(AfterWrites, "$PRODUCTOS = @('ab-ci')")]
    [InlineData(AfterWrites, "${productOs} = @('ab-ci')")]
    [InlineData(AfterWrites, "$script:productOs = @('ab-ci')")]
    [InlineData(AfterWrites, "$null = \"$($productOs = @(\"ab-ci\"))\"")]
    [InlineData(AfterWrites, "[ref]$productOs")]
    [InlineData(AfterWrites, "Set-Variable -Name ('product' + 'Os') -Value @('ab-ci')")]
    [InlineData(AfterWrites, "(Get-Variable productOs).Value = @('ab-ci')")]
    [InlineData(AfterWrites, "Invoke-Expression ('$product' + 'Os = 1')")]
    [InlineData(AfterWrites, "& ('Set-' + 'Variable') productOs @('ab-ci')")]
    [InlineData(AfterWrites, "$runners['win-x64'] = 'ab-ci'")]
    [InlineData(AfterWrites, "$runners.Add('x', 'ab-ci')")]
    [InlineData(AfterOutput, "$outputs[\"product-os\"] = '[\"ab-ci\"]'")]
    [InlineData(AfterOutput, "$outputs['Product-OS'] = '[\"ab-ci\"]'")]
    [InlineData(AfterOutput, "$outputs.'product-os' = '[]'")]
    [InlineData(AfterOutput, "$outputs[('product' + '-os')] = '[]'")]
    [InlineData(AfterOutput, "$outputs.Remove('product-os')")]
    // P1-2: a computed key, or a job name, after the product-os statement.
    [InlineData(AfterOutput, "$job = 'product' + '-os'; $outputs[$job] = '[\"ab-ci\"]'")]
    [InlineData(AfterOutput, "foreach ($job in @('product' + '-os')) { $outputs[$job] = '[\"ab-ci\"]' }")]
    [InlineData(AfterOutput, "foreach ($job in $Jobs) { $outputs[$job] = 'true' }")]
    [InlineData(AfterOutput, "$k = 'product-' + 'os'; $outputs[$k] = '[]'")]
    // Splatting: a map of parameters can carry -OutVariable or a name.
    [InlineData(AfterWrites, "$p = @{ Name = 'product' + 'Os'; Value = @('ab-ci') }; New-Variable @p -Force")]
    [InlineData(AfterWrites, "$p = @{ OutVariable = 'product' + 'Os' }; Write-Output 'ab-ci' @p")]
    [InlineData(AfterOutput, "$p = @{ Path = $env:GITHUB_STEP_SUMMARY }; ConvertTo-JsonArray @p")]
    // The output file, another way.
    [InlineData(AfterOutput, "'product-os=[\"ab-ci\"]' >> $env:GITHUB_OUTPUT")]
    [InlineData(AfterOutput, "Add-Content -Path ([Environment]::GetEnvironmentVariable('GITHUB_' + 'OUTPUT')) -Value x")]
    [InlineData(AfterOutput, "Write-Host ('::set-output name=product' + '-os::[\"ab-ci\"]')")]
    [InlineData(AfterOutput, "$lines += 'x=1'")]
    public void A_write_in_any_other_shape_is_caught(string after, string shape)
    {
        var plan = Plan();
        var line = plan.Split('\n').Single(l => l.Contains(after, StringComparison.Ordinal));
        Assert.NotEmpty(Pin(plan.Replace(line, line.TrimEnd('\r') + "\n" + shape, StringComparison.Ordinal)));
    }

    // P1-1: the map's values are constants, unchanged; the one write keeps its shape.
    [Theory]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = 'windows-11-arm'.Replace('windows-11-arm', 'ab-ci')")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = 'windows-11-arm' -replace '.+', 'ab-ci'")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = 'ab-' + 'ci'")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = 'windows-11-arm' + $env:SUFFIX")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = 'windows-11-arm'; 'x' = $env:RUNNER")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = 'windows-11-arm'; 'x' = 'ubuntu-latest'")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = \"windows-11-arm\"")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = 'ab-ci'")]
    [InlineData("ForEach-Object { $runners[$_] })\n#", "ForEach-Object { $runners[$_]; 'ab-ci' })\n#")]
    [InlineData("Where-Object { -not $platform", "Where-Object { $($productOs = 'ab-ci') -or -not $platform")]
    [InlineData("Where-Object { -not $platform", "Where-Object { (Set-Variable x 1) -or -not $platform")]
    [InlineData("$outputs['product-os'] = ConvertTo-JsonArray $productOs", "$outputs['product-os'] = '[\"ab-ci\"]'")]
    public void A_changed_map_write_or_output_is_caught(string from, string to)
    {
        var plan = Plan().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(from, plan, StringComparison.Ordinal);
        Assert.NotEmpty(Pin(plan.Replace(from, to, StringComparison.Ordinal)));
    }

    static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No AgentBridge.slnx above the test's output."); }
        return root;
    }

    static string Plan() => File.ReadAllText(Path.Combine(Root(), "quality", "plan.ps1"));

    static List<string> Pin(string plan)
    {
        var folder = Fixture.ScratchFolder();
        try
        {
            var path = Path.Combine(folder, "plan.ps1");
            File.WriteAllText(path, plan);
            return RunnerRouting.ProductOsProblems(Root(), path);
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }
}
