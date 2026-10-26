import AppKit
import ApplicationServices

struct WindowEntry: Identifiable {
    let id: String
    let app: SavedApp
    let pid: pid_t?
    let title: String
    let window: AXUIElement?
    let minimized: Bool
    let hidden: Bool
    var otherDesktop: Bool = false
    var appOnly: Bool = false
    var documentURL: URL? = nil
    var frame: CGRect? = nil
    var displayID: CGDirectDisplayID? = nil

    var subtitle: String {
        if appOnly { return "\(app.name) · Menu-bar app" }
        if minimized { return "\(app.name) · Minimized" }
        if hidden { return "\(app.name) · Hidden" }
        if otherDesktop { return "\(app.name) · Other Space" }
        return app.name
    }

    static func saved(_ app: SavedApp) -> WindowEntry {
        WindowEntry(id: "saved:\(app.path)", app: app, pid: nil,
                    title: app.name, window: nil, minimized: false, hidden: false)
    }

    static func recent(_ identity: ClosedWindowIdentity) -> WindowEntry {
        WindowEntry(id: "recent:\(identity.id)", app: identity.app, pid: nil,
                    title: identity.title, window: nil, minimized: false, hidden: false,
                    documentURL: identity.documentURL)
    }
}

struct CatalogSnapshot {
    var windows: [WindowEntry]
    var running: [SavedApp]
    var closedWindows: [ClosedWindowIdentity] = []
    var focusedWindowID: String? = nil
}

final class WindowCatalog {
    private var identifiers: [(element: AXUIElement, id: String)] = []
    private let closedTracker = ClosedWindowTracker()
    private let focusObserver = WindowFocusObserver()
    private let spaces = MacSpaces()

    static var isTrusted: Bool { AXIsProcessTrusted() }

    static func requestAccess() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }

    func observeFocusChanges(_ onChange: @escaping (pid_t) -> Void) {
        focusObserver.start(onChange)
    }

    func synchronizeFocusObservers() {
        focusObserver.synchronize()
    }

    func snapshot() -> CatalogSnapshot {
        var entries: [WindowEntry] = []
        var running: [SavedApp] = []
        var seen: [(element: AXUIElement, id: String)] = []
        let allApplications = NSWorkspace.shared.runningApplications
        let applications = allApplications.filter {
            $0.processIdentifier != ProcessInfo.processInfo.processIdentifier &&
            $0.activationPolicy != .prohibited && !$0.isTerminated && $0.bundleURL?.pathExtension == "app"
        }
        let runningPIDs = Set(allApplications.filter { !$0.isTerminated }.map(\.processIdentifier))
        let trusted = Self.isTrusted
        let spaceSnapshot = trusted ? spaces.snapshot() : nil
        let discoveryCandidates = trusted ? spaces.discoveryCandidates(in: spaceSnapshot) : [:]
        var runningPaths = Set<String>()
        for application in applications {
            guard let url = application.bundleURL else { continue }
            let app = SavedApp(path: url.path, name: application.localizedName ?? url.deletingPathExtension().lastPathComponent)
            if runningPaths.insert(app.path).inserted { running.append(app) }
            guard trusted else { continue }
            let axApp = AXUIElementCreateApplication(application.processIdentifier)
            AXUIElementSetMessagingTimeout(axApp, 0.15)
            // AXWindows can omit real windows on other Spaces. Keep previously verified elements
            // and supplement them with live AX elements matched to WindowServer's other-Space IDs.
            var windows = attribute(axApp, kAXWindowsAttribute) as? [AXUIElement] ?? []
            for previous in identifiers {
                var pid: pid_t = 0
                if AXUIElementGetPid(previous.element, &pid) == .success, pid == application.processIdentifier,
                   !windows.contains(where: { CFEqual($0, previous.element) }) {
                    windows.append(previous.element)
                }
            }
            let discovered = spaces.discoverWindows(pid: application.processIdentifier, known: windows,
                candidates: discoveryCandidates[application.processIdentifier] ?? [])
            windows.append(contentsOf: discovered)
            for window in windows {
                AXUIElementSetMessagingTimeout(window, 0.15)
                let role = attribute(window, kAXRoleAttribute) as? String
                let subrole = attribute(window, kAXSubroleAttribute) as? String
                guard role == kAXWindowRole,
                      subrole == kAXStandardWindowSubrole || subrole == kAXDialogSubrole else { continue }
                let id = identifiers.first(where: { CFEqual($0.element, window) })?.id ?? UUID().uuidString
                seen.append((window, id))
                let title = attribute(window, kAXTitleAttribute) as? String ?? ""
                let frame = windowFrame(window)
                entries.append(WindowEntry(id: id, app: app, pid: application.processIdentifier,
                    title: title.isEmpty ? app.name : title, window: window,
                    minimized: attribute(window, kAXMinimizedAttribute) as? Bool ?? false,
                    hidden: application.isHidden,
                    otherDesktop: spaces.otherDesktop(for: window, in: spaceSnapshot) ?? false,
                    documentURL: documentURL(window),
                    frame: frame, displayID: frame.flatMap(Self.displayID)))
            }
        }
        let menuBarApps = MenuBarAppCatalog().snapshot(applications: allApplications,
            excludingPIDs: Set(entries.compactMap(\.pid)), excludingAppPaths: Set(entries.map { $0.app.path }))
        for entry in menuBarApps {
            entries.append(WindowEntry(id: "app:\(entry.app.path):\(entry.pid)", app: entry.app,
                pid: entry.pid, title: entry.app.name, window: nil, minimized: false,
                hidden: entry.hidden, appOnly: true))
        }
        let seenIDs = Set(seen.map(\.id))
        for previous in identifiers where !seenIDs.contains(previous.id) {
            var pid: pid_t = 0
            var value: CFTypeRef?
            if AXUIElementGetPid(previous.element, &pid) == .success, runningPIDs.contains(pid),
               AXUIElementCopyAttributeValue(previous.element, kAXRoleAttribute as CFString, &value) != .invalidUIElement {
                seen.append(previous)
            }
        }
        identifiers = seen
        let closed = trusted ? closedTracker.observe(entries, runningPIDs: runningPIDs) : []
        let focused = NSWorkspace.shared.frontmostApplication.flatMap { focusedWindowID(pid: $0.processIdentifier) }
        return CatalogSnapshot(windows: entries, running: running, closedWindows: closed, focusedWindowID: focused)
    }

    func focusedWindowID(pid: pid_t) -> String? {
        guard Self.isTrusted else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.15)
        guard let value = attribute(app, kAXFocusedWindowAttribute),
              CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return identifiers.first { CFEqual($0.element, value) }?.id
    }

    func activate(_ entry: WindowEntry, completion: @escaping (String?) -> Void) {
        guard let window = entry.window, let pid = entry.pid else {
            let url = URL(fileURLWithPath: entry.app.path)
            guard FileManager.default.fileExists(atPath: url.path) else {
                completion("This app has moved or been removed. Unpin it and pin its new location.")
                return
            }
            let configuration = NSWorkspace.OpenConfiguration()
            if let documentURL = entry.documentURL {
                guard documentURL.isFileURL, FileManager.default.fileExists(atPath: documentURL.path) else {
                    completion("This document or folder has moved or been removed.")
                    return
                }
                NSWorkspace.shared.open([documentURL], withApplicationAt: url, configuration: configuration) { _, error in
                    DispatchQueue.main.async { completion(error?.localizedDescription) }
                }
            } else {
                NSWorkspace.shared.openApplication(at: url, configuration: configuration) { _, error in
                    DispatchQueue.main.async { completion(error?.localizedDescription) }
                }
            }
            return
        }
        guard Self.isTrusted else {
            completion("Accessibility access is required to switch to a specific window.")
            return
        }
        guard let application = NSRunningApplication(processIdentifier: pid), !application.isTerminated,
              application.bundleURL?.path == entry.app.path else {
            completion("This window is no longer available.")
            return
        }
        spaces.ensureCurrentSpace(for: window) { [self] error in
            guard error == nil else { completion(error); return }
            guard !application.isTerminated else {
                completion("This window is no longer available.")
                return
            }
            activateWindow(window, application: application, completion: completion)
        }
    }

    private func activateWindow(_ window: AXUIElement, application: NSRunningApplication,
                                completion: @escaping (String?) -> Void) {
        AXUIElementSetMessagingTimeout(window, 0.15)
        application.unhide()
        // Query the live state: the row may have been captured before the window was minimized.
        if attribute(window, kAXMinimizedAttribute) as? Bool == true {
            let result = AXUIElementSetAttributeValue(window, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
            guard result == .success else {
                completion("The app did not allow this window to be restored (\(result.rawValue)).")
                return
            }
        }
        let axApp = AXUIElementCreateApplication(application.processIdentifier)
        AXUIElementSetMessagingTimeout(axApp, 0.15)
        setIfSupported(window, kAXMainAttribute, kCFBooleanTrue)
        _ = AXUIElementPerformAction(window, kAXRaiseAction as CFString)
        let activated = application.activate(options: [])
        setIfSupported(axApp, kAXFocusedWindowAttribute, window)
        setIfSupported(window, kAXFocusedAttribute, kCFBooleanTrue)
        let result = AXUIElementPerformAction(window, kAXRaiseAction as CFString)
        let focused = attribute(axApp, kAXFocusedWindowAttribute).map { CFEqual($0, window) } ?? false
        if result == .success || focused {
            completion(activated ? nil : "macOS did not allow the app to become active.")
        } else {
            completion("The app did not allow this window to be raised (\(result.rawValue)).")
        }
    }

    func close(_ entry: WindowEntry) -> String? {
        guard let window = entry.window else { return "Only an open window can be closed." }
        AXUIElementSetMessagingTimeout(window, 0.15)
        guard let button = attribute(window, kAXCloseButtonAttribute),
              CFGetTypeID(button) == AXUIElementGetTypeID() else {
            return "This window does not expose a close button. Close it from its app instead."
        }
        let element = unsafeBitCast(button, to: AXUIElement.self)
        AXUIElementSetMessagingTimeout(element, 0.15)
        let result = AXUIElementPerformAction(element, kAXPressAction as CFString)
        return result == .success ? nil : "The app declined the close request (\(result.rawValue))."
    }

    private func setIfSupported(_ element: AXUIElement, _ name: String, _ value: CFTypeRef) {
        var settable = DarwinBoolean(false)
        if AXUIElementIsAttributeSettable(element, name as CFString, &settable) == .success, settable.boolValue {
            _ = AXUIElementSetAttributeValue(element, name as CFString, value)
        }
    }

    private func documentURL(_ window: AXUIElement) -> URL? {
        // Finder and document apps may expose a file URL; never guess a path from a title.
        for name in [kAXDocumentAttribute, kAXURLAttribute] {
            let value = attribute(window, name)
            let url = (value as? URL) ?? (value as? String).flatMap(URL.init(string:))
            if let url, url.isFileURL { return url.standardizedFileURL }
        }
        return nil
    }

    private func windowFrame(_ window: AXUIElement) -> CGRect? {
        guard let position = attribute(window, kAXPositionAttribute), let size = attribute(window, kAXSizeAttribute),
              CFGetTypeID(position) == AXValueGetTypeID(), CFGetTypeID(size) == AXValueGetTypeID() else { return nil }
        let positionValue = unsafeBitCast(position, to: AXValue.self)
        let sizeValue = unsafeBitCast(size, to: AXValue.self)
        guard AXValueGetType(positionValue) == .cgPoint, AXValueGetType(sizeValue) == .cgSize else { return nil }
        var point = CGPoint.zero
        var dimensions = CGSize.zero
        guard AXValueGetValue(positionValue, .cgPoint, &point), AXValueGetValue(sizeValue, .cgSize, &dimensions) else { return nil }
        return CGRect(origin: point, size: dimensions)
    }

    private static func displayID(_ frame: CGRect) -> CGDirectDisplayID? {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return nil }
        var displays = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &displays, &count) == .success else { return nil }
        return displays.prefix(Int(count)).compactMap { display -> (CGDirectDisplayID, CGFloat)? in
            let overlap = frame.intersection(CGDisplayBounds(display))
            return overlap.isNull || overlap.isEmpty ? nil : (display, overlap.width * overlap.height)
        }.max { $0.1 < $1.1 }?.0
    }

    private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
        return value
    }
}
