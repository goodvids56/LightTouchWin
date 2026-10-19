// N45FTL: the 1.x legacy FTL read back from a page store as the guest left it: an image store (PageNANDImage.s5l8900,
// the device's live clone in the overlay when there is one), or a format 1 bank<N>/<page>.page base plus the
// emulator's overlay of the same layout, blk<N>.erased markers honored. An erased page (every bit set) reads as none.
// The FTL's context is openiBoot's s5l8900 FTLCxt (the structure N45NAND.ftlMeta writes): the map's four 0x46 pages
// listed at +0x38, the log-offset pages at +0x110, the log table at +0x1A4 (20-byte entries: usn, vbn, lbn), the
// context blocks at +0x312. The context moves: the FTL takes new context blocks from its free pool (a booted M68 had it in
// virtual block 3), so the current one is found as the context-typed (0x43-0x4F) virtual block whose first page
// has the lowest usnDec, and its last written page must be the context itself (0x43); anything else is an
// unclean shutdown, which this reader refuses (that state needs FTL_Restore's scan).
//
//   let ftl = try N45FTL(base: nand, overlay: ovl)
//   ftl.read(lpn:)      // the logical page's 2048 bytes and spare, nil when it was never written
//   ftl.location(lpn:)  // where it lives now (a log block's page, or map[lbn] at the same offset)

import Foundation

public struct N45FTL {
    static let logs = 17  // FTLCxt.pLog slots the FTL uses (the 18th is not)
    static let virtualBlocks = N45NAND.blocksPerBank - N45NAND.ftlStart

    public let banks: Int
    let page: @Sendable (N45NAND.Page) -> (data: [UInt8], spare: [UInt8])?
    let map: [UInt16]
    let logTable: [UInt16: (vbn: UInt16, slot: Int)]
    let offsets: [UInt16]

    public init(base: URL, overlay: URL?) throws {
        let image = try PageNANDImage.s5l8900(at: base)
        let banks = image.chips
        self.banks = banks
        let page = try Self.reader(image, base: base, overlay: overlay)
        self.page = page
        let sb = N45NAND.superblock(banks)
        let read = { (vpn: Int) in page(N45NAND.location(vpn: vpn, banks: banks)) }

        var newest: (usn: UInt32, vb: Int)?
        for vb in 0..<Self.virtualBlocks {
            guard let (_, spare) = read(vb * sb), (0x43...0x4F).contains(spare[9]) else { continue }
            let usn = le32(spare, 0)
            if newest.map({ usn < $0.usn }) ?? true { newest = (usn, vb) }
        }
        guard let vb = newest?.vb else { throw FirmwareError(.unsupported, "\(base.path): no FTL context") }
        guard let last = (1..<sb).reversed().lazy.compactMap({ read(vb * sb + $0) }).first, last.spare[9] == 0x43 else {
            throw FirmwareError(
                .unsupported,
                "the 1.x FTL was not shut down cleanly (virtual block \(vb) does not end in its context); power the device off from the guest first"
            )
        }
        let cxt = last.data
        guard (0..<3).contains(where: { Int(le16(cxt, 0x312 + 2 * $0)) == vb }) else {
            throw FirmwareError(
                .unsupported,
                "FTL context in virtual block \(vb) does not list itself as a context block"
            )
        }
        var map: [UInt16] = []
        for i in 0..<N45NAND.mapTables {
            guard let page = read(Int(le32(cxt, 0x38 + 4 * i)))?.data else {
                throw FirmwareError(.unsupported, "FTL map page \(i) is missing")
            }
            map += stride(from: 0, to: N45NAND.page, by: 2).map { le16(page, $0) }
        }
        var table: [UInt16: (UInt16, Int)] = [:]
        for slot in 0..<Self.logs {
            let vbn = le16(cxt, 0x1A4 + 20 * slot + 4)
            let lbn = le16(cxt, 0x1A4 + 20 * slot + 6)
            if vbn != 0xFFFF { table[lbn] = (vbn, slot) }
        }
        var offsets: [UInt16] = []
        if !table.isEmpty {
            let pages = (Self.logs * sb * 2 + N45NAND.page - 1) / N45NAND.page
            var raw: [UInt8] = []
            for i in 0..<pages {
                guard let page = read(Int(le32(cxt, 0x110 + 4 * i)))?.data else {
                    throw FirmwareError(.unsupported, "FTL log-offset page \(i) is missing")
                }
                raw += page
            }
            offsets = stride(from: 0, to: Self.logs * sb * 2, by: 2).map { le16(raw, $0) }
        }
        self.map = map
        self.logTable = table
        self.offsets = offsets
    }

    public var logBlocksInUse: Int { logTable.count }

    /// Where logical page `lpn` lives now.
    public func location(lpn: Int) -> N45NAND.Page {
        let sb = N45NAND.superblock(banks)
        let (lbn, off) = lpn.quotientAndRemainder(dividingBy: sb)
        if let log = logTable[UInt16(lbn)] {
            let o = offsets[log.slot * sb + off]
            if o != 0xFFFF { return N45NAND.location(vpn: Int(log.vbn) * sb + Int(o), banks: banks) }
        }
        return N45NAND.location(vpn: Int(map[lbn]) * sb + off, banks: banks)
    }

    public func read(lpn: Int) -> (data: [UInt8], spare: [UInt8])? { page(location(lpn: lpn)) }

    /// Pages as the guest reads them (PageNANDImage.reader); nil for an erased or unreadable page.
    static func reader(_ image: PageNANDImage, base: URL, overlay: URL?) throws
        -> @Sendable (N45NAND.Page) -> (data: [UInt8], spare: [UInt8])?
    {
        let read = try image.reader(base: base, overlay: overlay)
        let blank = image.blankRecord
        return { p in
            guard let record = try? read(.init(chip: p.bank, page: p.page)), record != blank else { return nil }
            return (Array(record[0..<N45NAND.page]), Array(record[N45NAND.page...]))
        }
    }
}
