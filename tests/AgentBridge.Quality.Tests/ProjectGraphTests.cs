using System.Xml.Linq;

namespace AgentBridge.Quality.Tests;

public class ProjectGraphTests
{
    [Theory]
    [InlineData("*.md", "README.md", true)]
    [InlineData("*.md", "docs/status.md", false)]
    [InlineData("docs/*.md", "docs/status.md", true)]
    [InlineData("docs/*.md", "docs/design/ui.md", false)]
    [InlineData("pwa/**", "pwa/test/unit/a.test.mjs", true)]
    [InlineData("pwa/**", "pwa2/app.css", false)]
    [InlineData("**/Directory.Build.*", "Directory.Build.props", true)]
    [InlineData("**/Directory.Build.*", "src/Host/Directory.Build.targets", true)]
    [InlineData("AgentBridge.slnx", "AgentBridge.slnx", true)]
    [InlineData("AgentBridge.slnx", "tools/AgentBridge.slnx", false)]
    public void A_star_stays_within_a_folder_and_a_double_star_crosses_folders(string pattern, string path, bool matches) =>
        Assert.Equal(matches, new Glob(pattern).IsMatch(path));

    [Fact]
    public void A_csproj_yields_its_references_its_out_of_folder_links_and_whether_it_tests()
    {
        var csproj = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <ProjectReference Include="..\..\src\AgentBridge.Host.Core\AgentBridge.Host.Core.csproj" />
                <None Include="..\fixtures\copilot\*" Link="fixtures\copilot\%(Filename)%(Extension)" />
                <Compile Include="Local.cs" />
              </ItemGroup>
            </Project>
            """);

        var project = ProjectGraph.Parse("tests/AgentBridge.Host.Core.Tests", csproj);

        Assert.Equal(["src/AgentBridge.Host.Core"], project.References);
        Assert.Equal(["tests/fixtures/copilot/*"], project.LinkedFiles.Select(g => g.Pattern));
        Assert.True(project.IsTestProject);
    }

    [Fact]
    public void A_file_belongs_to_the_project_with_the_longest_folder_prefix()
    {
        var graph = new ProjectGraph([Project.Create("src/App"), Project.Create("src/App/Inner"), Project.Create("src/AppCore")]);

        Assert.Equal(["src/App/Inner"], graph.OwnersOf("src/App/Inner/X.cs").Select(p => p.Folder));
        Assert.Equal(["src/App"], graph.OwnersOf("src/App/X.cs").Select(p => p.Folder));
        Assert.Empty(graph.OwnersOf("src/Apple/X.cs"));
    }

    [Fact]
    public void The_graph_is_read_from_the_csproj_files_on_disk_skipping_build_output()
    {
        var root = Fixture.ScratchFolder();
        try
        {
            Write(root, "src/Lib/Lib.csproj", "<Project />");
            Write(root, "src/Lib/obj/Stale.csproj", "<Project />");
            Write(root, "tests/Lib.Tests/Lib.Tests.csproj", """<Project><ItemGroup><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup></Project>""");

            var graph = ProjectGraph.Load(root);

            Assert.Equal(["src/Lib", "tests/Lib.Tests"], graph.Projects.Select(p => p.Folder).Order());
            Assert.Equal(["src/Lib", "tests/Lib.Tests"], graph.ReachersOf("src/Lib").Order());
        }
        finally
        {
            Fixture.Delete(root);
        }
    }

    static void Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}
