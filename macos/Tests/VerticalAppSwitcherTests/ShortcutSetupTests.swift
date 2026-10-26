import Carbon
import Foundation
import XCTest
@testable import VerticalAppSwitcher

final class ShortcutSetupTests: XCTestCase {
    func testLegacyPresetsDecodeAndKeepTheirStringEncoding() throws {
        for name in ["controlBacktick", "controlOptionSpace", "controlShiftSpace", "commandShiftSpace"] {
            let data = Data("\"\(name)\"".utf8)
            let value = try JSONDecoder().decode(Shortcut.self, from: data)
            XCTAssertTrue(Shortcut.allCases.contains(value))
            XCTAssertEqual(try JSONEncoder().encode(value), data)
            let state = Data("{\"pins\":[],\"recent\":[],\"shortcut\":\"\(name)\"}".utf8)
            XCTAssertEqual(try JSONDecoder().decode(SavedState.self, from: state).shortcut, value)
        }
    }

    func testCustomAndDisabledShortcutsRoundTrip() throws {
        for value: Shortcut in [.none, .custom(keyCode: UInt32(kVK_ANSI_K), modifiers: UInt32(controlKey | optionKey))] {
            let state = SavedState(shortcut: value)
            let data = try JSONEncoder().encode(state)
            XCTAssertEqual(try JSONDecoder().decode(SavedState.self, from: data).shortcut, value)
        }
        XCTAssertThrowsError(try JSONDecoder().decode(Shortcut.self, from: Data("\"unknown\"".utf8)))
    }

    func testRejectsUnsafeAndReservedCombinations() {
        for value: Shortcut in [
            .custom(keyCode: UInt32(kVK_ANSI_K), modifiers: 0),
            .custom(keyCode: UInt32(kVK_ANSI_K), modifiers: UInt32(shiftKey)),
            .custom(keyCode: UInt32(kVK_Command), modifiers: UInt32(cmdKey)),
            .custom(keyCode: 128, modifiers: UInt32(cmdKey)),
            .custom(keyCode: UInt32(kVK_Tab), modifiers: UInt32(cmdKey)),
            .custom(keyCode: UInt32(kVK_Space), modifiers: UInt32(cmdKey)),
            .custom(keyCode: UInt32(kVK_ANSI_3), modifiers: UInt32(cmdKey | shiftKey))
        ] { XCTAssertNotNil(GlobalShortcut.validationError(value)) }
        XCTAssertNil(GlobalShortcut.validationError(.none))
        for value in Shortcut.allCases { XCTAssertNil(GlobalShortcut.validationError(value)) }
        XCTAssertNil(GlobalShortcut.validationError(.custom(keyCode: UInt32(kVK_ANSI_K), modifiers: UInt32(controlKey | optionKey))))
    }
}
