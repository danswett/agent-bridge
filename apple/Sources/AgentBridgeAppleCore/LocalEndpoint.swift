#if os(macOS)
    import CryptoKit
    import Foundation

    public enum LocalEndpoint {
        public enum Failure: LocalizedError, BridgeFixedSentenceError {
            case invalidProfile
            public var sentence: String {
                errorDescription ?? ""
            }
            public var errorDescription: String? {
                "Use 1–64 letters, numbers, dots, hyphens or underscores for the profile name (not . or ..)."
            }
        }

        public static func socketPath(profile: String, home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                      environment: [String: String] = ProcessInfo.processInfo.environment) throws -> String
        {
            guard (1 ... 64).contains(profile.utf8.count), profile != ".", profile != "..", profile.utf8.allSatisfy({
                (65 ... 90).contains($0) || (97 ... 122).contains($0) || (48 ... 57).contains($0) || $0 == 45 || $0 == 95 || $0 == 46
            }) else { throw Failure.invalidProfile }
            let folder = home.appendingPathComponent(".agent-bridge").appendingPathComponent(profile).standardized.path
            return socketPath(folder: folder, environment: environment)
        }

        static func socketPath(folder: String, environment: [String: String]) -> String {
            let path = URL(fileURLWithPath: folder).appendingPathComponent("host.sock").path
            guard path.utf8.count > 103 else { return path }
            let hash = SHA256.hash(data: Data(folder.utf8)).prefix(8).map { String(format: "%02x", $0) }.joined()
            let runtime = [environment["XDG_RUNTIME_DIR"], environment["TMPDIR"]].compactMap(\.self).first { value in
                var directory: ObjCBool = false
                return !value.isEmpty && FileManager.default.fileExists(atPath: value, isDirectory: &directory) && directory.boolValue
            }
            if let runtime {
                let fallback = URL(fileURLWithPath: runtime).appendingPathComponent("agent-bridge-\(hash).sock").path
                if fallback.utf8.count <= 103 {
                    return fallback
                }
            }
            return path
        }
    }
#endif
