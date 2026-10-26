import AppKit
import ApplicationServices

struct ClosedWindowIdentity: Codable, Equatable, Identifiable {
    let app: SavedApp
    let title: String
    let documentURL: URL?

    var id: String { documentURL.map { "\(app.path)|\($0.standardizedFileURL.absoluteString)" } ?? app.path }

    init(app: SavedApp, title: String, documentURL: URL? = nil) {
        self.app = app
        self.title = title
        self.documentURL = documentURL?.isFileURL == true ? documentURL?.standardizedFileURL : nil
    }
}

final class ClosedWindowTracker {
    private var known: [String: WindowEntry] = [:]

    func observe(_ entries: [WindowEntry], runningPIDs: Set<pid_t>) -> [ClosedWindowIdentity] {
        var current = Dictionary(entries.map { entry -> (String, WindowEntry) in
            var captured = entry
            if captured.documentURL == nil { captured.documentURL = known[entry.id]?.documentURL }
            return (entry.id, captured)
        }, uniquingKeysWith: { first, _ in first })
        var closed: [ClosedWindowIdentity] = []
        var identities = Set<String>()
        for (id, previous) in known where current[id] == nil {
            if previous.appOnly && entries.contains(where: { $0.app.path == previous.app.path }) { continue }
            let terminated = previous.pid.map { !runningPIDs.contains($0) } ?? false
            var invalid = false
            if let window = previous.window {
                var role: CFTypeRef?
                invalid = AXUIElementCopyAttributeValue(window, kAXRoleAttribute as CFString, &role) == .invalidUIElement
            }
            // AX enumeration can omit live windows during transitions or time out.
            guard terminated || invalid else {
                current[id] = previous
                continue
            }
            let identity = ClosedWindowIdentity(app: previous.app, title: previous.title, documentURL: previous.documentURL)
            if identities.insert(identity.id).inserted { closed.append(identity) }
        }
        known = current
        return closed
    }
}
