// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "ShrimpBreak",
    platforms: [
        .macOS(.v14)
    ],
    targets: [
        .executableTarget(
            name: "ShrimpBreak",
            path: "Sources/ShrimpBreak"
        )
    ]
)
