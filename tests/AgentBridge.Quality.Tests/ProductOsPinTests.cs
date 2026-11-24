namespace AgentBridge.Quality.Tests;

// The product-os pin (#4104, R8b on #4306): quality/product-os-pin.ps1 reads plan.ps1 with the PowerShell parser and
// allow-lists the one safe write; RunnerRouting.ProductOsProblems holds the map to ProductRunners. Each shape below,
// put into the real plan.ps1, writes product-os, the map or the output file some other way, and must be caught. It
// starts pwsh; it runs in ring 0 too, so a draft's fast ring checks the pin (R4zzB842 P3).
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
    // R8c (#4306 P2/P3): dot-sourcing, a built GITHUB_OUTPUT line, and an input or environment value as a product-os value.
    // R4zzB842 P1-A: a function, filter, alias, module or type that replaces a command the write or the output uses.
    [InlineData(AfterWrites, "function ConvertTo-JsonArray($items) { '[\"ab-ci\"]' }")]
    [InlineData(AfterWrites, "function ForEach-Object { 'ab-ci' }")]
    [InlineData(AfterWrites, "function Where-Object { $true }")]
    [InlineData(AfterWrites, "function ConvertTo-Json { '[\"ab-ci\"]' }")]
    [InlineData(AfterWrites, "function global:ConvertTo-Json { '[\"ab-ci\"]' }")]
    [InlineData(AfterWrites, "filter ForEach-Object { 'ab-ci' }")]
    [InlineData(AfterWrites, "Set-Alias ForEach-Object Write-Output -Force")]
    [InlineData(AfterWrites, "sal ForEach-Object Write-Output")]
    [InlineData(AfterWrites, "New-Alias -Name ConvertTo-Json -Value Write-Output -Force")]
    [InlineData(AfterWrites, "nal ConvertTo-Json Write-Output")]
    [InlineData(AfterWrites, "Import-Module ./evil.psm1")]
    [InlineData(AfterWrites, "Add-Type -TypeDefinition 'public static class X { }'")]
    [InlineData(AfterWrites, "Update-TypeData -TypeName System.String -MemberType ScriptMethod -MemberName X -Value { 'ab-ci' }")]
    [InlineData(AfterWrites, ". ./x.ps1")]
    // R4zzB858: an in-process write to the output file (any command, env read or type outside plan.ps1's own), and an
    // error setting or trap that would silence the line-break throw; an owned function defined twice.
    [InlineData(AfterOutput, "$p = 'GITHUB_' + 'OUTPUT'; $w = New-Object IO.StreamWriter((Get-Item \"env:$p\").Value, $true); $w.WriteLine('product-' + 'os=[\"ab-ci\"]'); $w.Close()")]
    [InlineData(AfterOutput, "$p = 'GITHUB_' + 'OUTPUT'; Start-Transcript -Append -Path (Get-Item \"env:$p\").Value")]
    [InlineData(AfterOutput, "Write-Host ('::set-' + 'output name=product' + '-os::[\"ab-ci\"]')")]
    [InlineData(AfterOutput, "Write-Output ('::set-' + 'output name=product' + '-os::[\"ab-ci\"]')")]
    [InlineData(AfterOutput, "$x = $env:HOME")]
    [InlineData(AfterOutput, "$w = [System.Text.StringBuilder]::new()")]
    [InlineData(AfterWrites, "$ErrorActionPreference = 'SilentlyContinue'")]
    [InlineData(AfterWrites, "$global:ErrorActionPreference = 'Ignore'")]
    [InlineData(AfterWrites, "trap { continue }")]
    [InlineData(AfterWrites, "function Text($s) { $s }")]
    // R4zzB871 (Quality's ruling: a lint against accidents and plain edits): default parameters, the host, drive-qualified
    // variables, reflection members, string .ForEach/.Where and -MemberName, and a bare expression that would print.
    [InlineData(AfterOutput, "$PSDefaultParameterValues['Add-Content:Path'] = 'x'")]
    [InlineData(AfterOutput, "$PSDefaultParameterValues.Add('*:Path', 'x')")]
    [InlineData(AfterOutput, "$global:PSDefaultParameterValues = @{}")]
    [InlineData(AfterOutput, "$Host.UI.WriteLine('::set-' + 'output name=product' + '-os::[]')")]
    [InlineData(AfterOutput, "$x = ${alias:ForEach-Object}")]
    [InlineData(AfterOutput, "${c:/tmp/out.txt} = 'product-' + 'os=[]'")]
    [InlineData(AfterOutput, "$x = ${/tmp/x}")]
    [InlineData(AfterOutput, "$x = 'a'.GetType().Assembly")]
    [InlineData(AfterOutput, "$x = [string].Module")]
    [InlineData(AfterOutput, "$x = @(1).ForEach('GetType')")]
    [InlineData(AfterOutput, "$x = @(1).Where('x')")]
    [InlineData(AfterOutput, "$x = @(1) | ForEach-Object -MemberName GetType")]
    [InlineData(AfterOutput, "'::set-' + 'output name=product' + '-os::[\"ab-ci\"]'")]
    [InlineData(AfterOutput, "if ($true) { '::set-' + 'output name=product' + '-os::[]' }")]
    [InlineData(AfterWrites, ". $PSScriptRoot/x.ps1")]
    [InlineData(AfterOutput, "Add-Content $env:GITHUB_OUTPUT ('product' + '-os=[\"ab-ci\"]')")]
    [InlineData(AfterOutput, "$lines = @($lines) + ('product' + '-os=[\"ab-ci\"]')")]
    [InlineData(AfterWrites, "$productOs = @($Platform)")]
    [InlineData(AfterWrites, "$productOs = @($env:AB_CI_RUNNER)")]
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
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = $Platform")]
    [InlineData("'win-arm64' = 'windows-11-arm'", "'win-arm64' = $env:AB_CI_RUNNER")]
    [InlineData("ForEach-Object { $runners[$_] })\n#", "ForEach-Object { $Platform })\n#")]
    // R8c: the help block ends at the FIRST #>, as PowerShell reads it, so a write after it on that line is code.
    [InlineData(".SYNOPSIS\n", ".SYNOPSIS #> $productOs = @('ab-ci') <#\n")]
    [InlineData("ForEach-Object { $runners[$_] })\n#", "ForEach-Object { $runners[$_]; 'ab-ci' })\n#")]
    [InlineData("Where-Object { -not $platform", "Where-Object { $($productOs = 'ab-ci') -or -not $platform")]
    [InlineData("Where-Object { -not $platform", "Where-Object { (Set-Variable x 1) -or -not $platform")]
    [InlineData("$outputs['product-os'] = ConvertTo-JsonArray $productOs", "$outputs['product-os'] = '[\"ab-ci\"]'")]
    [InlineData("function ConvertTo-JsonArray($items) { ConvertTo-Json -InputObject @($items) -Compress }", "function ConvertTo-JsonArray($items) { '[\"ab-ci\"]' }")]
    [InlineData("function Cell(", "function Cel1(")]
    public void A_changed_map_write_or_output_is_caught(string from, string to)
    {
        var plan = Plan().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(from, plan, StringComparison.Ordinal);
        Assert.NotEmpty(Pin(plan.Replace(from, to, StringComparison.Ordinal)));
    }

    // R4zzB842 P1-B: a line break in any output's value would write a second line, product-os=..., to the output file.
    // The plan's own $lines statement (the one the pin holds) must refuse it, whatever builds the value.
    [Theory]
    [InlineData("$outputs['x'] = \"a`nproduct-\" + 'os=[\"ab-ci\"]'")]
    [InlineData("$outputs['x'] = \"a`r`nproduct-\" + 'os=[\"ab-ci\"]'")]
    [InlineData("$outputs['x'] = 'a' + [char]10 + 'product-os=[\"ab-ci\"]'")]
    [InlineData("$outputs['x'] = 'a{0}product-os=[\"ab-ci\"]' -f [char]10")]
    [InlineData("$outputs[\"x`nproduct-os\"] = '[\"ab-ci\"]'")]
    public async Task A_line_break_in_an_output_is_refused_before_the_output_file_is_written(string shape)
    {
        // The pin holds this statement whole, so it is one line of plan.ps1.
        var lines = Plan().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Single(l => l.StartsWith("$lines = ", StringComparison.Ordinal));

        var start = new System.Diagnostics.ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     "$ErrorActionPreference = 'Stop'; $outputs = [ordered]@{ 'product-os' = '[\"ubuntu-latest\"]' }; " + shape + "; " + lines + "; $lines" })
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("line break", await error);
        Assert.DoesNotContain("ab-ci", output);
    }

    // R4zzB858 P2-1: the output statements stay at the top level, where no try can swallow the line-break throw.
    [Fact]
    public void A_pinned_output_statement_inside_a_try_is_caught()
    {
        var plan = Plan().Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = plan.Split('\n').Single(l => l.StartsWith("$lines = ", StringComparison.Ordinal));
        Assert.NotEmpty(Pin(plan.Replace(lines, "try { " + lines + " } catch { }", StringComparison.Ordinal)));
    }

    static string Root() => ChangedFiles.RepoRoot(AppContext.BaseDirectory);
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
