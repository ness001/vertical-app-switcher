import AppKit
import Foundation

// Standalone checks for machines with swiftc but no working SwiftPM/XCTest runtime.
@main
struct SmokeChecks {
    static func main() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("switcher-check-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = StateStore(directory: directory)
        let app = SavedApp(path: "/Applications/Test.app", name: "Test")
        var state = try store.load()
        precondition(state.pins.isEmpty)
        precondition(state.shortcut == .controlBacktick)
        precondition(state.shortcut.title == "⌃`")
        let defaultShortcut = try JSONDecoder().decode(Shortcut.self, from: JSONEncoder().encode(state.shortcut))
        precondition(defaultShortcut == .controlBacktick)
        state.togglePin(app)
        for index in 0..<15 { state.recordClosed(SavedApp(path: "/\(index).app", name: "\(index)")) }
        precondition(state.recent.count == 12)
        state.recordClosed(app)
        state.recordClosed(app)
        precondition(state.recent.filter { $0.path == app.path }.count == 1)
        state.recent.removeAll { $0.path == app.path }
        precondition(state.pins == [app])
        try store.save(state)
        let restored = try store.load()
        precondition(restored.pins == [app])
        state.togglePin(app)
        precondition(state.pins.isEmpty)
        let model = SwitcherModel(store: store, demo: true)
        model.query = "SAFARI"
        precondition(model.entries.count == 1)
        model.query = "no fixture matches"
        model.reconcileSelection()
        precondition(model.entries.isEmpty && model.selectedID == nil)
        model.activateNumber(8)
        model.query = ""
        model.changeTab(.pinned)
        precondition(model.entries.count == 2)
        model.changeTab(.recent)
        precondition(model.entries.isEmpty)
        let windows = [
            WindowEntry(id: "A", app: app, pid: 101, title: "A", window: nil, minimized: false, hidden: false),
            WindowEntry(id: "B", app: app, pid: 102, title: "B", window: nil, minimized: false, hidden: false)
        ]
        precondition(SwitcherModel.openingSelection(in: windows, focusedID: "A", scannedPID: 101, frontPID: 102) == "A")
        precondition(SwitcherModel.openingSelection(in: windows, focusedID: "A", scannedPID: 101, frontPID: 101) == "B")
        precondition(SwitcherModel.openingSelection(in: [], focusedID: nil, scannedPID: nil, frontPID: nil) == nil)
        let quitModel = SwitcherModel(store: store)
        let offline = WindowEntry.saved(app)
        quitModel.quitApp(offline)
        precondition(quitModel.message?.contains("not running") == true)
        quitModel.message = nil
        quitModel.searchFocused = true
        quitModel.quitApp(offline)
        precondition(quitModel.message == nil)
        quitModel.searchFocused = false
        let ownProcess = WindowEntry(id: "self", app: app, pid: ProcessInfo.processInfo.processIdentifier,
                                     title: "Self", window: nil, minimized: false, hidden: false)
        quitModel.quitApp(ownProcess)
        precondition(quitModel.message?.contains("not running") == true)
        let mismatched = WindowEntry(id: "mismatch", app: app,
                                     pid: NSWorkspace.shared.frontmostApplication?.processIdentifier,
                                     title: "Mismatch", window: nil, minimized: false, hidden: false)
        quitModel.message = nil
        quitModel.quitApp(mismatched)
        precondition(quitModel.message?.contains("not running") == true)
        model.message = nil
        model.quitApp(offline)
        precondition(model.message == nil)
        print("Passed: normal-quit rejects offline/self/mismatched targets; search and demo do not quit apps.")
        let legacy = Data("{\"pins\":[],\"recent\":[{\"path\":\"/Applications/Test.app\",\"name\":\"Test\"}],\"shortcut\":\"controlOptionSpace\"}".utf8)
        let migrated = try JSONDecoder().decode(SavedState.self, from: legacy)
        precondition(migrated.closedWindows.count == 1 && migrated.closedWindows[0].app == app)
        precondition(migrated.appearance == .system)
        precondition(state.appearance == .system)
        let appearanceModel = SwitcherModel(store: store)
        for appearance in SwitcherAppearance.allCases {
            precondition(appearanceModel.setAppearance(appearance))
            let saved = try store.load()
            precondition(saved.appearance == appearance)
            precondition(saved.pins == restored.pins)
            precondition(saved.recent == restored.recent)
            precondition(saved.closedWindows == restored.closedWindows)
            precondition(saved.shortcut == restored.shortcut)
        }
        let blockedDirectory = directory.appendingPathComponent("blocked")
        try Data().write(to: blockedDirectory)
        let blockedModel = SwitcherModel(store: StateStore(directory: blockedDirectory))
        precondition(!blockedModel.setAppearance(.midnight))
        precondition(blockedModel.state.appearance == .system && blockedModel.message != nil)
        precondition(SwitcherAppearance.system.preferredColorScheme == nil)
        precondition(SwitcherAppearance.system.appKitAppearance == nil)
        precondition(SwitcherAppearance.light.preferredColorScheme == .light)
        precondition(SwitcherAppearance.dark.preferredColorScheme == .dark)
        precondition(SwitcherAppearance.midnight.preferredColorScheme == .dark)
        print("Passed: legacy appearance defaults, four-preset persistence without unrelated state changes, failed-save rollback, native appearance mapping.")
        let folderA = ClosedWindowIdentity(app: app, title: "Folder A", documentURL: URL(fileURLWithPath: "/tmp/A"))
        let folderB = ClosedWindowIdentity(app: app, title: "Folder B", documentURL: URL(fileURLWithPath: "/tmp/B"))
        var folders = SavedState(pins: [app])
        folders.recordClosedWindow(folderA)
        folders.recordClosedWindow(folderB)
        folders.recordClosedWindow(folderA)
        precondition(folders.closedWindows.map(\.id) == [folderA.id, folderB.id])
        precondition(folders.pins == [app])
        model.state = folders
        model.windows = []
        model.changeTab(.pinned)
        precondition(model.entries.map(\.documentURL) == [folderA.documentURL, folderB.documentURL])
        precondition(model.entries.allSatisfy(model.isRecent))
        precondition(!model.isRecent(windows[0]))
        model.windows = []
        model.desktopWindows = [WindowEntry(id: "other-space", app: app, pid: 104, title: "Other Space", window: nil, minimized: true, hidden: true, otherDesktop: true)]
        model.changeTab(.desktops)
        precondition(model.entries.map(\.id) == ["other-space"])
        model.state.pins = [app]
        model.changeTab(.pinned)
        precondition(model.entries.map(\.id).first == "other-space")
        precondition(model.entries.count == 3)
        model.backgroundApps = [WindowEntry(id: "background", app: app, pid: 103, title: app.name, window: nil, minimized: false, hidden: false, appOnly: true)]
        model.changeTab(.tray)
        precondition(model.entries.count == 1)
        model.query = "TEST"
        precondition(model.entries(in: .tray).count == 1)
        model.query = "no match"
        precondition(model.entries(in: .tray).isEmpty)
        model.focusSearch()
        precondition(model.searchVisible && model.searchFocused)
        model.prepareToOpen()
        precondition(!model.searchVisible && !model.searchFocused && model.query.isEmpty)
        precondition(model.tab == .open && model.entries.isEmpty)
        let spaces = MacSpaces.Snapshot(displays: [
            MacSpaces.Display(identifier: "display", currentSpace: 1, spaces: [1, 2, 3])
        ])
        precondition(spaces.otherDesktop(for: [1]) == false)
        precondition(spaces.otherDesktop(for: [2]) == true)
        precondition(spaces.otherDesktop(for: [1, 2]) == false)
        precondition(spaces.otherDesktop(for: [99]) == nil)
        precondition(spaces.otherDesktop(for: []) == nil)
        precondition(spaces.destination(for: [2])?.space == 2)
        precondition(spaces.destination(for: [1]) == nil)
        print("Passed: Spaces current/other/sticky/unknown membership and destination selection; Desktops/Pinned filtering; Tray/search; state migration.")
        let invalid = Data("invalid JSON".utf8)
        try invalid.write(to: store.file)
        do {
            _ = try store.load()
            preconditionFailure("Invalid data must not silently load as defaults")
        } catch DecodingError.dataCorrupted { }
        let corruptModel = SwitcherModel(store: store)
        precondition(corruptModel.message != nil)
        corruptModel.setShortcut(.commandShiftSpace)
        precondition(!corruptModel.setAppearance(.midnight))
        precondition(corruptModel.state.appearance == .system)
        let preserved = try Data(contentsOf: store.file)
        precondition(preserved == invalid)
        print("Passed: pins, bounded/deduplicated recent history, persistence, filtering, empty selection, invalid settings preservation.")
    }
}
