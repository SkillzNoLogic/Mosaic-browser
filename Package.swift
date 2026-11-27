// swift-tools-version: 6.0
import PackageDescription

// Chromium Embedded Framework, fetched by Scripts/bootstrap.sh.
let cef = "\(Context.packageDirectory)/Vendor/cef"

let cefCompile: [CXXSetting] = [
    .unsafeFlags(["-I", cef, "-fno-rtti", "-Wno-deprecated-declarations"]),
]
let cefLink: [LinkerSetting] = [
    .unsafeFlags(["-L\(cef)/build/libcef_dll_wrapper", "-lcef_dll_wrapper"]),
    .linkedFramework("AppKit"),
]

let package = Package(
    name: "Mosaic",
    platforms: [.macOS(.v14)],
    targets: [
        // Engine-independent logic: search engines, address parsing, CRX conversion,
        // Gecko remote control. No UI, fully unit-testable.
        .target(name: "MosaicCore"),

        // Objective-C++ shim that embeds Chromium (CEF) and exposes it to Swift.
        .target(name: "CEFBridge", cxxSettings: cefCompile, linkerSettings: cefLink),

        // Chromium sub-process (renderer / GPU / utility) entry point.
        .executableTarget(name: "MosaicHelper", cxxSettings: cefCompile, linkerSettings: cefLink),

        // The SwiftUI app.
        .executableTarget(name: "Mosaic", dependencies: ["MosaicCore", "CEFBridge"]),

        .testTarget(name: "MosaicCoreTests", dependencies: ["MosaicCore"]),
    ],
    swiftLanguageModes: [.v5],
    cxxLanguageStandard: .cxx20
)
