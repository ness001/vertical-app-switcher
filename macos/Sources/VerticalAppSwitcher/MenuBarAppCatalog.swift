import AppKit
import ApplicationServices

struct MenuBarAppEntry {
    let app: SavedApp
    let pid: pid_t
    let hidden: Bool
    let statusWindowIDs: [CGWindowID]
}

struct MenuBarAppCatalog {
    func snapshot(applications: [NSRunningApplication], excludingPIDs: Set<pid_t> = [],
                  excludingAppPaths: Set<String> = []) -> [MenuBarAppEntry] {
        let statusWindows = statusWindowOwners()
        let trusted = AXIsProcessTrusted()
        var represented = excludingAppPaths
        var entries: [MenuBarAppEntry] = []
        for application in applications {
            let pid = application.processIdentifier
            guard pid != ProcessInfo.processInfo.processIdentifier,
                  !excludingPIDs.contains(pid), !application.isTerminated,
                  let url = application.bundleURL, url.pathExtension == "app",
                  !represented.contains(url.path) else { continue }
            let windowIDs = statusWindows[pid] ?? []
            // Activation policy alone does not establish ownership of a menu-bar item.
            guard !windowIDs.isEmpty || (trusted && hasMenuBarItem(pid: pid)) else { continue }
            represented.insert(url.path)
            let app = SavedApp(path: url.path,
                name: application.localizedName ?? url.deletingPathExtension().lastPathComponent)
            entries.append(MenuBarAppEntry(app: app, pid: pid, hidden: application.isHidden,
                                           statusWindowIDs: windowIDs))
        }
        return entries.sorted { $0.app.name.localizedStandardCompare($1.app.name) == .orderedAscending }
    }

    private func hasMenuBarItem(pid: pid_t) -> Bool {
        let application = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(application, 0.05)
        guard let value = attribute(application, kAXExtrasMenuBarAttribute),
              CFGetTypeID(value) == AXUIElementGetTypeID() else { return false }
        let menuBar = unsafeBitCast(value, to: AXUIElement.self)
        AXUIElementSetMessagingTimeout(menuBar, 0.05)
        guard let children = attribute(menuBar, kAXChildrenAttribute) as? [AXUIElement] else { return false }
        // Query extras, not the ordinary application menu bar. Hidden extras can remain in this tree.
        return children.contains { child in
            AXUIElementSetMessagingTimeout(child, 0.05)
            var owner: pid_t = 0
            return AXUIElementGetPid(child, &owner) == .success && owner == pid &&
                attribute(child, kAXRoleAttribute) as? String == kAXMenuBarItemRole
        }
    }

    private func statusWindowOwners() -> [pid_t: [CGWindowID]] {
        // All windows includes offscreen status items; this reads metadata, never window images.
        let windows = CGWindowListCopyWindowInfo([.optionAll, .excludeDesktopElements], kCGNullWindowID)
            as? [[String: Any]] ?? []
        let displays = displayBounds()
        let menuBars = windows.compactMap { info -> CGRect? in
            guard info[kCGWindowLayer as String] as? Int32 == CGWindowLevelForKey(.mainMenuWindow),
                  let frame = bounds(info), frame.height > 0,
                  displays.contains(where: {
                      abs(frame.minY - $0.minY) <= 1 && abs(frame.minX - $0.minX) <= 1 &&
                      abs(frame.width - $0.width) <= 1 && frame.height < $0.height
                  }) else { return nil }
            return frame
        }
        var owners: [pid_t: Set<CGWindowID>] = [:]
        for info in windows {
            guard info[kCGWindowLayer as String] as? Int32 == CGWindowLevelForKey(.statusWindow),
                  let pid = info[kCGWindowOwnerPID as String] as? pid_t,
                  let id = info[kCGWindowNumber as String] as? CGWindowID,
                  let frame = bounds(info), frame.width > 0,
                  menuBars.contains(where: {
                      abs(frame.minY - $0.minY) <= 1 && abs(frame.height - $0.height) <= 1 &&
                      frame.width < $0.width
                  }) else { continue }
            // Floating panels also use the status layer; only menu-bar-shaped windows qualify.
            // Do not constrain X or require onscreen visibility: overflow managers move items offscreen.
            owners[pid, default: []].insert(id)
        }
        return owners.mapValues { $0.sorted() }
    }

    private func bounds(_ info: [String: Any]) -> CGRect? {
        guard let value = info[kCGWindowBounds as String] as? [String: Any] else { return nil }
        return CGRect(dictionaryRepresentation: value as CFDictionary)
    }

    private func displayBounds() -> [CGRect] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
        var displays = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &displays, &count) == .success else { return [] }
        return displays.prefix(Int(count)).map(CGDisplayBounds)
    }

    private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
        return value
    }
}
