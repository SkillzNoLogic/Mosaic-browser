import Foundation
import Testing
@testable import MosaicCore

/// A stand-in for Gecko's debugging server: speaks just enough of the remote
/// debugging protocol over a Unix socket to exercise `RDPClient`.
final class FakeGecko: @unchecked Sendable {
    let path: String
    /// Send the root greeting only after the first request has arrived, the
    /// ordering that once made the client mistake the greeting for a reply.
    var greetLate = false
    /// Close the connection instead of answering an evaluation of this text.
    var hangUpOn: String?

    static let longText = String(repeating: "mosaic ", count: 3000)

    private let listener: Int32
    private var connection: Int32 = -1
    private var counter = 0

    init() throws {
        path = (NSTemporaryDirectory() as NSString)
            .appendingPathComponent("mrdp-\(UUID().uuidString.prefix(8)).sock")
        listener = socket(AF_UNIX, SOCK_STREAM, 0)
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { $0.copyBytes(from: path.utf8) }
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(listener, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard bound == 0, listen(listener, 1) == 0 else {
            throw RDPClient.RDPError.connectFailed("fake server could not listen")
        }
        let thread = Thread { [self] in serve() }
        thread.start()
    }

    func stop() {
        if connection >= 0 { shutdown(connection, SHUT_RDWR) }
        close(listener)
        unlink(path)
    }

    private func serve() {
        connection = accept(listener, nil, nil)
        guard connection >= 0 else { return }
        // Tests close the client while this server may still be writing; without this the
        // write raises SIGPIPE and takes the whole test process down.
        var noSigPipe: Int32 = 1
        setsockopt(connection, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
        var greeted = false
        let greeting: [String: Any] = ["from": "root", "applicationType": "browser", "traits": [:] as [String: Any]]
        if !greetLate {
            send(greeting)
            greeted = true
        }
        var buffer = [UInt8]()
        var chunk = [UInt8](repeating: 0, count: 8192)
        while true {
            let count = read(connection, &chunk, chunk.count)
            if count <= 0 { break }
            buffer.append(contentsOf: chunk[0..<count])
            while let colon = buffer.firstIndex(of: UInt8(ascii: ":")),
                  let length = Int(String(decoding: buffer[..<colon], as: UTF8.self)),
                  buffer.count >= colon + 1 + length
            {
                let body = Data(buffer[(colon + 1)..<(colon + 1 + length)])
                buffer.removeSubrange(..<(colon + 1 + length))
                guard let packet = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] else { continue }
                if !greeted {
                    send(greeting)
                    greeted = true
                }
                if !handle(packet) { return }
            }
        }
    }

    /// Returns false when the connection was deliberately dropped.
    private func handle(_ packet: [String: Any]) -> Bool {
        let to = packet["to"] as? String ?? ""
        let type = packet["type"] as? String ?? ""
        switch (to, type) {
        case ("root", "getProcess"):
            send(["from": "root", "processDescriptor": ["actor": "process1", "id": 0, "isParent": true]])
        case ("process1", "getTarget"):
            send(["from": "process1", "process": ["actor": "target1", "consoleActor": "console1"]])
        case ("console1", "evaluateJSAsync"):
            let text = packet["text"] as? String ?? ""
            if text == hangUpOn {
                shutdown(connection, SHUT_RDWR)
                return false
            }
            counter += 1
            let id = "result\(counter)"
            send(["from": "console1", "resultID": id])
            var event: [String: Any] = ["from": "console1", "type": "evaluationResult", "resultID": id]
            switch text {
            case "1+1": event["result"] = 2
            case "str": event["result"] = "hello"
            case "flag": event["result"] = true
            case "undef": event["result"] = ["type": "undefined"]
            case "long":
                event["result"] = [
                    "type": "longString", "actor": "long1", "length": Self.longText.count,
                    "initial": String(Self.longText.prefix(1000)),
                ]
            case "reject":
                // What Gecko sends when an awaited promise rejects.
                event["topLevelAwaitRejected"] = true
            case "throw":
                event["hasException"] = true
                event["exceptionMessage"] = "Error: boom"
                event["result"] = ["type": "undefined"]
            case "noise":
                // Unrelated events from the same actor must be ignored.
                send(["from": "console1", "type": "consoleAPICall", "message": ["level": "log"]])
                event["result"] = "after noise"
            default: event["result"] = "echo:\(text)"
            }
            send(event)
        case ("long1", "substring"):
            let start = packet["start"] as? Int ?? 0
            let end = packet["end"] as? Int ?? 0
            let text = Self.longText
            let from = text.index(text.startIndex, offsetBy: start)
            let upTo = text.index(text.startIndex, offsetBy: min(end, text.count))
            send(["from": "long1", "substring": String(text[from..<upTo])])
        default:
            send(["from": to, "error": "noSuchActor", "message": "No such actor for ID: \(to)"])
        }
        return true
    }

    private func send(_ packet: [String: Any]) {
        guard let body = try? JSONSerialization.data(withJSONObject: packet) else { return }
        var frame = Data("\(body.count):".utf8)
        frame.append(body)
        frame.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let written = write(connection, buffer.baseAddress! + offset, buffer.count - offset)
                if written <= 0 { return }
                offset += written
            }
        }
    }
}

@Suite struct RDPClientTests {
    private func connect(_ configure: (FakeGecko) -> Void = { _ in }) async throws -> (RDPClient, FakeGecko) {
        let server = try FakeGecko()
        configure(server)
        let client = try RDPClient(unixSocketPath: server.path)
        try await client.attachToParentProcess()
        return (client, server)
    }

    @Test func evaluatesPrimitives() async throws {
        let (client, server) = try await connect()
        defer { client.close(); server.stop() }
        #expect(try await client.evaluate("1+1") as? Int == 2)
        #expect(try await client.evaluate("str") as? String == "hello")
        #expect(try await client.evaluate("flag") as? Bool == true)
        #expect(try await client.evaluate("undef") == nil)
    }

    @Test func fetchesLongStringsInFull() async throws {
        let (client, server) = try await connect()
        defer { client.close(); server.stop() }
        #expect(try await client.evaluate("long") as? String == FakeGecko.longText)
    }

    @Test func surfacesScriptExceptions() async throws {
        let (client, server) = try await connect()
        defer { client.close(); server.stop() }
        do {
            _ = try await client.evaluate("throw")
            Issue.record("expected an error")
        } catch let error as RDPClient.RDPError {
            guard case .remote(let message) = error else {
                Issue.record("wrong error: \(error)")
                return
            }
            #expect(message.contains("boom"))
        }
        // The connection stays usable after a script error.
        #expect(try await client.evaluate("str") as? String == "hello")
    }

    @Test func rejectedPromisesAreErrorsNotEmptyResults() async throws {
        let (client, server) = try await connect()
        defer { client.close(); server.stop() }
        await #expect(throws: RDPClient.RDPError.self) { _ = try await client.evaluate("reject") }
        #expect(try await client.evaluate("str") as? String == "hello")
    }

    @Test func ignoresUnrelatedEvents() async throws {
        let (client, server) = try await connect()
        defer { client.close(); server.stop() }
        #expect(try await client.evaluate("noise") as? String == "after noise")
    }

    @Test func greetingArrivingAfterTheFirstRequestIsNotTakenAsItsReply() async throws {
        let (client, server) = try await connect { $0.greetLate = true }
        defer { client.close(); server.stop() }
        #expect(try await client.evaluate("1+1") as? Int == 2)
    }

    @Test func concurrentEvaluationsGetTheirOwnResults() async throws {
        let (client, server) = try await connect()
        defer { client.close(); server.stop() }
        let results = try await withThrowingTaskGroup(of: (Int, String?).self) { group in
            for index in 0..<40 {
                group.addTask { (index, try await client.evaluate("job\(index)") as? String) }
            }
            var collected: [Int: String?] = [:]
            for try await (index, value) in group { collected[index] = value }
            return collected
        }
        for index in 0..<40 {
            #expect(results[index] == "echo:job\(index)")
        }
    }

    @Test func evaluatingBeforeAttachingFails() async throws {
        let server = try FakeGecko()
        let client = try RDPClient(unixSocketPath: server.path)
        defer { client.close(); server.stop() }
        await #expect(throws: RDPClient.RDPError.self) { _ = try await client.evaluate("1+1") }
    }

    @Test func aDroppedConnectionFailsPendingWorkAndReportsClose() async throws {
        let (client, server) = try await connect { $0.hangUpOn = "hang" }
        defer { server.stop() }
        let closed = Closed()
        client.onClose = { closed.mark() }
        await #expect(throws: RDPClient.RDPError.self) { _ = try await client.evaluate("hang") }
        await #expect(throws: RDPClient.RDPError.self) { _ = try await client.evaluate("1+1") }
        for _ in 0..<50 where !closed.value { try await Task.sleep(nanoseconds: 20_000_000) }
        #expect(closed.value)
    }

    @Test func connectingToNothingThrows() {
        #expect(throws: RDPClient.RDPError.self) {
            _ = try RDPClient(unixSocketPath: NSTemporaryDirectory() + "mosaic-no-such.sock")
        }
    }

    private final class Closed: @unchecked Sendable {
        private let lock = NSLock()
        private var flag = false
        var value: Bool { lock.withLock { flag } }
        func mark() { lock.withLock { flag = true } }
    }
}
