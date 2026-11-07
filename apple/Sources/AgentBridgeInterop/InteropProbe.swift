import Foundation

@main
enum InteropProbe {
    @MainActor static func main() async {
        guard CommandLine.arguments.count == 2 else {
            evidence("arguments", status: "fail", reason: "Usage: agent-bridge-interop /absolute/path/to/private-manifest.json")
            exit(2)
        }
        var runner: ProbeRunner?
        do {
            let manifest = try ProbeManifest.load(CommandLine.arguments[1])
            let probe = ProbeRunner(manifest: manifest)
            runner = probe
            try await probe.run()
        } catch {
            // Never print arbitrary service messages, URLs, bodies, tokens or keys.
            let failure = (error as? ProbeFailure)?.check ?? runner?.check ?? "manifest"
            evidence(failure, status: "fail")
            let code = (error as NSError).code
            evidence("diagnostic.error-code", status: "info", reason: String(code))
            exit(1)
        }
    }
}
