import Foundation

final class DiagnosticLog {
    static let shared = DiagnosticLog()
    private let queue = DispatchQueue(label: "app.verticalswitcher.diagnostics")
    private let directory = StateStore.standard.directory

    func write(_ message: String) {
        queue.async {
            do {
                try FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true)
                let url = self.directory.appendingPathComponent("switcher.log")
                if let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 1_048_576 {
                    try Data().write(to: url, options: .atomic)
                }
                let line = "\(ISO8601DateFormatter().string(from: Date())) \(message)\n"
                if !FileManager.default.fileExists(atPath: url.path) {
                    try Data(line.utf8).write(to: url, options: .atomic)
                } else {
                    let handle = try FileHandle(forWritingTo: url)
                    defer { try? handle.close() }
                    try handle.seekToEnd()
                    try handle.write(contentsOf: Data(line.utf8))
                }
            } catch {
                NSLog("Vertical App Switcher logging error: %@", error.localizedDescription)
            }
        }
    }
}
