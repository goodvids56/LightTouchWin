// KBoot: the ipad1 machine's direct-kernel boot bundle, kboot.bin: what iBoot-817.29 does before it jumps
// to xnu. A port of ipad1_kboot.py (build, fill_dt, logo, main); the byte layout is documented there.
//
//   let ident = try UnitIdentity.synthesize(seed: seed)
//   try KBoot.write(decrypted: decDir, to: kbootURL, identity: ident)                // normal boot
//   try KBoot.write(decrypted: decDir, to: kbootURL, identity: ident, ramdisk: rd)   // md0 RAM-disk root
//   let img = try KBoot.build(kernel: mach, deviceTree: dt, identity: ident, iboot: v, ramdisk: nil)
//   KBoot.bundle(img, segments: logo)          // image + K48SEG segments + K48KBOOT trailer
//   KBoot.ibootVersion(iBootBin)               // chosen/firmware-version: "iBoot-N.N" out of the decrypted iBoot
//   try MachO(kernel).segments / .entry        // LC_SEGMENT list, LC_UNIXTHREAD pc
//
// decrypted: a FirmwareDecryptor output directory (kernelcache.mach, DeviceTree.bin, iBoot.bin, AppleLogo.bin).
// The kernel virtual base is the kernelcache's own link base (0xC0000000 on 3.x, 0x80000000 on 4.x).
// RAM-disk mode puts the image after the kernel, adds chosen/memory-map RAMDisk, appends rd=md0 and empties
// chosen/root-matching; the DT's own secure-root-prefix is left as the IPSW has it, so md0 is a SecureRoot.
// What differs per A4 board is a KBoot.Board, picked from the DT's own compatible (ipad1_kboot.BOARDS): K48's
// landscape panel (display-rotation 270, 4.x lays its UI out by it) or N81's portrait Retina one. A board without
// the SPI NOR (N81: boot-from-nand) gets K48's 4.x nor-flash subtree grafted in (graftNOR). The S5L8920 family
// (s5l8920_kboot.py) is Boards too: the iPhone 3GS (N88, -M n88) and the S5L8922 iPod touch 3G (N18, -M n18), each
// with its own platform-name/chip-id, a 320x480 panel and the iPad's clock table cut to its DT's 32 slots; N18 gets
// the NOR graft, N88 keeps its own NOR and has its baseband node's identity filled with test values.

import Foundation
import HostRuntime

public enum KBoot {
    public static let physBase: UInt32 = 0x4000_0000, dramSize: UInt32 = 0x1000_0000
    public static let pramSize: UInt32 = 0x4000, vramSize: UInt32 = 0x90_0000 - 0x4000
    public static let memSize = dramSize - pramSize - vramSize
    public static let vramPA = physBase + memSize, pramPA = physBase + dramSize - pramSize
    public static let fbWidth = 1024, fbHeight = 768, fbDepth = 32
    /// ipad1_kboot.DEFAULT_BOOT_ARGS. enable-hsic=1: 4.x's AppleS5L8930XUSBArbitrator::handleStart publishes the
    /// USB host nubs for the DT's hsic-enabled only when this boot-arg is 1 (no USB keyboard without it); 3.x ignores it.
    public static let defaultBootArgs =
        "serial=3 debug=0x8 amfi_allow_any_signature=1 cs_enforcement_disable=1 enable-hsic=1"
    public static let defaultIBootVersion = "iBoot-817.29"
    public static let rootMatching =
        "<dict><key>IOProviderClass</key><string>IOMedia</string><key>IOPropertyMatch</key>"
        + "<dict><key>Partition ID</key><integer>1</integer></dict></dict>"

    // Measured on a real iPad 1 running 7B500: cpu/memory 0, bus/peripheral 100 MHz, fixed/timebase 24 MHz.
    static let cpuHz: UInt32 = 0, memHz: UInt32 = 0, busHz: UInt32 = 100_000_000, periphHz: UInt32 = 100_000_000
    static let fixedHz: UInt32 = 24_000_000, timebaseHz: UInt32 = 24_000_000, usbphyHz: UInt32 = 24_000_000
    /// /arm-io/audio-complex ncoref-frequency (S5L8920/8922 only): PLL1's 162 MHz, as iBoot fills it.
    static let ncorefHz = PreparedDeviceBoot.ncorefHz
    static let clocks: [UInt32] = {
        var c = [UInt32](repeating: periphHz, count: 55)
        for (i, hz) in [0: timebaseHz, 5: cpuHz, 6: periphHz, 27: memHz, 32: busHz, 33: fixedHz] { c[i] = hz }
        return c
    }()
    /// NAND geometry iBoot would have probed (16 GB, eight Hynix dies); only the keys the DT has are written.
    static let nand: [(String, UInt32)] = [
        ("#ce", 8), ("#die-ce", 1), ("#ce-blocks", 0x1000), ("#block-pages", 128), ("#page-bytes", 4096),
        ("#spare-bytes", 0x80), ("device-readid", 0xB614_D5AD), ("vendor-type", 0x10_0014), ("#databus", 2),
        ("ecc-correctable", 8), ("ecc-threshold", 8), ("bbt-format", 3),
        ("read-cycle-ns", 25), ("read-setup-ns", 10), ("read-hold-ns", 10), ("read-delay-ns", 20),
        ("read-valid-ns", 20), ("write-cycle-ns", 25), ("write-hold-ns", 10),
        ("meta-per-logical-page", 12), ("valid-meta-per-logical-page", 10), ("logical-page-size", 4096),
        ("ppn-device", 0),
        // iBoot-1219 (5.x): the populated CEs numbered across the buses (bus b's at 8b + n); AppleIOPFMI-49's
        // _fmiInitVirtToPhysMap loops forever on an empty one. 4 CEs on each of 2 buses.
        ("ce-bitmap", 0x0F0F),
    ]
    /// The nodes the geometry goes in: iBoot-1219 DTs carry it on flash-controller0 itself as well as on its disk.
    static let nandNodes = ["arm-io/flash-controller0", "arm-io/flash-controller0/disk"]
    static let model = [("model-number", "MB292"), ("region-info", "LL/A")]

    /// One kboot board: what iBoot would put in its DT. (The machine that runs it is the emulator's: DeviceInfo.)
    public struct Board: Equatable, Sendable {
        public var fbWidth: Int, fbHeight: Int
        public var rotation: UInt32, scale: UInt32, boardID: UInt32
        public var modelNumber: String
        /// DRAM bytes: memSize, vram and pram sit at its top, as iBoot puts them.
        public var dram: UInt32 = KBoot.dramSize
        /// A radio board (N90) keeps its baseband node; the machine's `baseband` property unmatches it at boot.
        public var radio = false
        /// The SoC as iBoot names it (root platform-name, chosen/chip-id).
        public var platformName = "s5l8930x", chipID: UInt32 = 0x8930
        /// /product/product-id, which iBoot fills and the IPSW DT reserves zeroed (6.x MobileGestalt's product hash).
        /// 6.x GraphicsServices picks the font cache by it: the 3GS's (compared at GSFontInitialize) selects
        /// CGFontCacheUR.plist, the only set its rootfs ships fonts for; zeroed, the default plist names fonts the 3GS
        /// IPSW lacks and every UIFont and bitmap context comes back nil. nil: leave the slot alone.
        public var productID: [UInt8]? = nil

        public static let k48 = Board(
            fbWidth: 1024,
            fbHeight: 768,
            rotation: 270,
            scale: 1,
            boardID: 0x02,
            modelNumber: "MB292"
        )
        public static let n81 = Board(
            fbWidth: 640,
            fbHeight: 960,
            rotation: 0,
            scale: 2,
            boardID: 0x08,
            modelNumber: "MC540"
        )
        public static let n90 = Board(
            fbWidth: 640,
            fbHeight: 960,
            rotation: 0,
            scale: 2,
            boardID: 0x00,
            modelNumber: "MC603",
            dram: 0x2000_0000,
            radio: true
        )
        /// iPhone 3GS (S5L8920): -M n88, model MB715. Its baseband node is unmatched (no modem model yet).
        public static let n88 = Board(
            fbWidth: 320,
            fbHeight: 480,
            rotation: 0,
            scale: 1,
            boardID: 0x00,
            modelNumber: "MB715",
            platformName: "s5l8920x",
            chipID: 0x8920,
            productID: [
                0x87, 0x84, 0xae, 0x8d, 0x70, 0x66, 0xb0, 0xf0, 0x13, 0x6b,
                0xe9, 0x1d, 0xcf, 0xe6, 0x32, 0xa4, 0x36, 0xff, 0xd6, 0xfb,
            ]
        )
        /// iPod touch 3G (S5L8922): -M n18, model MC008; NOR-less, so it takes the graft.
        public static let n18 = Board(
            fbWidth: 320,
            fbHeight: 480,
            rotation: 0,
            scale: 1,
            boardID: 0x02,
            modelNumber: "MC008",
            platformName: "s5l8922x",
            chipID: 0x8922
        )
        /// The S5L8920 family (-M n18, n88): no metadata whitening in its DTs, the IPSW's NAND epoch, no USB host.
        public var isS5L8920: Bool { platformName != "s5l8930x" }

        /// From the DT's compatible ("N81AP\0iPod4,1\0AppleARM" -> n81); K48 otherwise.
        public static func of(_ dt: DeviceTree) -> Board {
            let first = dt.value("", "compatible").map { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) } ?? ""
            return ["N81AP": .n81, "N90AP": .n90, "N88AP": .n88, "N18AP": .n18][first] ?? .k48
        }

        var memSize: UInt32 { dram - KBoot.pramSize - KBoot.vramSize }
        var vramPA: UInt32 { KBoot.physBase + memSize }
        var pramPA: UInt32 { KBoot.physBase + dram - KBoot.pramSize }
    }

    /// K48's 4.x (8C148) spi0/nor-flash subtree: diagnostics, nvram, the image area and effaceable storage
    /// (ipad1_kboot.NOR_GRAFT). Phandles are K48's.
    static let norGraft: [(String, String, [(String, DeviceTree.Value)])] = [
        (
            "arm-io/spi0", "nor-flash",
            [
                ("compatible", .string("nor-flash,spi")), ("#address-cells", .u32(1)),
                ("device_type", .string("nor-flash")), ("#size-cells", .u32(1)),
                ("ranges", .words([0, 0, 0x10_0000])), ("reg", .words([0, 0x53, 0x0801_0000, 0, 0, 0, 0, 0])),
                ("AAPL,phandle", .u32(0x0091_70E0)),
            ]
        ),
        (
            "arm-io/spi0/nor-flash", "diagnostic-data",
            [
                ("compatible", .string("diagnostic-data,format1")),
                ("device_type", .string("diagnostic-data")),
                ("reg", .words([0x6000, 0x2000, 0x4000, 0x2000])), ("AAPL,phandle", .u32(0x0091_74F0)),
            ]
        ),
        (
            "arm-io/spi0/nor-flash", "nvram",
            [
                ("compatible", .string("nvram,chrp")), ("device_type", .string("nvram")),
                ("reg", .words([0xF_C000, 0x2000, 0xF_E000, 0x2000])), ("AAPL,phandle", .u32(0x0091_7880)),
            ]
        ),
        (
            "arm-io/spi0/nor-flash", "raw-device",
            [
                ("compatible", .string("raw-device,non-nvram")), ("device_type", .string("raw-device")),
                ("reg", .words([0x8000, 0xF_2000, 0, 0x1000])), ("AAPL,phandle", .u32(0x0091_7860)),
            ]
        ),
        (
            "arm-io/spi0/nor-flash", "effaceable",
            [
                ("compatible", .string("effaceable,nor")), ("device_type", .string("effaceable")),
                ("reg", .words([0xF_A000, 0x1000, 0xF_B000, 0x1000])), ("AAPL,phandle", .u32(0x0091_7F70)),
            ]
        ),
    ]

    /// A NOR-less board: graft the NOR in. Either way take the NAND off boot duty (the N88 has its own NOR
    /// and the property too): the kernel keys off boot-from-nand's presence, not its value
    /// (IOFlashStorageDevice then hunts NAND boot blocks and the FTL never finds root); the editor cannot
    /// delete, so rename it.
    static func graftNOR(_ dt: inout DeviceTree) throws {
        guard dt.contains("arm-io/spi0") else { return }
        if !dt.contains("arm-io/spi0/nor-flash") {
            for (parent, name, props) in norGraft { try dt.addNode(parent, name, props) }
        }
        try landingMap(&dt)
        if dt.props["arm-io/flash-controller0/disk"]?["boot-from-nand"] != nil {
            try dt.rename("arm-io/flash-controller0/disk", "boot-from-nand", "boot-from-nor")
        }
    }

    /// 3.x's FMI groups the CEs into buses by the disk node's landing-map words, one CE mask per bus (3.1.3
    /// findNandInfo); the IPSW's DT has a single word, so every CE folds into one bus and its board table has no match
    /// ("2-bus not supported"). One word per populated bus from the disk's CE bitmap (reg), as s5l8920_kboot.py does;
    /// DTs without landing-map (4.x on) are untouched.
    static func landingMap(_ dt: inout DeviceTree) throws {
        let disk = "arm-io/flash-controller0/disk"
        guard dt.props[disk]?["landing-map"] != nil, let reg = dt.value(disk, "reg"), reg.count >= 4 else { return }
        let ces = MachO.u32(reg, 0)
        let words = (0..<4).map { ces & (0xFF << (8 * UInt32($0))) }.filter { $0 != 0 }
        try dt.rename(disk, "landing-map", "landing-map-dt")
        try dt.add(disk, "landing-map", DeviceTree.Value.le(words))
    }

    public struct Segment: Equatable, Sendable {
        public var pa: UInt32, length: UInt32
        /// nil: zero-fill.
        public var data: Data?
        public init(pa: UInt32, length: UInt32, data: Data?) {
            self.pa = pa
            self.length = length
            self.data = data
        }
    }

    public struct Image: Sendable {
        public var image: Data
        public var loadPA: UInt32, entryPA: UInt32, bootArgsPA: UInt32
    }

    /// The first "iBoot-N(.N)*" in a decrypted iBoot, else the default.
    public static func ibootVersion(_ iboot: Data?) -> String {
        guard let b = iboot.map([UInt8].init) else { return defaultIBootVersion }
        let tag = Array("iBoot-".utf8)
        let digit = { (c: UInt8) in c >= 0x30 && c <= 0x39 }
        var i = 0
        while i + tag.count < b.count {
            if b[i] == tag[0], b[i..<i + tag.count].elementsEqual(tag), digit(b[i + tag.count]) {
                var j = i + tag.count
                while j < b.count, digit(b[j]) { j += 1 }
                while j + 1 < b.count, b[j] == 0x2E, digit(b[j + 1]) {
                    j += 1
                    while j < b.count, digit(b[j]) { j += 1 }
                }
                return String(decoding: b[i..<j], as: UTF8.self)
            }
            i += 1
        }
        return defaultIBootVersion
    }

    /// (root props, chosen props, {node: local-mac-address}) for an identity.
    static func identityDT(_ id: UnitIdentity, board: Board = .k48) throws -> (
        [(String, DeviceTree.Value)], [(String, DeviceTree.Value)], [(String, Data)]
    ) {
        func need(_ k: String) throws -> String {
            guard let v = id[k] else { throw FirmwareError(.unsupported, "identity: missing \(k)") }
            return v
        }
        func hex(_ s: String) throws -> UInt64 {
            let t = s.lowercased().hasPrefix("0x") ? String(s.dropFirst(2)) : s
            guard let v = UInt64(t, radix: 16) else { throw FirmwareError(.unsupported, "identity: \(s) is not hex") }
            return v
        }
        func mac(_ s: String) throws -> Data {
            guard let d = Data(hex: s.replacingOccurrences(of: ":", with: "")) else {
                throw FirmwareError(.unsupported, "identity: bad MAC \(s)")
            }
            return d
        }
        let ecid = try hex(need("unique-chip-id"))
        guard let die = id.dieID, die.count == 2 else { throw FirmwareError(.unsupported, "identity: missing die-id") }
        let dieWords = try die.map { w -> UInt32 in
            let v = try hex(w)
            guard v <= UInt32.max else {
                throw FirmwareError(.unsupported, "identity: die-id word \(w) is over 32 bits")
            }
            return UInt32(v)
        }
        guard ecid >> 32 <= UInt32.max else {
            throw FirmwareError(.unsupported, "identity: unique-chip-id over 64 bits")
        }
        let root: [(String, DeviceTree.Value)] =
            [
                ("serial-number", .string(try need("serial-number"))),
                ("mlb-serial-number", .string(try need("mlb-serial-number"))),
            ]
            + model.map { k, v in (k, .string(id[k] ?? (k == "model-number" ? board.modelNumber : v))) }
        let chosen: [(String, DeviceTree.Value)] = [
            ("unique-chip-id", .words([UInt32(ecid & 0xFFFF_FFFF), UInt32(ecid >> 32)])),
            ("die-id", .words(dieWords)),
        ]
        // Bluetooth hangs off whichever UART the board wires it to (K48 uart3, N81 uart1): fillDT finds it.
        return (root, chosen, [("arm-io/sdio", try mac(need("wifi-mac"))), ("bluetooth", try mac(need("bt-mac")))])
    }

    /// An empty NVRAM image as IODTNVRAM parses it (ipad1_kboot.nvram_image): CHRP partitions, a 2 KiB "common"
    /// (0x70), the rest "free" (0x7f). Each 16-byte header is sig, checksum, length in 16-byte units, 12-byte name;
    /// the checksum adds byte 0 and bytes 2-15 with end-around carry. A zeroed image is a zero-length partition,
    /// and initNVRAMImage loops on it forever.
    static func nvramImage(size: Int) -> Data {
        func part(_ sig: UInt8, _ name: String, _ units: Int) -> Data {
            var h =
                [sig, 0, UInt8(units & 0xFF), UInt8(units >> 8)] + Array(name.utf8)
                + [UInt8](repeating: 0, count: 12 - name.utf8.count)
            var c = UInt32(h[0])
            for x in h[2...] {
                c += UInt32(x)
                if c > 0xFF { c = (c & 0xFF) + 1 }
            }
            h[1] = UInt8(c)
            return Data(h) + Data(count: units * 16 - 16)
        }
        let common = 0x80
        return part(0x70, "common", common) + part(0x7F, "free", size / 16 - common)
    }

    static func fillDT(
        _ dt: inout DeviceTree,
        memoryMap: [(String, UInt32, UInt32)],
        identity: UnitIdentity,
        iboot: String,
        rootMatching: String
    ) throws {
        let board = Board.of(dt)
        let (root, chosen, macs) = try identityDT(identity, board: board)
        for (k, v) in [("platform-name", DeviceTree.Value.string(board.platformName))] + root { try dt.set("", k, v) }
        let flags: [(String, DeviceTree.Value)] = [
            "debug-enabled", "production-cert", "secure-boot", "gid-aes-key",
            "uid-aes-key", "system-trusted",
        ].map { ($0, .u32(1)) }
        for (k, v) in flags + [("board-id", .u32(board.boardID)), ("chip-id", .u32(board.chipID))] + chosen
            + [
                ("firmware-version", .string(iboot)), ("display-rotation", .u32(board.rotation)),
                ("display-scale", .u32(board.scale)),
                ("root-matching", .string(rootMatching)),
            ]
        {
            // 3.1.3's DTs (N88 7E18) have none of these: nothing to fill.
            if ["die-id", "display-rotation", "display-scale"].contains(k), dt.props["chosen"]?[k] == nil { continue }
            try dt.set("chosen", k, v)
        }
        // iBoot-1537/1940 (6.x/7.x) hand NVRAM over as /chosen/nvram-proxy-data; the IPSW DT reserves it zeroed.
        if let slot = dt.props["chosen"]?["nvram-proxy-data"] {
            try dt.set("chosen", "nvram-proxy-data", .bytes(nvramImage(size: slot.length)))
        }
        if let id = board.productID, dt.props["product"]?["product-id"]?.length == id.count {
            try dt.set("product", "product-id", .bytes(Data(id)))
        }
        // iBoot-1940 also copies syscfg's MACs to /chosen; 7.x's MobileGestalt reads them there (and hashes them into the UDID).
        for (k, node) in [("mac-address-wifi0", "arm-io/sdio"), ("mac-address-bluetooth0", "bluetooth")]
        where dt.props["chosen"]?[k] != nil {
            if let mac = macs.first(where: { $0.0 == node })?.1 { try dt.set("chosen", k, .bytes(mac)) }
        }
        for (k, hz) in [
            ("clock-frequency", cpuHz), ("memory-frequency", memHz), ("bus-frequency", busHz),
            ("peripheral-frequency", periphHz), ("fixed-frequency", fixedHz), ("timebase-frequency", timebaseHz),
        ] {
            try dt.set("cpus/cpu0", k, .u32(hz))
        }
        let slots = (dt.props["arm-io"]?["clock-frequencies"]?.length ?? clocks.count * 4) / 4
        try dt.set("arm-io", "clock-frequencies", .words(Array(clocks.prefix(slots))))
        try dt.set("arm-io", "usbphy-frequency", .u32(usbphyHz))
        if dt.props["arm-io/audio-complex"]?["ncoref-frequency"] != nil {
            try dt.set("arm-io/audio-complex", "ncoref-frequency", .u32(ncorefHz))
        }
        if dt.contains("arm-io/sgx") { try dt.set("arm-io/sgx", "compatible", .string("none")) }  // no SGX model
        for (want, mac) in macs {
            let path = dt.props.keys.sorted().first { $0 == want || $0.hasSuffix("/" + want) } ?? want
            if dt.contains(path) { try dt.set(path, "local-mac-address", .bytes(mac)) }
        }
        if dt.contains("arm-io/mipi-dsim/lcd") {  // the panel id iBoot's pinot_init writes; the DSI model's reply
            // 3.0's DTs (N88 7A341) have no raw-panel-id slot
            for k in ["lcd-panel-id", "raw-panel-id"] where dt.props["arm-io/mipi-dsim/lcd"]?[k] != nil {
                try dt.set("arm-io/mipi-dsim/lcd", k, .u32(0x00A1_D13C))
            }
        }
        if dt.contains("baseband"), !board.radio {  // Wi-Fi iPad: no radio, so unmatch and unname the N82 baseband node
            for (k, v) in [("compatible", "none"), ("device_type", "none"), ("name", "nobb")] {
                try dt.set("baseband", k, .string(v))
            }
            // lockdownd still reads the node's identity (N88): GSMA's test IMEI and a placeholder serial, so nothing
            // passes for a real unit (s5l8920_kboot.py).
            if dt.props["baseband"]?["device-imei"] != nil {
                try dt.set("baseband", "device-imei", .string("004999010640000"))
                if dt.props["baseband"]?["snum"] != nil {  // 3.0's DT has none
                    try dt.set("baseband", "snum", .bytes(Data("TESTSNUM0000".utf8)))
                }
            }
        }
        if dt.props["arm-io"]?["chip-revision"] != nil { try dt.set("arm-io", "chip-revision", .u32(0x11)) }
        for node in nandNodes {
            guard let props = dt.props[node] else { continue }
            for (k, v) in nand where props[k] != nil { try dt.set(node, k, .u32(v)) }
        }
        try dt.set("pram", "reg", .words([board.pramPA, pramSize]))
        try dt.set("vram", "reg", .words([board.vramPA, vramSize]))
        for (i, (name, pa, size)) in memoryMap.enumerated() {
            try dt.rename("chosen/memory-map", "MemoryMapReserved-\(i)", name)
            try dt.set("chosen/memory-map", name, .words([pa, size]))
        }
    }

    /// The flat physical image, its load PA, entry PA and boot_args PA. `ramdisk`: raw HFS to boot as md0.
    public static func build(
        kernel: Data,
        deviceTree: Data,
        bootArgs: String = defaultBootArgs,
        identity: UnitIdentity,
        iboot: String = defaultIBootVersion,
        ramdisk: Data? = nil
    ) throws -> Image {
        let page = { (n: Int) in (n + 0xFFF) & ~0xFFF }
        let m = try MachO(kernel)
        let segs = m.segments.filter { $0.name != "__PAGEZERO" }
        guard let lowest = segs.map(\.vmaddr).min(), let end = segs.map({ Int($0.vmaddr) + Int($0.vmsize) }).max()
        else {
            throw FirmwareError(.unsupported, "kernelcache has no segments")
        }
        let vbase = Int(lowest & 0xF000_0000)
        let pa = { (va: Int) in UInt32(truncatingIfNeeded: va - vbase + Int(physBase)) }
        var dt = try DeviceTree(deviceTree)
        let board = Board.of(dt)
        try graftNOR(&dt)
        // Host nubs (EHCI, OHCI0) up at arbitrator start, next to device mode (qemu-ios docs/ipad1/usb-keyboard.md).
        if dt.contains("arm-io/usb-complex") { try dt.add("arm-io/usb-complex", "hsic-enabled") }
        let dtLen = dt.data.count
        var top = page(end)
        let rdVA = top
        var args = bootArgs
        if let rd = ramdisk {
            top += page(rd.count)
            args += " rd=md0"
        }
        let dtVA = top
        let argsVA = top + page(dtLen)
        let endVA = argsVA + 0x1000
        let topOfKernel = pa((endVA + 0x3FFF) & ~0x3FFF)

        var image = Data(count: endVA - vbase)
        var memoryMap: [(String, UInt32, UInt32)] = []
        for s in segs {
            let n = Int(min(s.filesize, s.vmsize))
            let at = Int(s.vmaddr) - vbase
            guard Int(s.fileoff) + n <= kernel.count else {
                throw FirmwareError(.unsupported, "kernelcache segment \(s.name) runs past the file")
            }
            let from = kernel.startIndex + Int(s.fileoff)
            image.replaceSubrange(at..<at + n, with: kernel[from..<from + n])
            memoryMap.append(("Kernel-\(s.name)", pa(Int(s.vmaddr)), s.vmsize))
        }
        if let rd = ramdisk {
            image.replaceSubrange(rdVA - vbase..<rdVA - vbase + rd.count, with: rd)
            memoryMap.append(("RAMDisk", pa(rdVA), UInt32(rd.count)))
        }
        memoryMap += [("DeviceTree", pa(dtVA), UInt32(dtLen)), ("BootArgs", pa(argsVA), 0x1000)]
        try fillDT(
            &dt,
            memoryMap: memoryMap,
            identity: identity,
            iboot: iboot,
            rootMatching: ramdisk == nil ? rootMatching : ""
        )
        image.replaceSubrange(dtVA - vbase..<dtVA - vbase + dt.data.count, with: dt.data)

        // boot_args rev 1 / the version the kernel checks for (2, or 3 from xnu-1735.47). Video: base, display
        // (0 = text console for -v/-s), rowbytes, w, h, depth.
        let verbose = args.split(separator: " ").contains { $0 == "-v" || $0 == "-s" }
        let cmdline = Array(args.utf8)
        guard cmdline.count < 256 else { throw FirmwareError(.unsupported, "boot-args longer than BOOT_LINE_LENGTH") }
        var ba = Data([1, 0, m.bootArgsVersion(), 0])
        ba += DeviceTree.Value.le([
            UInt32(vbase), physBase, board.memSize, topOfKernel,
            board.vramPA, verbose ? 0 : 1, UInt32(board.fbWidth * fbDepth / 8), UInt32(board.fbWidth),
            UInt32(board.fbHeight), UInt32(fbDepth) | (board.scale - 1) << 16,
            0, UInt32(dtVA), UInt32(dtLen),
        ])
        ba += cmdline + [UInt8](repeating: 0, count: 256 - cmdline.count)
        image.replaceSubrange(argsVA - vbase..<argsVA - vbase + ba.count, with: ba)
        return Image(image: image, loadPA: physBase, entryPA: pa(Int(try m.entry())), bootArgsPA: pa(argsVA))
    }

    /// image + segments ("K48SEG\0\0", pa, len, flags bit 0 = zero-fill, data) + the 24-byte trailer.
    public static func bundle(_ img: Image, segments: [Segment]) -> Data {
        var out = img.image
        for s in segments {
            out += Data("K48SEG\0\0".utf8) + DeviceTree.Value.le([s.pa, s.length, s.data == nil ? 1 : 0])
            if let d = s.data { out += d }
        }
        out +=
            Data("K48KBOOT".utf8)
            + DeviceTree.Value.le([img.loadPA, img.entryPA, img.bootArgsPA, UInt32(img.image.count)])
        return out
    }

    /// ipad1_kboot.main: kboot.bin from a decrypted-firmware directory.
    public static func write(
        decrypted dir: URL,
        to out: URL,
        identity: UnitIdentity,
        bootArgs: String = defaultBootArgs,
        ramdisk: URL? = nil,
        kernelOverride: Data? = nil
    ) throws {
        let file = { (n: String) in dir.appendingPathComponent(n) }
        let exists = { (n: String) in FileManager.default.fileExists(atPath: file(n).path) }
        let img = try build(
            kernel: kernelOverride ?? Data(contentsOf: file("kernelcache.mach")),
            deviceTree: Data(contentsOf: file("DeviceTree.bin")),
            bootArgs: bootArgs,
            identity: identity,
            iboot: ibootVersion(exists("iBoot.bin") ? try Data(contentsOf: file("iBoot.bin")) : nil),
            ramdisk: try ramdisk.map { try Data(contentsOf: $0) }
        )
        let board = Board.of(try DeviceTree(Data(contentsOf: file("DeviceTree.bin"))))
        let logo =
            exists("AppleLogo.bin")
            ? try BootLogo.segments(
                iBootIm: Data(contentsOf: file("AppleLogo.bin")),
                framebufferPA: board.vramPA,
                width: board.fbWidth,
                height: board.fbHeight,
                turn: board.rotation == 270
            ) : []
        try bundle(img, segments: logo).write(to: out)
    }
}

/// A 32-bit Mach-O's segments and entry point (imgtools/macho.py, as much as kboot needs).
public struct MachO: Sendable {
    public struct Segment: Equatable, Sendable {
        public var name: String
        public var vmaddr: UInt32, vmsize: UInt32, fileoff: UInt32, filesize: UInt32
    }

    public let segments: [Segment]
    let data: Data

    public init(_ data: Data) throws {
        self.data = data
        guard data.count >= 28, Self.u32(data, 0) == 0xFEED_FACE else {
            throw FirmwareError(.unsupported, "not a 32-bit Mach-O")
        }
        var segs: [Segment] = []
        var off = 28
        for _ in 0..<Self.u32(data, 16) {
            guard off + 8 <= data.count else {
                throw FirmwareError(.unsupported, "Mach-O load commands run past the file")
            }
            let cmd = Self.u32(data, off)
            let size = Int(Self.u32(data, off + 4))
            if cmd == 1 {  // LC_SEGMENT
                let nameBytes = data[data.startIndex + off + 8..<data.startIndex + off + 24].prefix { $0 != 0 }
                segs.append(
                    Segment(
                        name: String(decoding: nameBytes, as: UTF8.self),
                        vmaddr: Self.u32(data, off + 24),
                        vmsize: Self.u32(data, off + 28),
                        fileoff: Self.u32(data, off + 32),
                        filesize: Self.u32(data, off + 36)
                    )
                )
            }
            guard size > 0 else { break }
            off += size
        }
        segments = segs
    }

    /// The boot_args.Version pe_identify_machine demands, read off the kernel's own check (imgtools/ipad1_kboot.py
    /// boot_args_version): the Thumb pair `ldrh rN, [r0, #2]` (0x8840|N) … `cmp rN, #V` (0x28|N<<8|V) just before the
    /// literal naming "pe_identify_machine: Epoch Mismatch". 2 when the shape is not found (3.2.x, 4.2.1 and 4.3.0
    /// boot with 2); 4.3.5's xnu-1735.47 and iOS 5's xnu-1878 say 3.
    public func bootArgsVersion() -> UInt8 {
        guard let so = data.range(of: Data("pe_identify_machine: Epoch Mismatch".utf8))?.lowerBound,
            let seg = segments.first(where: {
                Int($0.fileoff) <= so - data.startIndex && so - data.startIndex < Int($0.fileoff + $0.filesize)
            })
        else { return 2 }
        let sva = seg.vmaddr + UInt32(so - data.startIndex) - seg.fileoff
        if let lit = data.range(of: Data(DeviceTree.Value.le([sva])))?.lowerBound {
            let window = [UInt8](data[max(data.startIndex, lit - 0x400)..<lit])
            for n in 0..<8 {
                guard window.count > 2,
                    let i = (0..<(window.count - 1)).reversed().first(where: {
                        window[$0] == 0x40 | UInt8(n) && window[$0 + 1] == 0x88
                    })
                else { continue }
                for j in (i + 2)..<min(i + 10, window.count) where window[j] == 0x28 | UInt8(n) { return window[j - 1] }
            }
        }
        // iOS 6's xnu-2107 reaches the string through movw/movt/add rX, pc, so no literal names it: the pair is
        // `ldrh rN, [r0, #2]; cmp rN, #V` with that sequence within the next 24 bytes (ipad1_kboot 08a698c2f1).
        let bytes = [UInt8](data)
        let h16 = { (o: Int) in UInt32(bytes[o]) | UInt32(bytes[o + 1]) << 8 }
        let imm16 = { (hw1: UInt32, hw2: UInt32) in
            (hw1 & 0xF) << 12 | ((hw1 >> 10) & 1) << 11 | ((hw2 >> 12) & 7) << 8 | (hw2 & 0xFF)
        }
        func namesString(_ at: Int, _ seg: Segment) -> Bool {
            for o in stride(from: at, to: min(at + 24, bytes.count - 10), by: 2) {
                let hw1 = h16(o)
                let hw2 = h16(o + 2)
                guard hw1 & 0xFBF0 == 0xF240 else { continue }  // movw
                let rd = (hw2 >> 8) & 0xF
                let lo = imm16(hw1, hw2)
                let t1 = h16(o + 4)
                let t2 = h16(o + 6)
                guard t1 & 0xFBF0 == 0xF2C0, (t2 >> 8) & 0xF == rd else { continue }  // movt, same register
                guard h16(o + 8) == 0x4478 | (rd & 7) | ((rd & 8) << 4) else { continue }  // add rd, pc
                let pc = seg.vmaddr &+ UInt32(o + 8 - Int(seg.fileoff)) &+ 4
                return (imm16(t1, t2) << 16 | lo) &+ pc == sva
            }
            return false
        }
        for seg in segments where seg.filesize > 0 {
            let lo = Int(seg.fileoff)
            let hi = min(Int(seg.fileoff + seg.filesize), bytes.count - 4)
            guard lo < hi else { continue }
            for i in stride(from: lo, to: hi, by: 2) where bytes[i + 1] == 0x88 && bytes[i] & 0xF8 == 0x40 {
                let n = bytes[i] & 7
                if bytes[i + 3] == 0x28 | n, namesString(i + 6, seg) { return bytes[i + 2] }
            }
        }
        return 2
    }

    /// LC_UNIXTHREAD's ARM_THREAD_STATE pc (r15).
    public func entry() throws -> UInt32 {
        var off = 28
        for _ in 0..<Self.u32(data, 16) {
            let cmd = Self.u32(data, off)
            let size = Int(Self.u32(data, off + 4))
            if cmd == 5 { return Self.u32(data, off + 16 + 15 * 4) }
            guard size > 0 else { break }
            off += size
        }
        throw FirmwareError(.unsupported, "no LC_UNIXTHREAD")
    }

    static func u32(_ d: Data, _ at: Int) -> UInt32 { d.withUnsafeBytes { $0.u32le(at) } }
}
