import FirmwareSchema
import Foundation
import HostServiceWire
import Subprocess
import System

/// Narrow host boundary: the GUI requests operations; FirmwareKit owns formats.
public nonisolated enum FirmwareTool {
    /// FirmwareKit validates/migrates stopped storage and releases ownership
    /// before DeviceRuntime spawns the helper. No live NOR writer is hosted here.
    /// The worker's report: whether the storage changed, and a quiet notice for the window, if any.
    public static func admitBoot(device: URL, managed: Bool, executable: URL) async throws
        -> FirmwareWire.BootAdmission
    {
        let data = try await run(
            FirmwareCommand.BootAdmit(
                device: device,
                recordPolicy: managed ? .managed : .standalone,
                allowRaw: !managed
            ),
            executable: executable
        )
        let report = try JSONDecoder().decode(FirmwareWire.BootAdmission.self, from: data)
        guard report.event == "admitted" else {
            throw DeviceToolsError.failed("The firmware worker did not admit this device.")
        }
        return report
    }

    /// iPhone OS 1.x: `certificate` (DER) as a system anchor in the stopped device (`firmwarekit edit --action
    /// trust-anchor`, a stopped edit through the 1.x FTL). Nothing when the device already trusts it.
    public static func trustAnchor(device: URL, certificate: URL, executable: URL) async throws -> Bool {
        let data = try await run(
            FirmwareCommand.Edit(device: device, action: .trustAnchor, recordPolicy: .managed, cert: certificate),
            executable: executable
        )
        struct Report: Decodable { let changed: Bool }
        return try JSONDecoder().decode(Report.self, from: data).changed
    }

    /// iPhone OS 1.x: `changes` into the stopped device's preferences (`firmwarekit edit --action preferences`, a
    /// stopped edit through the 1.x FTL). False when the device holds them already.
    public static func writePreferences(device: URL, changes: [GuestDefault], executable: URL) async throws -> Bool {
        let plist = changes.map { d -> [String: Any] in
            var entry: [String: Any] = ["domain": d.domain, "key": d.key]
            entry["value"] = d.value?.object
            return entry
        }
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("prefs-\(UUID().uuidString).plist")
        try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        let data = try await run(
            FirmwareCommand.Edit(device: device, action: .preferences, recordPolicy: .managed, prefs: file),
            executable: executable
        )
        struct Report: Decodable { let changed: Bool }
        return try JSONDecoder().decode(Report.self, from: data).changed
    }

    public static func run(_ command: some FirmwareCommandLine, executable: URL) async throws -> Data {
        let child = try await Subprocess.run(
            .path(FilePath(executable.path)),
            arguments: Arguments(command.arguments),
            input: .none,
            output: .string(limit: 65536),
            error: .string(limit: 65536)
        )
        guard child.terminationStatus == .exited(0) else {
            throw DeviceToolsError.failed(child.standardError.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return Data(child.standardOutput.utf8)
    }
}
