import CryptoKit
import Foundation
import zlib

@main
struct UpdateChecks {
    static func reject(_ operation: () throws -> Void) {
        do { try operation(); preconditionFailure("Expected rejection") } catch { }
    }

    static func main() throws {
        let ordered = try UpdateVersion("1.10.0") > UpdateVersion("1.9.99")
        precondition(ordered)
        for version in ["01.2.3", "1.2", "1.2.3-beta", "1.2.65535", "1.2.-3"] {
            reject { _ = try UpdateVersion(version) }
        }
        let name = "VerticalAppSwitcher-1.2.3-osx-universal.zip"
        let asset = UpdateRelease.repository + "/releases/download/v1.2.3/"
        let json: [String: Any] = ["draft": false, "prerelease": false, "tag_name": "v1.2.3", "assets": [
            ["name": name, "browser_download_url": asset + name],
            ["name": name + ".sha256", "browser_download_url": asset + name + ".sha256"]]]
        let data = try JSONSerialization.data(withJSONObject: json)
        let release = try UpdateRelease.parse(data, installed: "1.2.2")
        precondition(release?.packageName == name)
        let same = try UpdateRelease.parse(data, installed: "1.2.3")
        let older = try UpdateRelease.parse(data, installed: "2.0.0")
        precondition(same == nil && older == nil)
        let prerelease = try UpdateRelease.parse(data, installed: "1.2.3-rc.1")
        precondition(prerelease?.version.description == "1.2.3")
        reject { _ = try UpdateRelease.parse(data, installed: "1.2.3-01") }
        let spoofed = String(data: data, encoding: .utf8)!.replacingOccurrences(of: "github.com", with: "github.com.evil.test")
        reject { _ = try UpdateRelease.parse(Data(spoofed.utf8), installed: "1.0.0") }
        let content = Data("verified payload".utf8)
        let hash = SHA256.hash(data: content).map { String(format: "%02x", $0) }.joined()
        try UpdateRelease.verifyChecksum(content, checksum: Data("\(hash)  \(name)\n".utf8), packageName: name)
        reject { try UpdateRelease.verifyChecksum(content, checksum: Data("\(hash)  other.zip".utf8), packageName: name) }
        reject { try UpdateRelease.verifyChecksum(Data(), checksum: Data("\(hash)  \(name)".utf8), packageName: name) }
        let initial = URL(string: asset + name)!
        precondition(UpdateRequest.trusted(initial, initial: initial))
        precondition(UpdateRequest.trusted(URL(string: "https://release-assets.githubusercontent.com/github-production-release-asset/123?signature=test")!, initial: initial))
        for url in ["http://github.com/test", "https://github.com.evil.test/test", "https://release-assets.githubusercontent.com/other", "https://user@release-assets.githubusercontent.com/github-production-release-asset/a", "https://release-assets.githubusercontent.com:444/github-production-release-asset/a"] {
            precondition(!UpdateRequest.trusted(URL(string: url)!, initial: initial))
        }
        let archive = fixture()
        try UpdateArchive.validate(archive)
        var unsafe = archive
        unsafe[30] = 47 // The local filename must exactly match the central filename.
        reject { try UpdateArchive.validate(unsafe) }
        unsafe = archive
        unsafe[unsafe.count - 1] = 1 // Archive comments are outside the package contract.
        reject { try UpdateArchive.validate(unsafe) }
        unsafe = fixture(oversized: true)
        reject { try UpdateArchive.validate(unsafe) }
        unsafe = fixture(link: true)
        reject { try UpdateArchive.validate(unsafe) }
        unsafe = fixture(corruptCRC: true)
        reject { try UpdateArchive.validate(unsafe) }
        if CommandLine.arguments.count == 2 {
            try UpdateArchive.validate(Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
        }
        print("Update checks passed: versions, release selection, URL trust, checksums, ZIP structure, size, CRC, and symlink rejection.")
    }

    private static func fixture(oversized: Bool = false, link: Bool = false, corruptCRC: Bool = false) -> Data {
        var local = Data()
        var central = Data()
        var count = 0
        func integer(_ value: UInt64, _ bytes: Int) -> Data {
            Data((0..<bytes).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
        }
        for file in UpdateArchive.files.sorted() {
            let name = Data((UpdateArchive.appName + "/" + file).utf8)
            let payload = Data("fixture".utf8)
            let checksum = payload.withUnsafeBytes { UInt64(crc32(0, $0.baseAddress!.assumingMemoryBound(to: Bytef.self), uInt(payload.count))) } + (corruptCRC ? 1 : 0)
            let size = UInt64(oversized ? UpdateArchive.maximumExpanded + 1 : payload.count)
            let offset = local.count
            local.append(integer(0x04034b50, 4))
            for number in [20, 0, 0, 0, 0] { local.append(integer(UInt64(number), 2)) }
            local.append(integer(checksum, 4)); local.append(integer(UInt64(payload.count), 4)); local.append(integer(size, 4))
            local.append(integer(UInt64(name.count), 2)); local.append(integer(0, 2)); local.append(name); local.append(payload)
            central.append(integer(0x02014b50, 4))
            for number in [0x0314, 20, 0, 0, 0, 0] { central.append(integer(UInt64(number), 2)) }
            central.append(integer(checksum, 4)); central.append(integer(UInt64(payload.count), 4)); central.append(integer(size, 4))
            central.append(integer(UInt64(name.count), 2))
            for _ in 0..<4 { central.append(integer(0, 2)) }
            central.append(integer(UInt64(link ? 0xa1ff0000 : 0x81a40000), 4)); central.append(integer(UInt64(offset), 4)); central.append(name)
            count += 1
        }
        let start = local.count
        local.append(central)
        local.append(integer(0x06054b50, 4))
        local.append(integer(0, 4))
        local.append(integer(UInt64(count), 2)); local.append(integer(UInt64(count), 2))
        local.append(integer(UInt64(central.count), 4)); local.append(integer(UInt64(start), 4)); local.append(integer(0, 2))
        return local
    }
}
