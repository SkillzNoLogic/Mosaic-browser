// Sends one automation command to a running Mosaic started with --mosaic-automation.
//
//   swift Scripts/drive.swift navigate example.com
//   swift Scripts/drive.swift engine chromium
//   swift Scripts/drive.swift dump /tmp/mosaic-state.json
//
// Commands are listed in Sources/Mosaic/App/Automation.swift.
import Foundation

let arguments = CommandLine.arguments.dropFirst()
guard let action = arguments.first else {
    FileHandle.standardError.write(Data("usage: drive.swift <command> [value] [out-file]\n".utf8))
    exit(2)
}
var command: [String: String] = ["do": action]
if arguments.count > 1 { command["value"] = arguments[arguments.startIndex + 1] }
if arguments.count > 2 { command["out"] = arguments[arguments.startIndex + 2] }
let payload = String(data: try JSONSerialization.data(withJSONObject: command), encoding: .utf8)!
DistributedNotificationCenter.default().postNotificationName(
    Notification.Name("dev.mosaic.browser.automation"), object: payload, userInfo: nil, deliverImmediately: true)
// Give the notification server a moment to forward it before this process exits.
RunLoop.current.run(until: Date().addingTimeInterval(0.2))
