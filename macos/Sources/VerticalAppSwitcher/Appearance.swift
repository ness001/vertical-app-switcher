import AppKit
import SwiftUI

enum SwitcherAppearance: String, Codable, CaseIterable, Identifiable {
    case system
    case light
    case dark
    case midnight

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: return "System"
        case .light: return "Light"
        case .dark: return "Dark"
        case .midnight: return "Midnight"
        }
    }

    var preferredColorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark, .midnight: return .dark
        }
    }

    var appKitAppearance: NSAppearance? {
        switch self {
        case .system: return nil
        case .light: return NSAppearance(named: .aqua)
        case .dark, .midnight: return NSAppearance(named: .darkAqua)
        }
    }

    var accent: Color {
        switch self {
        case .system: return Color(nsColor: .controlAccentColor)
        case .light: return Color(red: 0.36, green: 0.28, blue: 0.78)
        case .dark: return Color(red: 0.70, green: 0.65, blue: 1.0)
        case .midnight: return Color(red: 0.50, green: 0.73, blue: 1.0)
        }
    }

    var backgroundTint: Color {
        switch self {
        case .system: return .clear
        case .light: return .white.opacity(0.20)
        case .dark: return .black.opacity(0.12)
        case .midnight: return Color(red: 0.055, green: 0.085, blue: 0.15).opacity(0.88)
        }
    }

    var selectionFill: Color {
        accent.opacity(self == .light || self == .system ? 0.13 : 0.18)
    }

    var selectionStroke: Color {
        accent.opacity(self == .light || self == .system ? 0.35 : 0.45)
    }
}
