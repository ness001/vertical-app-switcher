// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "VerticalAppSwitcher",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "VerticalAppSwitcher", targets: ["VerticalAppSwitcher"])],
    targets: [
        .executableTarget(name: "VerticalAppSwitcher"),
        .testTarget(name: "VerticalAppSwitcherTests", dependencies: ["VerticalAppSwitcher"])
    ]
)
