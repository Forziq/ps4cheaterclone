// swift-tools-version:5.10
// PS4 Mem for macOS: a SwiftUI app that talks to the C# backend (src/PS4Mem.Api) on 127.0.0.1.
// Build:  swift build          (from this folder; the Command Line Tools are enough)
// Run:    ../run.sh            (builds the API and the app, runs both, Ctrl+C stops both)
// App:    ../scripts/build-app.sh   (PS4Mem.app with the self-contained backend inside)
// Xcode can open this file too, but it isn't needed.
import PackageDescription

let package = Package(
    name: "PS4Mem",
    platforms: [.macOS(.v14)],
    targets: [
        // The app itself. Folders: API (client and JSON models), Backend (starts the API), Views (SwiftUI).
        .executableTarget(name: "PS4Mem", path: "Sources/PS4Mem"),
        // Tests for the parts that don't need a window: JSON decoding, the API client, the backend launcher.
        .testTarget(name: "PS4MemTests", dependencies: ["PS4Mem"], path: "Tests/PS4MemTests"),
    ]
)
