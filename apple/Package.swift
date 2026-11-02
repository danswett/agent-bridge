// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "AgentBridgeAppleCore",
    platforms: [.macOS(.v15), .iOS(.v18)],
    products: [.library(name: "AgentBridgeAppleCore", targets: ["AgentBridgeAppleCore"])],
    targets: [
        .target(name: "AgentBridgeAppleCore"),
        .testTarget(name: "AgentBridgeAppleCoreTests", dependencies: ["AgentBridgeAppleCore"],
                    resources: [.copy("Resources/noise-xx-p256-vectors.json")]),
    ]
)
