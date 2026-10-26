import Foundation

struct SavedApp: Codable, Equatable, Identifiable {
    var id: String { path }
    let path: String
    var name: String
}

struct SavedState: Codable {
    var pins: [SavedApp] = []
    var recent: [SavedApp] = []
    var closedWindows: [ClosedWindowIdentity] = []
    var shortcut: Shortcut = .controlBacktick
    var appearance: SwitcherAppearance = .system

    init(pins: [SavedApp] = [], recent: [SavedApp] = [], closedWindows: [ClosedWindowIdentity] = [],
         shortcut: Shortcut = .controlBacktick, appearance: SwitcherAppearance = .system) {
        self.pins = pins
        self.recent = recent
        self.closedWindows = closedWindows.isEmpty ? recent.map { ClosedWindowIdentity(app: $0, title: $0.name) } : closedWindows
        self.shortcut = shortcut
        self.appearance = appearance
    }

    private enum CodingKeys: String, CodingKey { case pins, recent, closedWindows, shortcut, appearance }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        pins = try values.decodeIfPresent([SavedApp].self, forKey: .pins) ?? []
        recent = try values.decodeIfPresent([SavedApp].self, forKey: .recent) ?? []
        closedWindows = try values.decodeIfPresent([ClosedWindowIdentity].self, forKey: .closedWindows)
            ?? recent.map { ClosedWindowIdentity(app: $0, title: $0.name) }
        shortcut = try values.decodeIfPresent(Shortcut.self, forKey: .shortcut) ?? .controlBacktick
        appearance = try values.decodeIfPresent(SwitcherAppearance.self, forKey: .appearance) ?? .system
    }

    mutating func togglePin(_ app: SavedApp) {
        if pins.contains(where: { $0.path == app.path }) {
            pins.removeAll { $0.path == app.path }
        } else {
            pins.append(app)
        }
    }

    mutating func recordClosedWindow(_ identity: ClosedWindowIdentity) {
        closedWindows.removeAll { $0.id == identity.id }
        closedWindows.insert(identity, at: 0)
        closedWindows = Array(closedWindows.prefix(12))
    }

    mutating func recordClosed(_ app: SavedApp) {
        recordClosedWindow(ClosedWindowIdentity(app: app, title: app.name))
        recent.removeAll { $0.path == app.path }
        recent.insert(app, at: 0)
        recent = Array(recent.prefix(12))
    }
}

enum Shortcut: Codable, Hashable, CaseIterable {
    case controlBacktick, controlOptionSpace, controlShiftSpace, commandShiftSpace
    case custom(keyCode: UInt32, modifiers: UInt32)
    case none

    static let allCases: [Shortcut] = [.controlBacktick, .controlOptionSpace, .controlShiftSpace, .commandShiftSpace]

    private enum CodingKeys: String, CodingKey { case keyCode, modifiers }

    init(from decoder: Decoder) throws {
        if let value = try? decoder.singleValueContainer().decode(String.self) {
            switch value {
            case "controlBacktick": self = .controlBacktick
            case "controlOptionSpace": self = .controlOptionSpace
            case "controlShiftSpace": self = .controlShiftSpace
            case "commandShiftSpace": self = .commandShiftSpace
            case "none": self = .none
            default:
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Unknown shortcut"))
            }
        } else {
            let values = try decoder.container(keyedBy: CodingKeys.self)
            self = .custom(keyCode: try values.decode(UInt32.self, forKey: .keyCode),
                modifiers: try values.decode(UInt32.self, forKey: .modifiers))
        }
    }

    func encode(to encoder: Encoder) throws {
        let value: String
        switch self {
        case .controlBacktick: value = "controlBacktick"
        case .controlOptionSpace: value = "controlOptionSpace"
        case .controlShiftSpace: value = "controlShiftSpace"
        case .commandShiftSpace: value = "commandShiftSpace"
        case .none: value = "none"
        case let .custom(keyCode, modifiers):
            var values = encoder.container(keyedBy: CodingKeys.self)
            try values.encode(keyCode, forKey: .keyCode)
            try values.encode(modifiers, forKey: .modifiers)
            return
        }
        var container = encoder.singleValueContainer()
        try container.encode(value)
    }
}

struct StateStore {
    let directory: URL
    var file: URL { directory.appendingPathComponent("state.json") }

    static var standard: StateStore {
        StateStore(directory: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("VerticalAppSwitcher", isDirectory: true))
    }

    func load() throws -> SavedState {
        guard FileManager.default.fileExists(atPath: file.path) else { return SavedState() }
        return try JSONDecoder().decode(SavedState.self, from: Data(contentsOf: file))
    }

    func save(_ state: SavedState) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(state).write(to: file, options: .atomic)
    }
}
