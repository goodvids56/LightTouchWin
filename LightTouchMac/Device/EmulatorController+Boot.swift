import Cocoa
import DeviceRuntime
import FirmwareSchema
import HostRuntime
import HostServiceClient
import HostServiceWire
import LightTouchCore
import Observation

extension EmulatorController {
    // MARK: - Boot

    func start() {
        guard !started else { return }
        let paths = instance.paths
        do {
            try Bundled.requireStorage()
            guard instance.format <= DeviceInstance.currentFormat else { throw DeviceInstance.NewerFormat() }
            try DeviceStateStorage.checkBootPaths(
                base: paths.base,
                mutable: [
                    paths.overlay, paths.snapshot, paths.snapshotMeta, paths.snapshotTmp, paths.snapshotBad,
                    paths.usbmuxConf, paths.work, paths.lease,
                ] + [paths.writableNOR].compactMap { $0 },
                state: Bundled.stateDirectory,
                owner: instance.id
            )
        } catch {
            logEvent("storage: \(error.localizedDescription)")
            failBoot(error)
            return
        }
        started = true
        transition(to: .booting)
        notices.helperStarted()  // the last boot's notices end; a fresh helper opens the files as they are now
        let process = DeviceProcess(
            instance: instance.id,
            profile: profile,
            log: instance.paths.logs.appendingPathComponent("native.log"),
            lease: instance.paths.lease
        )
        self.process = process
        lastFrameSerial = 0
        process.onDeath = { [weak self, weak process] reason in
            Task.detached { PreparedDeviceBoot.syncStorage(overlay: paths.overlay, writableNOR: paths.writableNOR) }
            guard let self, let process, self.process === process else { return }
            helperDied(reason)
        }
        process.onAudio = { [weak self] event in self?.audioSink?(event) }
        process.onGuestPasteboard = { [weak self] text in
            guard let self else { return }
            ClipboardSharing.shared.guestCopied(text, on: self)
        }
        startStatusPoll()
        // Low space doesn't stop a boot; it's said before writes start failing.
        warnIfLowOnSpace()
        // The boot is built after the hello: usbmuxd must listen before the guest's USB.
        readiness.setStatus("Preparing device…")
        process.start(
            { [weak self] info in self?.bootConfiguration(hardware: info.deviceInfo) },
            preparation: { [weak self] in
                guard let self, !self.releasing, !self.stopped else { throw CancellationError() }
                guard let executable = FirmwareJobs.preparer else {
                    throw DeviceToolsError.failed("The firmware worker is unavailable.")
                }
                // The boot configuration (on main, after the helper's hello) reads the unpacked guest tools: their
                // first read hashes guest.aar, so it happens here, off the main thread.
                await Self.unpackGuestTools()
                // 1.x has no agent to trust the web proxy's CA: once it exists, the stopped device takes it as an anchor.
                let ca = URL(fileURLWithPath: WebProxyConfiguration.file(in: self.proxyDirectory).path + ".ca.der")
                if self.trustsStopped, FileManager.default.fileExists(atPath: ca.path) {
                    self.readiness.setStatus("Trusting the proxy certificate…")
                    if try await FirmwareTool.trustAnchor(
                        device: self.instance.paths.directory,
                        certificate: ca,
                        executable: executable
                    ) {
                        logEvent("proxy: certificate written into the stopped device's trust store")
                    }
                    try Task.checkCancellation()
                }
                // 1.x has no agent to write the tweaks' keys either: the stopped device takes them.
                let device = self.instance.paths.directory
                await self.tweaks.writeStopped {
                    try await FirmwareTool.writePreferences(device: device, changes: $0, executable: executable)
                }
                try Task.checkCancellation()
                let admitted = try await FirmwareTool.admitBoot(
                    device: self.instance.paths.directory,
                    managed: true,
                    executable: executable
                )
                // a storage update that couldn't run (no room, or it failed): the device starts as before
                if let notice = admitted.notice { self.reportDeviceNotice(notice, for: .storageUpdate) }
                try Task.checkCancellation()
                let recordBytes = try Data(contentsOf: DeviceRecord.url(self.instance.paths.directory))
                let refreshed = try DeviceInstance.decoder.decode(DeviceInstance.self, from: recordBytes)
                guard refreshed.format <= DeviceInstance.currentFormat else { throw DeviceInstance.NewerFormat() }
                guard refreshed.id == self.instance.id, refreshed.board == self.instance.board else {
                    throw DeviceToolsError.failed("The device identity changed while preparing to start.")
                }
                self.instance = refreshed
                self.admittedStorage = try StorageBootProof.capture(recordBytes: recordBytes)
                self.onStorageGenerationChanged?()
            }
        ) { [weak self] result in
            if case .failure(let error) = result, let self { logEvent("boot: \(instance.name): \(error)") }
        }
        // The boot begins, with its watches, in bootConfiguration(), once it is built and its offer composed.
    }

    /// nil when the device can't boot; the notice says why and the state is dead.
    /// `hardware`: the helper's hello's DeviceInfo, the emulator's facts about this board.
    private func bootConfiguration(hardware: DeviceInfo?) -> BootConfig? {
        guard !isDead, !releasing else { return nil }
        link?.send(.screenVisible(screenVisible))
        proxy.forgetEndpoint()
        var config = preparedBootConfiguration(hardware: hardware)
        config?.webProxy = proxy.endpoint
        config?.storageProof = admittedStorage
        if let port = options.chooseDebugPort(booting: config != nil) {
            config?.argv += DebugPort.arguments(port: port)
            logEvent("debug port: QEMU gdbstub on 127.0.0.1:\(port)")
        }
        if config != nil {
            bootSettings = nextBootSettings
            // Stopped migration time is separate from the guest boot budget.
            cycle.begin()
            logEmulatorBuild()
        }
        return config
    }

    /// Storage preparation and argv assembly are shared with headless callers.
    /// DeviceProcess already holds the storage lease when this hello callback runs.
    /// The UDID the guest reports: the record's, or for an iPhone base prepared before its identity carried an IMEI
    /// (n90/n88 recipe 1) the one its seed-derived IMEI makes; DeviceLock.machineOptions passes that IMEI to the modem.
    var guestUDID: String? {
        if profile.kbootPhone,
            let data = try? Data(contentsOf: instance.paths.base.appendingPathComponent("identity.json")),
            let identity = try? JSONSerialization.jsonObject(with: data) as? [String: Any], identity["imei"] == nil,
            let upgraded = IPhoneIdentity.upgraded(identity)
        {
            return upgraded.udid
        }
        return instance.identity?.udid
    }

    private func preparedBootConfiguration(hardware: DeviceInfo?) -> BootConfig? {
        let prepared: PreparedDeviceBoot
        let machine: DeviceInfo
        do {
            guard let hardware else {
                throw DeviceToolsError.failed("The emulator library has no machine for this \(profile.shortName).")
            }
            machine = hardware
            prepared = try PreparedDeviceBoot.prepare(
                board: profile,
                base: instance.paths.base,
                overlay: overlayURL,
                writableNOR: instance.paths.writableNOR,
                storageKey: instance.storage.key,
                bootrom: BootRecipe.bootrom(profile.bootrom, filesRoot: Bundled.filesRoot),
                dieID: instance.identity?.dieID,
                panel: instance.panel,
                clock: tweaks.bootClock
            )
        } catch PreparedDeviceBoot.Failure.baseMismatch {
            baseImageMismatch = true
            reportDeviceNotice("This \(profile.shortName)’s data was made with an older system image.", for: .erase)
            endBoot(.unbuildable)
            return nil
        } catch {
            failBoot(error)
            return nil
        }
        // Keep the bridge listening before the guest USB starts.
        let usbSession = usbmux.start(paths: instance.paths)
        openSerialLog()
        let netdev: String?
        readSetupExpectation()
        foreground.liftsRestrict = false
        if profile.isKBoot {
            let restrict = network && expectsSetup
            netdev =
                network
                ? proxy.forward().map {
                    BootRecipe.wifiNetdev(guestForward: $0, restricted: restrict, localNetwork: localNetworkEnabled)
                } : nil
            foreground.liftsRestrict = netdev != nil && restrict
        } else {
            netdev =
                network
                ? BootRecipe.wifiNetdev(
                    guestForward: proxy.forward() ?? "",
                    restricted: false,
                    localNetwork: localNetworkEnabled
                ) : nil
        }
        do {
            return try prepared.configuration(
                hardware: machine,
                bootArgs: DeviceOptions.bootArgs(),
                usbAddress: usbSession?.guestAddress,
                wifi: network,
                guestPackage: guestPackage.compose(),
                serial: serialCapture?.argument ?? "null",
                // Every board: 16 CoreAudio buffers (186 ms) ride out a busy emulator thread. The A4 boards
                // had QEMU's default 4 (46 ms), and the iPod touch 4's sounds crackled on a slower Mac.
                audio: ["-audio", "driver=coreaudio,out.buffer-count=16"],
                netdev: netdev,
                carrier: profile.hasCellular ? carrierSettings : nil,
                refreshHz: DisplayRefresh.hz
            )
        } catch {
            failBoot(error)
            return nil
        }
    }

    /// What the next fresh helper's boot would be built with (BootSettings): the record's panel, the saved
    /// Internet choice (this device's when none is saved), the debug port and the boot arguments.
    var nextBootSettings: BootSettings {
        BootSettings(
            panel: instance.panel,
            network: NetworkAccessPreference.decided() ?? network,
            debugPort: debugPortEnabled,
            bootArgs: DeviceOptions.bootArgs()
        )
    }
    var nextStartChanged: Bool { bootSettings.map { $0 != nextBootSettings } ?? false }

    /// Whether this boot ends in Setup: iOS 5 or later, prepared without Skip Setup, on an overlay that hasn't finished
    /// it (its mark), read at every boot, so a Restart after Setup waits for the Home screen. Setup's end is watched on
    /// every boot that shows it (the mark, the readiness text), networked or not.
    func readSetupExpectation() {
        let setupDone = FileManager.default.fileExists(atPath: BootRecipe.setupDoneMark(overlay: overlayURL).path)
        expectsSetup = BootRecipe.setupPhonesHome(iosVersion: iosVersion) && !setupDone && lock?.skippedSetup != true
        foreground.setupGate = expectsSetup ? BootRecipe.SetupNetworkGate() : nil
    }

    private func openSerialLog() {
        do {
            serialCapture = try SerialLogCapture(
                url: instance.paths.logs.appendingPathComponent("serial.log"),
                watch: [BootWatch.recoveryMarker] + BootStage.serialMarkers.keys
            ) { [weak self] phrase in
                Task { @MainActor in
                    guard let self else { return }
                    switch phrase {
                    case BootWatch.recoveryMarker:
                        self.inRecovery = true
                        self.abortBoot(BootWatch.recoveryReason(self.profile))
                    default: self.noteBoot(.serial(phrase))
                    }
                }
            }
        } catch { logEvent("logging: serial capture unavailable: \(error.localizedDescription)") }
    }

    // MARK: - Files under a running device

    func startFileWatch() {
        guard fileWatch == nil, !filesMeddled else { return }
        let paths = instance.paths
        // Guest-owned files only; the app's own writes under Devices/<uuid> must not fire this.
        let nor = [paths.writableNOR].compactMap { $0 }.filter { !$0.path.hasPrefix(paths.overlay.path + "/") }
        // An image store's live NAND (overlay/nand) holds the chip files QEMU has open.
        let directories = [paths.overlay, PreparedDeviceBoot.liveNAND(overlay: paths.overlay)]
        fileWatch = DeviceFileWatch(directories: directories, files: nor, base: paths.base) { [weak self] path in
            Task { @MainActor in self?.filesChanged(path) }
        }
    }

    private func filesChanged(_ path: String) {
        guard !filesMeddled, !isDead, !isPoweredOff else { return }
        filesMeddled = true
        fileWatch = nil
        logEvent("files: \(path) changed under the running device; Stop will quit without a flush")
        reportDeviceNotice(DeviceFileWatch.notice(shortName: profile.shortName), for: .files)
    }
}
