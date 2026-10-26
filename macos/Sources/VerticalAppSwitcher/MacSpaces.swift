import AppKit
import ApplicationServices
import Darwin

// Private entry points are optional and resolved at runtime, never linked into the app.
final class MacSpaces {
    struct Display {
        let identifier: String
        let currentSpace: UInt64
        let spaces: Set<UInt64>
    }

    struct Snapshot {
        let displays: [Display]

        func otherDesktop(for memberships: Set<UInt64>) -> Bool? {
            guard !memberships.isEmpty, !displays.isEmpty else { return nil }
            let current = Set(displays.map(\.currentSpace))
            if !memberships.isDisjoint(with: current) { return false }
            let known = displays.reduce(into: Set<UInt64>()) { $0.formUnion($1.spaces) }
            return memberships.isSubset(of: known) ? true : nil
        }

        func destination(for memberships: Set<UInt64>) -> (display: String, space: UInt64)? {
            guard otherDesktop(for: memberships) == true else { return nil }
            for display in displays {
                if let space = display.spaces.intersection(memberships).min() {
                    return (display.identifier, space)
                }
            }
            return nil
        }
    }

    private typealias MainConnection = @convention(c) () -> Int32
    private typealias CopyDisplays = @convention(c) (Int32) -> Unmanaged<CFArray>?
    private typealias CopyWindowSpaces = @convention(c) (Int32, Int32, CFArray) -> Unmanaged<CFArray>?
    private typealias WindowID = @convention(c) (AXUIElement, UnsafeMutablePointer<CGWindowID>) -> AXError
    private typealias SetCurrentSpace = @convention(c) (Int32, CFString, UInt64) -> Void
    private typealias RemoteToken = @convention(c) (AXUIElement) -> Unmanaged<CFData>?
    private typealias RemoteElement = @convention(c) (CFData) -> Unmanaged<AXUIElement>?

    private struct Discovery {
        var targets: Set<CGWindowID>
        var nextElement: UInt64 = 0
        var limit: UInt64
        var retryAfter = Date.distantPast
    }

    private var discoveries: [pid_t: Discovery] = [:]

    private let mainConnection: MainConnection?
    private let copyDisplays: CopyDisplays?
    private let copyWindowSpaces: CopyWindowSpaces?
    private let getWindowID: WindowID?
    private let setCurrentSpace: SetCurrentSpace?
    private let remoteToken: RemoteToken?
    private let remoteElement: RemoteElement?

    init() {
        // Keep successful handles loaded for the lifetime of their function pointers.
        let skyLight = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_LAZY | RTLD_LOCAL)
        let coreGraphics = dlopen("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", RTLD_LAZY | RTLD_LOCAL)
        let accessibility = dlopen("/System/Library/Frameworks/ApplicationServices.framework/Frameworks/HIServices.framework/HIServices", RTLD_LAZY | RTLD_LOCAL)
        func symbol<T>(_ handles: [UnsafeMutableRawPointer?], _ names: [String], as type: T.Type) -> T? {
            for handle in handles.compactMap({ $0 }) {
                for name in names {
                    if let address = dlsym(handle, name) { return unsafeBitCast(address, to: type) }
                }
            }
            return nil
        }
        let graphics = [skyLight, coreGraphics]
        mainConnection = symbol(graphics, ["SLSMainConnectionID", "CGSMainConnectionID"], as: MainConnection.self)
        copyDisplays = symbol(graphics, ["SLSCopyManagedDisplaySpaces", "CGSCopyManagedDisplaySpaces"], as: CopyDisplays.self)
        copyWindowSpaces = symbol(graphics, ["SLSCopySpacesForWindows", "CGSCopySpacesForWindows"], as: CopyWindowSpaces.self)
        setCurrentSpace = symbol(graphics, ["SLSManagedDisplaySetCurrentSpace", "CGSManagedDisplaySetCurrentSpace"], as: SetCurrentSpace.self)
        getWindowID = symbol([accessibility], ["_AXUIElementGetWindow"], as: WindowID.self)
        remoteToken = symbol([accessibility], ["_AXUIElementRemoteTokenCreate"], as: RemoteToken.self)
        remoteElement = symbol([accessibility], ["_AXUIElementCreateWithRemoteToken"], as: RemoteElement.self)
    }

    func snapshot() -> Snapshot? {
        guard let connection = mainConnection?(), connection > 0,
              let values = copyDisplays?(connection)?.takeRetainedValue() as? [[String: Any]],
              !values.isEmpty else { return nil }
        var displays: [Display] = []
        for value in values {
            guard let identifier = value["Display Identifier"] as? String, !identifier.isEmpty,
                  let current = value["Current Space"] as? [String: Any],
                  let currentID = Self.spaceID(current["ManagedSpaceID"]),
                  let spaces = value["Spaces"] as? [[String: Any]], !spaces.isEmpty else { return nil }
            let ids = spaces.compactMap { Self.spaceID($0["ManagedSpaceID"]) }
            guard ids.count == spaces.count, ids.contains(currentID) else { return nil }
            displays.append(Display(identifier: identifier, currentSpace: currentID, spaces: Set(ids)))
        }
        return Snapshot(displays: displays)
    }

    func otherDesktop(for window: AXUIElement, in snapshot: Snapshot?) -> Bool? {
        guard let snapshot, let memberships = memberships(of: window) else { return nil }
        return snapshot.otherDesktop(for: memberships)
    }

    func windowID(of window: AXUIElement) -> CGWindowID? {
        var id: CGWindowID = 0
        guard getWindowID?(window, &id) == .success, id != 0 else { return nil }
        return id
    }

    func discoveryCandidates(in snapshot: Snapshot?) -> [pid_t: Set<CGWindowID>] {
        guard let snapshot,
              let metadata = CGWindowListCopyWindowInfo(.optionAll, kCGNullWindowID) as? [[String: Any]] else { return [:] }
        var result: [pid_t: Set<CGWindowID>] = [:]
        for record in metadata {
            guard let pid = record[kCGWindowOwnerPID as String] as? Int32,
                  let id = record[kCGWindowNumber as String] as? UInt32,
                  record[kCGWindowLayer as String] as? Int == 0,
                  let memberships = memberships(of: id), snapshot.otherDesktop(for: memberships) == true else { continue }
            result[pid, default: []].insert(id)
        }
        discoveries = discoveries.filter { result[$0.key] != nil }
        return result
    }

    func discoverWindows(pid: pid_t, known: [AXUIElement], candidates: Set<CGWindowID>) -> [AXUIElement] {
        let missing = candidates.subtracting(known.compactMap(windowID))
        guard !missing.isEmpty, let remoteToken, let remoteElement else { return [] }
        let seed = known.first ?? AXUIElementCreateApplication(pid)
        guard let original = remoteToken(seed)?.takeRetainedValue(),
              let roundTrip = remoteElement(original)?.takeRetainedValue(), CFEqual(seed, roundTrip) else { return [] }
        var token = original as Data
        if token.count == 12 {
            // An app with every window on another Space may expose no seed window. Cocoa's
            // window token shares the app's process header, followed by its observed object tag.
            token = Data(token.prefix(8)) + Data([0x6f, 0x63, 0x6f, 0x63]) + Data(repeating: 0, count: 8)
        }
        guard token.count == 20 else { return [] }
        // Preserve the process-specific header; every candidate still requires live AX validation.
        let seedID = token.suffix(8).enumerated().reduce(UInt64(0)) { $0 | UInt64($1.element) << ($1.offset * 8) }
        let limit = min(max(seedID, 65_536) + 65_536, 1_048_576)
        var discovery = discoveries[pid] ?? Discovery(targets: candidates, limit: limit)
        if discovery.targets != candidates {
            discovery = Discovery(targets: candidates, limit: limit)
        }
        guard Date() >= discovery.retryAfter else { return [] }
        if discovery.nextElement >= discovery.limit {
            discovery.nextElement = 0
            discovery.limit = limit
        }
        let deadline = Date().addingTimeInterval(0.08)
        let end = min(discovery.nextElement + 8_192, discovery.limit)
        var found: [AXUIElement] = []
        var remaining = missing
        // Chrome can omit other-Space windows from AXWindows. Probe a bounded token range,
        // accepting only live AX windows with the exact PID and WindowServer ID we observed.
        while discovery.nextElement < end, Date() < deadline, !remaining.isEmpty {
            var elementID = discovery.nextElement.littleEndian
            discovery.nextElement += 1
            withUnsafeBytes(of: &elementID) { token.replaceSubrange(12..<20, with: $0) }
            guard let element = remoteElement(token as CFData)?.takeRetainedValue() else { continue }
            AXUIElementSetMessagingTimeout(element, 0.02)
            var owner: pid_t = 0
            var role: CFTypeRef?
            guard AXUIElementGetPid(element, &owner) == .success, owner == pid,
                  AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role) == .success,
                  role as? String == kAXWindowRole,
                  let id = windowID(of: element), remaining.remove(id) != nil else { continue }
            found.append(element)
        }
        if discovery.nextElement >= discovery.limit || remaining.isEmpty {
            discovery.nextElement = discovery.limit
            discovery.retryAfter = Date().addingTimeInterval(30)
        }
        discoveries[pid] = discovery
        return found
    }

    func ensureCurrentSpace(for window: AXUIElement, completion: @escaping (String?) -> Void) {
        guard let snapshot = snapshot(), let memberships = memberships(of: window),
              let destination = snapshot.destination(for: memberships) else {
            // An unavailable or unrecognized private API falls back to ordinary AX activation.
            completion(nil)
            return
        }
        guard let connection = mainConnection?(), connection > 0, let setCurrentSpace else {
            completion("macOS does not expose a way to switch to this window's Space.")
            return
        }
        setCurrentSpace(connection, destination.display as CFString, destination.space)
        // The setter has no result and the transition is asynchronous. Verify before raising.
        waitForSpace(destination.space, display: destination.display, attempts: 15, completion: completion)
    }

    private func waitForSpace(_ space: UInt64, display: String, attempts: Int, completion: @escaping (String?) -> Void) {
        if snapshot()?.displays.contains(where: { $0.identifier == display && $0.currentSpace == space }) == true {
            completion(nil)
        } else if attempts > 0 {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [self] in
                waitForSpace(space, display: display, attempts: attempts - 1, completion: completion)
            }
        } else {
            completion("macOS did not switch to this window's Space. Switch Desktops manually and try again.")
        }
    }

    private func memberships(of window: AXUIElement) -> Set<UInt64>? {
        guard let id = windowID(of: window) else { return nil }
        return memberships(of: id)
    }

    private func memberships(of id: CGWindowID) -> Set<UInt64>? {
        guard let connection = mainConnection?(), connection > 0,
              // 7 includes current, other, and user Spaces, without filtering out hidden/minimized windows.
              let values = copyWindowSpaces?(connection, 7, [NSNumber(value: id)] as CFArray)?.takeRetainedValue() as? [Any],
              !values.isEmpty else { return nil }
        let spaces = values.compactMap(Self.spaceID)
        return spaces.count == values.count ? Set(spaces) : nil
    }

    private static func spaceID(_ value: Any?) -> UInt64? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.int64Value > 0 else { return nil }
        return number.uint64Value
    }
}
