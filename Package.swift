// swift-tools-version: 5.9
import PackageDescription
import Foundation
let vendorLib = URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("Vendor/lib").path
let package = Package(name: "DanmakuPlayer", platforms: [.macOS(.v14)], products: [.executable(name: "DanmakuPlayer", targets: ["PlayerUI"])], targets: [
    .systemLibrary(name: "CSQLite", pkgConfig: "sqlite3"),
    .target(name: "PlayerCore", dependencies: ["CSQLite"]),
    .target(name: "MPVBridge", path: "Sources/MPVBridge", publicHeadersPath: "include", cSettings: [.headerSearchPath("../../Vendor/include")], linkerSettings: [.unsafeFlags(["-L", vendorLib, "-lmpv", "-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"]), .linkedFramework("OpenGL"), .linkedFramework("AppKit")]),
    .executableTarget(name: "PlayerUI", dependencies: ["PlayerCore", "MPVBridge"]),
    .executableTarget(name: "CoreChecks", dependencies: ["PlayerCore"], path: "Tests/PlayerCoreTests")
])
