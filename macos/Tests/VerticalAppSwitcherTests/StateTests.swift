import Foundation
import XCTest
@testable import VerticalAppSwitcher

final class StateTests: XCTestCase {
    private let first = SavedApp(path: "/Applications/First.app", name: "First")
    private let second = SavedApp(path: "/Applications/Second.app", name: "Second")

    func testOpeningIgnoresFocusFromAnotherForegroundApp() {
        let windows = [
            WindowEntry(id: "A", app: first, pid: 101, title: "A", window: nil, minimized: false, hidden: false),
            WindowEntry(id: "B", app: second, pid: 102, title: "B", window: nil, minimized: false, hidden: false)
        ]
        XCTAssertEqual(SwitcherModel.openingSelection(in: windows, focusedID: "A", scannedPID: 101, frontPID: 102), "A")
        XCTAssertEqual(SwitcherModel.openingSelection(in: windows, focusedID: "A", scannedPID: 101, frontPID: 101), "B")
        XCTAssertNil(SwitcherModel.openingSelection(in: [], focusedID: nil, scannedPID: nil, frontPID: nil))
    }

    func testPinOrderAndRemoval() {
        var state = SavedState()
        state.togglePin(first)
        state.togglePin(second)
        XCTAssertEqual(state.pins, [first, second])
        state.togglePin(first)
        XCTAssertEqual(state.pins, [second])
    }

    func testRecentDeduplicationLimitAndPinIndependence() {
        var state = SavedState(pins: [first])
        for index in 0..<15 {
            state.recordClosed(SavedApp(path: "/Applications/\(index).app", name: "App \(index)"))
        }
        XCTAssertEqual(state.recent.count, 12)
        XCTAssertEqual(state.recent.first?.name, "App 14")
        state.recordClosed(first)
        state.recordClosed(first)
        XCTAssertEqual(state.recent.filter { $0.path == first.path }.count, 1)
        state.recent.removeAll { $0.path == first.path }
        XCTAssertEqual(state.pins, [first])
    }

    func testTemporaryStoreRoundTripAndInvalidData() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = StateStore(directory: directory)
        XCTAssertTrue(try store.load().pins.isEmpty)
        let state = SavedState(pins: [first], recent: [second], shortcut: .controlShiftSpace)
        try store.save(state)
        let restored = try store.load()
        XCTAssertEqual(restored.pins, state.pins)
        XCTAssertEqual(restored.recent, state.recent)
        XCTAssertEqual(restored.shortcut, .controlShiftSpace)
        try Data("invalid JSON".utf8).write(to: store.file)
        XCTAssertThrowsError(try store.load())
    }

    func testModelFilteringAndMissingNumberDoNotActivateAnotherEntry() {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let model = SwitcherModel(store: StateStore(directory: directory), demo: true)
        model.query = "SAFARI"
        XCTAssertEqual(model.entries.count, 1)
        model.reconcileSelection()
        let selection = model.selectedID
        model.activateNumber(8)
        XCTAssertEqual(model.selectedID, selection)
        model.query = "no matching fixture"
        model.reconcileSelection()
        XCTAssertTrue(model.entries.isEmpty)
        XCTAssertNil(model.selectedID)
    }
}
