using System.Text.Json;
using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class SlotPayloadRulesTests
{
    const string Target = "net10.0/win-x64";

    [Theory]
    [InlineData("a")]
    [InlineData("lib/net10.0/Some.Library.dll")]
    [InlineData("COM10")]
    public void A_plain_relative_path_is_safe(string path) => Assert.True(SlotPayloadRules.SafeRelative(path));

    [Theory]
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a\\b")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("a./b")]
    [InlineData("a /b")]
    [InlineData("a/b:stream")]
    [InlineData("a/b*")]
    [InlineData("a/PROGRA~1")]
    [InlineData("a/con")]
    [InlineData("a/CON.txt")]
    [InlineData("a/COM1")]
    [InlineData("a/lpt².log")]
    public void An_unsafe_relative_path_is_refused(string path) => Assert.False(SlotPayloadRules.SafeRelative(path));

    [Fact]
    public void A_control_character_in_a_path_is_refused() => Assert.False(SlotPayloadRules.SafeRelative("a/b\u0001c"));

    static JsonElement Deps(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Runtime_assets_name_each_runtime_native_and_localized_file_once_with_its_kind()
    {
        var deps = Deps("""
            {"targets":{"net10.0/win-x64":{
              "A/1.0":{"dependencies":{"B":"1.0"},"runtime":{"lib/net10.0/A.dll":{},"lib/net10.0/_._":{}}},
              "B/1.0":{"native":{"runtimes/win-x64/native/b.dll":{}},"resources":{"lib/net10.0/fr/B.resources.dll":{"locale":"fr"}}}}}}
            """);

        Assert.Equal(
            [("A.dll", (byte)1), ("b.dll", (byte)2), ("fr/B.resources.dll", (byte)1)],
            SlotPayloadRules.RuntimeAssets(deps, Target).ToArray());
    }

    [Fact]
    public void A_runtime_target_asset_for_another_runtime_is_left_out()
    {
        var deps = Deps("""
            {"targets":{"net10.0/win-x64":{
              "A/1.0":{"runtimeTargets":{
                "runtimes/win-x64/native/a.dll":{"rid":"win-x64","assetType":"native"},
                "runtimes/win-arm64/native/a.dll":{"rid":"win-arm64","assetType":"native"}}}}}}
            """);

        Assert.Equal([("a.dll", (byte)2)], SlotPayloadRules.RuntimeAssets(deps, Target).ToArray());
    }

    [Fact]
    public void A_dependency_that_is_not_in_the_graph_is_refused()
    {
        var deps = Deps("""{"targets":{"net10.0/win-x64":{"A/1.0":{"dependencies":{"C":"1.0"}}}}}""");

        var failed = Assert.Throws<InvalidDataException>(() => SlotPayloadRules.RuntimeAssets(deps, Target).ToArray());

        Assert.Contains("Missing runtime dependency: C", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cyclic_dependency_graph_is_refused()
    {
        var deps = Deps("""
            {"targets":{"net10.0/win-x64":{
              "A/1.0":{"dependencies":{"B":"1.0"}},
              "B/1.0":{"dependencies":{"A":"1.0"}}}}}
            """);

        var failed = Assert.Throws<InvalidDataException>(() => SlotPayloadRules.RuntimeAssets(deps, Target).ToArray());

        Assert.Contains("Cyclic", failed.Message, StringComparison.Ordinal);
    }
}
