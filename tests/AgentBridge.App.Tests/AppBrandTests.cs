using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.App.Tests;

// The product's name and brand art come from one place each (#537), so renaming the product (#535)
// is a change there rather than a hunt: Product.Name for the code, the app's project for the build
// and the installer, and AppAssets for the art's files.
public class AppBrandTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        return root;
    }

    static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    static string ProjectProperty(string name) =>
        XDocument.Load(Path.Combine(Root, "src", "AgentBridge.App", "AgentBridge.App.csproj")).Descendants(name).Single().Value;

    [Fact]
    public void The_exe_and_the_installer_carry_the_same_name_as_the_app_shows()
    {
        Assert.Equal(Product.Name, ProjectProperty("Product"));
        Assert.Equal("$(Product)", ProjectProperty("AssemblyTitle"));

        // The installer's and the releases' titles are the project's Product, read by app-channels.
        Assert.Contains("$AppProductName = Get-AppProjectProperty 'Product'", Read("build", "app-channels.ps1"), StringComparison.Ordinal);
        Assert.Contains("--packTitle $AppProductName", Read("build", "pack-app.ps1"), StringComparison.Ordinal);
        var publish = Read("build", "publish-prerelease.ps1");
        Assert.Contains("\"$AppProductName $Version (pre-release)\"", publish, StringComparison.Ordinal);
        Assert.Contains("\"Pre-release of the $AppProductName Windows app", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exe_the_window_and_the_installer_use_the_same_icon_files()
    {
        static string Local(string project) => project.Replace('\\', Path.DirectorySeparatorChar);
        Assert.Equal(AppAssets.Icon, Local(ProjectProperty("AppIconFile")));
        Assert.Equal(AppAssets.Logo, Local(ProjectProperty("AppLogoFile")));
        Assert.Equal("$(AppIconFile)", ProjectProperty("ApplicationIcon"));
        Assert.True(File.Exists(Path.Combine(Root, "src", "AgentBridge.App", AppAssets.Icon)));
        Assert.True(File.Exists(Path.Combine(Root, "src", "AgentBridge.App", AppAssets.Logo)));

        var pack = Read("build", "pack-app.ps1");
        Assert.Contains("--icon (Join-Path $publish $AppIconFile)", pack, StringComparison.Ordinal);
        Assert.Contains("$AppIconFile, $AppLogoFile", pack, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hosts_executables_carry_the_products_name_and_keep_their_file_names()
    {
        foreach (var (project, file) in new[] { ("AgentBridge.Host", "agent-bridge-host"), ("AgentBridge.HostLauncher", "agent-bridge-hostw"), ("AgentBridge.Injector", "agent-bridge-injector"), ("AgentBridge.Hook", "agent-bridge-host-hook") })
        {
            var csproj = XDocument.Load(Path.Combine(Root, "src", project, $"{project}.csproj"));
            Assert.Equal(Product.Name, csproj.Descendants("Product").Single().Value);
            Assert.StartsWith($"{Product.Name} ", csproj.Descendants("AssemblyTitle").Single().Value, StringComparison.Ordinal);
            Assert.Equal(file, csproj.Descendants("AssemblyName").Single().Value);
        }
    }

    // An update to the new name must still be the same install: Windows finds its notifications by
    // the AUMID and activator, Velopack its feed by the pack id and its Start shortcut by the exe it
    // points at, and pairing, keys and settings live under the data folder (#537's "Do not rename
    // in Phase 1"; #541 migrates them deliberately).
    [Fact]
    public void Renaming_the_product_leaves_what_an_installed_copy_is_found_by()
    {
        var toasts = Read("src", "AgentBridge.App", "Toasts.cs");
        Assert.Contains("Aumid = \"velopack.AgentBridgeApp\"", toasts, StringComparison.Ordinal);
        Assert.Contains("new(\"671f58d4-187e-47c8-bd8f-ca6103448929\")", toasts, StringComparison.Ordinal);
        Assert.Contains("$AppPackId = 'AgentBridgeApp'", Read("build", "app-channels.ps1"), StringComparison.Ordinal);
        Assert.Equal("AgentBridge", ProjectProperty("AssemblyName"));
        Assert.Contains("-e AgentBridge.exe", Read("build", "pack-app.ps1"), StringComparison.Ordinal);
        Assert.Contains("\"AgentBridge\")", Read("src", "AgentBridge.Transport", "ProfileFolders.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_tray_tooltip_names_the_product_and_how_many_wait()
    {
        Assert.Equal(Product.Name, MainViewModel.TrayTip(0));
        Assert.Equal($"{Product.Name} · 3 need you", MainViewModel.TrayTip(3));
    }

    // Copy in the Windows app, the host and its launcher that names the product (old or new) or the
    // host service by hand, rather than through Product and the brand token: what a rename would miss. The
    // bundled fonts' family names (Slipgates Sans and Mono) are the fonts', not the product's. Comments may name it; the
    // generated design resources name the bundled font, which is the design's to rename (#544).
    [Fact]
    public void No_windows_app_or_host_copy_types_the_product_name_by_hand()
    {
        var literal = new Regex("\"[^\"\\r\\n]*(Agent Bridge|Bridge Host|Slipgates(?! ?(Sans|Mono)))[^\"\\r\\n]*\"");
        string[] projects = ["AgentBridge.App", "AgentBridge.App.Core", "AgentBridge.Host", "AgentBridge.Host.Core", "AgentBridge.Host.Delivery", "AgentBridge.HostLauncher", "AgentBridge.Local"];
        var generated = Path.Combine("src", "AgentBridge.App", "Design") + Path.DirectorySeparatorChar;
        List<string> typed = [];
        foreach (var project in projects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)))
            {
                var relative = Path.GetRelativePath(Root, file);
                if (relative.StartsWith(generated, StringComparison.Ordinal) || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) { continue; }
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i].TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("<!--", StringComparison.Ordinal)) { continue; }
                    if (literal.IsMatch(code)) { typed.Add($"{relative}:{i + 1}: {code}"); }
                }
            }
        }
        Assert.Empty(typed);
    }
}
