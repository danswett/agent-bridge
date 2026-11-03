namespace AgentBridge.Quality.Tests;

/// <summary>A small catalog and graph shaped like the repository's, so tests don't move when it does.</summary>
static class Fixture
{
    public const string CatalogYaml = """
        global_triggers:
          - AgentBridge.slnx
          - .github/workflows/**
        no_test_paths:
          - '*.md'
          - docs/*.md
          - docs/design/**
        suites:
          - id: protocol
            kind: unit
            ring: 0
            select: always
            platforms: [win-x64, linux-x64]
            runner: local
            project: tests/Protocol.Tests
            triggers: [docs/protocol/**]
            command: dotnet test tests/Protocol.Tests
            where: ci.yml product
            status: exists
          - id: transport
            kind: component
            ring: 0
            select: affected
            platforms: [win-x64, linux-x64]
            runner: local
            project: tests/Transport.Tests
            command: dotnet test tests/Transport.Tests
            where: ci.yml product
            status: exists
          - id: host
            kind: component
            ring: 0
            select: affected
            platforms: [win-x64, linux-x64]
            runner: local
            project: tests/Host.Tests
            status: planned
          - id: homeassistant
            kind: component
            ring: 0
            select: affected
            platforms: [win-x64, linux-x64]
            runner: local
            project: tests/HomeAssistant.Tests
            status: planned
          - id: ha-integration
            kind: integration
            ring: 1
            select: affected
            platforms: [linux-x64]
            runner: hosted
            project: tests/HomeAssistant.Tests
            command: see ci.yml ha-integration
            where: ci.yml ha-integration
            status: exists
          - id: docs-links
            kind: static
            ring: 1
            select: affected
            platforms: [linux-x64]
            runner: hosted
            triggers: ['**/*.md']
            status: planned
          - id: pwa-unit
            kind: unit
            ring: 0
            select: affected
            platforms: [browser]
            runner: local
            triggers: [pwa/**]
            command: cd pwa && node --test "test/unit/*.test.mjs"
            status: planned
          - id: pwa-ui
            kind: ui
            ring: 1
            select: affected
            platforms: [browser]
            runner: hosted
            triggers: [pwa/**]
            command: cd pwa && npx playwright test   # Chromium and WebKit
            status: planned
          - id: apple-core
            kind: unit
            ring: 0
            select: affected
            platforms: [macos-arm64]
            runner: local
            triggers: [apple/**]
            status: planned
          - id: infra
            kind: static
            ring: 1
            select: affected
            platforms: [linux-x64]
            runner: hosted
            triggers: [infra/**]
            status: planned
          - id: codeql
            kind: security
            ring: 1
            select: always
            platforms: [linux-x64]
            runner: hosted
            status: planned
          - id: bench
            kind: perf
            ring: 2
            select: always
            platforms: [linux-x64]
            runner: lab
            command: run it on the lab's quiet machine
            status: exists
        """;

    public static Catalog Catalog() => Quality.Catalog.Parse(CatalogYaml);

    // Host reaches HomeAssistant, which reaches HostCore, which reaches Protocol: the real graph's
    // shape. Transport.Tests also references Relay, as the real one does.
    public static ProjectGraph Graph() => new([
        Project.Create("src/Protocol"),
        Project.Create("src/Transport"),
        Project.Create("src/Relay", ["src/Transport"]),
        Project.Create("src/HostCore", ["src/Protocol"]),
        Project.Create("src/HomeAssistant", ["src/HostCore"]),
        Project.Create("src/Host", ["src/HostCore", "src/Transport", "src/HomeAssistant"]),
        Project.Create("tests/Protocol.Tests", ["src/Protocol"], ["docs/protocol/examples/*.json"], isTestProject: true),
        Project.Create("tests/Transport.Tests", ["src/Transport", "src/Relay"], isTestProject: true),
        Project.Create("tests/Host.Tests", ["src/Host"], ["tests/fixtures/shared/*"], isTestProject: true),
        Project.Create("tests/HomeAssistant.Tests", ["src/HomeAssistant"], isTestProject: true),
    ]);

    public static Selector Selector() => new(Catalog(), Graph());

    public static string[] Ids(Selection selection) => selection.Suites.Select(s => s.Suite.Id).ToArray();

    public static Reason ReasonFor(Selection selection, string id, string rule) =>
        selection.Suites.Single(s => s.Suite.Id == id).Reasons.Single(r => r.Rule == rule);

    /// <summary>A scratch folder inside the test's own output, removed by the caller.</summary>
    public static string ScratchFolder()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string folder)
    {
        // git marks its objects read-only, which Directory.Delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }
}
