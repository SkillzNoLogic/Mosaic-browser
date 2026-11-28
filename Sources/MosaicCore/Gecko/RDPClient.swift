import Foundation

/// Minimal client for the Firefox Remote Debugging Protocol, spoken over a Unix
/// domain socket (`firefox --start-debugger-server <path>`).
///
/// Mosaic uses exactly one capability of it: evaluating privileged JavaScript in
/// Gecko's parent process, which is how the Mosaic agent (`agent.sys.mjs`) is
/// loaded and driven. Packets are `<byte length>:<json>`.
public final class RDPClient: @unchecked Sendable {
    public enum RDPError: Error, LocalizedError {
        case connectFailed(String)
        case closed
        case remote(String)
        case protocolError(String)

        public var errorDescription: String? {
            switch self {
            case .connectFailed(let why): "Could not connect to Gecko: \(why)"
            case .closed: "The connection to Gecko was closed."
            case .remote(let why): "Gecko reported an error: \(why)"
            case .protocolError(let why): "Unexpected reply from Gecko: \(why)"
            }
        }
    }

    private typealias Packet = [String: Any]
    private typealias Waiter = CheckedContinuation<Packet, Error>

    private enum Pending {
        /// Resumed with the actor's direct reply.
        case reply(Waiter)
        /// The direct reply only carries a `resultID`; the value arrives later in
        /// an `evaluationResult` event.
        case evaluation(Waiter)
    }

    private let fd: Int32
    private let lock = NSLock()
    private var pending: [String: [Pending]] = [:]
    private var evaluations: [String: Waiter] = [:]
    private var isClosed = false
    private var consoleActor: String?

    /// Called once, on a background thread, when the socket goes away.
    public var onClose: (@Sendable () -> Void)?

    /// Connects synchronously. Throws if nothing is listening yet, so callers
    /// retry while Gecko starts up.
    public init(unixSocketPath path: String) throws {
        let descriptor = socket(AF_UNIX, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw RDPError.connectFailed(String(cString: strerror(errno))) }

        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard path.utf8.count < capacity else {
            Darwin.close(descriptor)
            throw RDPError.connectFailed("socket path is too long")
        }
        withUnsafeMutableBytes(of: &address.sun_path) { buffer in
            buffer.copyBytes(from: path.utf8)
        }
        let length = socklen_t(MemoryLayout<sockaddr_un>.size)
        let status = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(descriptor, $0, length) }
        }
        guard status == 0 else {
            let reason = String(cString: strerror(errno))
            Darwin.close(descriptor)
            throw RDPError.connectFailed(reason)
        }

        var noSigPipe: Int32 = 1
        setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))

        fd = descriptor
        let reader = Thread { [weak self] in self?.readLoop(descriptor) }
        reader.name = "mosaic.gecko.rdp"
        reader.start()
    }

    deinit { close() }

    public func close() {
        lock.lock()
        let alreadyClosed = isClosed
        isClosed = true
        lock.unlock()
        if !alreadyClosed { shutdown(fd, SHUT_RDWR) }
    }

    // MARK: - Requests

    /// Finds the parent-process console, the actor that evaluates privileged script.
    public func attachToParentProcess() async throws {
        let process = try await send(["to": "root", "type": "getProcess", "id": 0], as: Pending.reply)
        guard let descriptor = (process["processDescriptor"] as? Packet)?["actor"] as? String else {
            throw RDPError.protocolError("no parent process descriptor")
        }
        let target = try await send(["to": descriptor, "type": "getTarget"], as: Pending.reply)
        guard let console = (target["process"] as? Packet)?["consoleActor"] as? String else {
            throw RDPError.protocolError("no console actor for the parent process")
        }
        lock.withLock { consoleActor = console }
    }

    /// Evaluates `script` with system privileges in Gecko's parent process and
    /// returns its value. Promises are awaited. Strings, numbers and booleans come
    /// back as such; `undefined`/`null` as nil; anything else as its grip dictionary.
    public func evaluate(_ script: String) async throws -> Any? {
        let console = lock.withLock { consoleActor }
        guard let console else { throw RDPError.protocolError("not attached to the parent process") }

        let packet = try await send(
            ["to": console, "type": "evaluateJSAsync", "text": script, "mapped": ["await": true]],
            as: Pending.evaluation)

        if (packet["hasException"] as? Bool) == true {
            throw RDPError.remote(Self.describe(packet["exceptionMessage"]) ?? "script threw an exception")
        }
        // An awaited promise that rejects is not reported as an exception: the
        // reply only carries this flag, with no message or value.
        if (packet["topLevelAwaitRejected"] as? Bool) == true {
            throw RDPError.remote("an awaited promise was rejected")
        }
        return try await unwrap(packet["result"])
    }

    private func unwrap(_ value: Any?) async throws -> Any? {
        guard let grip = value as? Packet, let type = grip["type"] as? String else {
            return value is NSNull ? nil : value
        }
        switch type {
        case "undefined", "null":
            return nil
        case "longString":
            // Strings over 10 000 characters arrive as a handle to fetch in full.
            guard let actor = grip["actor"] as? String, let length = grip["length"] as? Int else {
                return grip["initial"]
            }
            let reply = try await send(
                ["to": actor, "type": "substring", "start": 0, "end": length], as: Pending.reply)
            return reply["substring"] ?? grip["initial"]
        default:
            return grip
        }
    }

    private static func describe(_ value: Any?) -> String? {
        if let text = value as? String { return text }
        if let grip = value as? Packet { return (grip["initial"] as? String) ?? "\(grip)" }
        return nil
    }

    private func send(_ packet: Packet, as kind: @escaping (Waiter) -> Pending) async throws -> Packet {
        guard let actor = packet["to"] as? String else { throw RDPError.protocolError("packet has no target") }
        let body = try JSONSerialization.data(withJSONObject: packet)
        var frame = Data("\(body.count):".utf8)
        frame.append(body)

        return try await withCheckedThrowingContinuation { (continuation: Waiter) in
            lock.lock()
            if isClosed {
                lock.unlock()
                continuation.resume(throwing: RDPError.closed)
                return
            }
            // Replies carry no request ID; each actor answers in order, so the
            // queue position and the write must be decided under the same lock.
            pending[actor, default: []].append(kind(continuation))
            let written = frame.withUnsafeBytes { buffer -> Bool in
                var offset = 0
                while offset < buffer.count {
                    let count = write(fd, buffer.baseAddress! + offset, buffer.count - offset)
                    if count < 0 && errno == EINTR { continue }
                    if count <= 0 { return false }
                    offset += count
                }
                return true
            }
            lock.unlock()
            if !written { close() }
        }
    }

    // MARK: - Reading

    private func readLoop(_ descriptor: Int32) {
        var buffer = [UInt8]()
        var chunk = [UInt8](repeating: 0, count: 64 * 1024)

        reading: while true {
            let count = read(descriptor, &chunk, chunk.count)
            if count < 0 && errno == EINTR { continue }
            if count <= 0 { break }
            buffer.append(contentsOf: chunk[0..<count])

            while let colon = buffer.firstIndex(of: UInt8(ascii: ":")) {
                guard let header = String(bytes: buffer[..<colon], encoding: .ascii),
                      let length = Int(header)
                else { break reading }  // bulk packets are never requested; treat as fatal
                let end = colon + 1 + length
                guard buffer.count >= end else { break }
                let body = Data(buffer[(colon + 1)..<end])
                buffer.removeSubrange(..<end)
                if let packet = (try? JSONSerialization.jsonObject(with: body)) as? Packet {
                    dispatch(packet)
                }
            }
        }
        failAll()
        Darwin.close(descriptor)
    }

    private func dispatch(_ packet: Packet) {
        guard let from = packet["from"] as? String else { return }
        // The root actor greets every new connection. It is not a reply, and
        // must not be mistaken for the answer to a request sent before it arrived.
        if from == "root", packet["applicationType"] != nil { return }
        let failure = packet["error"] as? String

        // Unsolicited events are the packets that carry a `type` and no error.
        if failure == nil, let type = packet["type"] as? String {
            guard type == "evaluationResult", let id = packet["resultID"] as? String else { return }
            lock.lock()
            let waiter = evaluations.removeValue(forKey: id)
            lock.unlock()
            waiter?.resume(returning: packet)
            return
        }

        lock.lock()
        guard var queue = pending[from], !queue.isEmpty else {
            lock.unlock()
            return
        }
        let next = queue.removeFirst()
        pending[from] = queue

        if let failure {
            lock.unlock()
            let message = (packet["message"] as? String).map { "\(failure): \($0)" } ?? failure
            switch next {
            case .reply(let waiter), .evaluation(let waiter):
                waiter.resume(throwing: RDPError.remote(message))
            }
            return
        }

        switch next {
        case .reply(let waiter):
            lock.unlock()
            waiter.resume(returning: packet)
        case .evaluation(let waiter):
            if let id = packet["resultID"] as? String {
                evaluations[id] = waiter
                lock.unlock()
            } else {
                lock.unlock()
                waiter.resume(throwing: RDPError.protocolError("evaluation reply had no resultID"))
            }
        }
    }

    private func failAll() {
        lock.lock()
        isClosed = true
        let waiting = pending.values.flatMap { $0 }
        let evaluating = Array(evaluations.values)
        pending.removeAll()
        evaluations.removeAll()
        let handler = onClose
        onClose = nil
        lock.unlock()

        for entry in waiting {
            switch entry {
            case .reply(let waiter), .evaluation(let waiter): waiter.resume(throwing: RDPError.closed)
            }
        }
        for waiter in evaluating { waiter.resume(throwing: RDPError.closed) }
        handler?()
    }
}
