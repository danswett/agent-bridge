// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "AgentBridgeAppleCore",
    platforms: [.macOS(.v15), .iOS(.v18)],
    products: [
        .library(name: "AgentBridgeAppleCore", targets: ["AgentBridgeAppleCore"]),
        .executable(name: "agent-bridge-interop", targets: ["AgentBridgeInterop"]),
    ],
    targets: [
        .target(name: "AgentBridgeAppleCore"),
        .target(name: "AgentBridgeClientState", dependencies: ["AgentBridgeAppleCore"], path: "State"),
        .executableTarget(name: "AgentBridgeInterop", dependencies: ["AgentBridgeAppleCore"]),
        .testTarget(name: "AgentBridgeInteropTests", dependencies: ["AgentBridgeInterop"]),
        .testTarget(name: "AgentBridgeClientStateTests", dependencies: ["AgentBridgeClientState", "AgentBridgeAppleCore"]),
        .testTarget(name: "AgentBridgeAppleCoreTests", dependencies: ["AgentBridgeAppleCore"],
                    resources: [.copy("Resources/noise-xx-p256-vectors.json"), .copy("Resources/device-pages.json")]),
    ]
)
