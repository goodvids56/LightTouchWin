import Foundation
import LightTouchCore
import SessionKit

/// `sessions single BASE`: one prepared base booted as the app boots it (session-driver's single.swift): lit, lockdown
/// over its own usbmuxd, the first-host activation handshake and the Mac's time zone, the Home screen (the guest agent's
/// frontmost app and screen where the base has an agent, the frame against tests/sessions/matrix-refs where there is a
/// reference), the backlight at the firmware's 100%, AFC round trips past 16 KiB, an IPA install (2.x on), a clean
/// shutdown confirmed by the guest, and the base untouched.
func single(_ args: SingleCheck) -> Never {
    let base = Base(args.base)
    let work = workDirectory(args.inputs, "single")
    let tools = Tools.resolve(args.inputs, work: work)
    let d = base.driverBoard
    let install = !args.noInstall && base.major >= 2  // 1.x has no installation_proxy
    let ipa = args.ipa ?? checkout("qemu-ios").appendingPathComponent("contrib/it-harness/build/Harness.ipa")
    if install, !FileManager.default.fileExists(atPath: ipa.path) { die("no test IPA at \(ipa.path) (--ipa)") }
    let before = SessionJudge.tree(base.url)

    var single: [String: Any] = [
        "board": d, "base": base.url.path, "lockdownTZ": tools.services.path,
        "launch": args.launch, "reboot": args.reboot, "install": install, "httpget": httpget(args.inputs).path,
    ]
    let homeReference = repository.appendingPathComponent("tests/sessions/matrix-refs/\(base.entryID)-home.png")
    if FileManager.default.fileExists(atPath: homeReference.path) { single["homeReference"] = homeReference.path }
    if args.hostPowerGesture { single["hostPowerGesture"] = true }
    if let zone = args.secondZone { single["secondZone"] = zone }
    if let region = args.region { single["region"] = region }
    if let file = args.readFile { single["readFile"] = file }
    if args.developerImage {
        guard let image = DeveloperDiskImage.image(version: base.version, in: tools.guest) else {
            die("the guest archive has no Developer Disk Image for \(base.version)")
        }
        single["developerImage"] = image.path
    }
    if args.accessibility { single["accessibility"] = true }
    if !args.media.isEmpty {
        single["media"] = args.media.map(\.path)
        single["mediaTools"] =
            (args.mediaTools ?? checkout("qemu-ios").appendingPathComponent("contrib/it-media")).path
    }
    if args.skipSetup { single["skipSetup"] = true }
    if args.jailbreak { single["jailbreak"] = true }
    if let panel = args.panel { single["panel"] = panel }
    if args.killPersist { single["killPersist"] = true }
    if let upgrade = args.upgradeIPA { single["upgradeIPA"] = upgrade.path }
    if let wav = args.audioWAV { single["audioWAV"] = wav.path }
    let race = args.afcRace
    if let race {
        single["raceBoots"] = race
        single["raceDirty"] = args.dirty
    }
    var config = driverConfig(tools, work: work, ipa: ipa)
    if !args.noOffer {  // the app offers its bundled guest package at every boot
        let packs = tools.guest.appendingPathComponent("guest-tools")
        if base.armv7 {
            config["ipadItpack"] = (args.itpack ?? packs.appendingPathComponent("armv7.itpack")).path
        } else {
            single["itpack"] = (args.itpack ?? packs.appendingPathComponent("armv6.itpack")).path
        }
    }
    if args.jailbreak {
        single["proxyItpack"] = tools.guest.appendingPathComponent("guest-tools/armv6.itpack").path
    }
    if base.board == "k48ap" { config["ipadBase"] = base.url.path }
    config["single"] = single
    // 6.x/7.x: the first boot walks the Setup Assistant; 7.x also boots and pairs far slower. Per boot.
    var timeout = base.major >= 7 ? 1400.0 : base.major >= 6 ? 700 : 560
    if args.reboot || args.panel != nil || args.killPersist { timeout *= 2 }
    if let race { timeout = 200 * Double(race) }
    // The app survey (session-driver apps.swift): 150 s an app on top of the boot.
    if let list = ProcessInfo.processInfo.environment["LTM_APPS_LIST"],
        let text = try? String(contentsOfFile: list, encoding: .utf8)
    {
        timeout += 150 * Double(text.split(separator: "\n").count) + 60
    }
    // --jailbreak: Cydia online (its refresh and an install) and the proxy's trace, which the check reads.
    if args.jailbreak { timeout += 600 }
    config["timeout"] = timeout

    print("\(base.entryID): \(d) from \(base.url.path)")
    let (events, status) = sessionDriver(
        config,
        work: work,
        timeout: timeout,
        environment: driverEnvironment(tools).merging(args.jailbreak ? ["LTM_WEB_PROXY_TRACE": "1"] : [:]) { $1 }
    )
    let r = Report()
    if let race {
        for e in events.find("race", ["device": d]) {
            r.check(
                !e.has("error"),
                "\(d) boot \(e.int("generation") ?? 0): AFC \(format(e.double("seconds"))) s after lockdown's first answer "
                    + "(\(format(e.double("lockdown"))) s after power-on): "
                    + (e.string("error") ?? "\(e.int("entries") ?? 0) entries")
            )
        }
        for e in events.find("raceStop", ["device": d]) {
            r.check(
                !e.has("uploadError"),
                "\(d) boot \(e.int("generation") ?? 0): installed, uploaded, Stop \(format(e.double("afterHalt"), 0)) s into the halt"
                    + (e.string("uploadError").map { ": \($0)" } ?? "")
            )
        }
        r.check(
            events.find("race", ["device": d]).count == race && events.any("done"),
            "\(d): \(events.find("race", ["device": d]).count)/\(race) boots ran"
        )
        finish(r, work: work)
    }

    if args.killPersist {
        let lit = events.find("lit", ["device": d])
        r.check(lit.count == 2, "\(d): lit on both boots (\(lit.count))")
        let kept = events.one("killPersist", ["device": d])
        r.check(
            kept.bool("kept") && kept.bool("same"),
            "\(d): the file uploaded before the SIGKILL is there after it"
                + (kept.string("error").map { ": \(clip($0, 120))" } ?? "")
        )
        r.check(SessionJudge.tree(base.url) == before, "\(d): the prepared base is unchanged")
        r.check(events.any("done") && status == 0, "driver finished (exit \(status.map(String.init) ?? "timeout"))")
        finish(r, work: work)
    }

    if let panel = args.panel {
        resized(r, d, panel: panel, events: events)
        let after = SessionJudge.tree(base.url)
        r.check(after == before, "\(d): the prepared base is unchanged")
        r.check(events.any("done") && status == 0, "driver finished (exit \(status.map(String.init) ?? "timeout"))")
        finish(r, work: work)
    }

    let lit = events.one("lit", ["device": d])
    r.check(!lit.isEmpty, "\(d): lit in \(format(lit.double("seconds"))) s")
    let usb = events.one("usb", ["device": d])
    r.check(
        usb.string("productType") == base.productType,
        "\(d): lockdown over its usbmuxd: \(usb.string("productType") ?? "none")"
    )
    let home = SessionJudge.home(
        lock: base.lock,
        events: events,
        entryID: base.entryID,
        references: repository.appendingPathComponent("tests/sessions/matrix-refs")
    )
    r.check(home.ok == true, "\(d): usable Home screen: \(home.detail)")
    let top = SessionJudge.backlightTop(board: base.board, base: base.url, productVersion: base.version)
    for h in events.find("home", ["device": d]) {
        let level = h.int("backlight") ?? -1
        if let top, level != -1 {
            r.check(level >= top, "\(d): backlight at Home \(hex(level)), the firmware's 100% is \(hex(top))")
        } else {
            r.note("\(d): backlight level not decoded here (\(level))")
        }
    }
    let wifi = events.one("wifi", ["device": d])
    if wifi.isEmpty || wifi.bool("agent") {
        r.check(
            wifi.bool("ok"),
            "\(d): Wi-Fi up: the guest fetched wifi0's page: \(clip(wifi.string("output") ?? "not asked", 80))"
        )
    } else {
        r.note("\(d): Wi-Fi not checked (no guest agent)")
    }
    let activation = events.find("activationCompleted", ["device": d])
    r.check(
        !activation.isEmpty && activation.allSatisfy { $0.bool("ok") },
        "\(d): automatic activation handshake completed"
            + (activation.allSatisfy { $0.bool("ok") } ? "" : ": \(activation.map { $0.string("error") ?? "" })")
    )
    let ids = events.find("identity", ["device": d])
    if d == "ipod" || d == "ipad" {
        r.check(
            !ids.isEmpty && ids.allSatisfy { $0.bool("matches") },
            "\(d): lockdown's identity matches the prepared identity: "
                + ids.map { e in
                    ((e["values"] as? [String: String]) ?? [:]).sorted { $0.key < $1.key }.map {
                        "\($0.key) \($0.value)"
                    }
                    .joined(separator: ", ") + (e.bool("matches") ? "" : " (want \(e["expected"] ?? ""))")
                }.joined(separator: "; ")
        )
    }
    let afc = events.find("afc", ["device": d])
    for a in afc {
        let bytes = a.int("bytes") ?? 0
        r.check(
            a.bool("same") && a.int("listed") == bytes,
            "\(d): AFC round trip of \(bytes) bytes"
                + (a.bool("same")
                    ? " (\(format(a.double("seconds"))) s)" : ": \(a.string("error") ?? "content differs")")
        )
    }
    r.check(afc.count >= 4, "\(d): AFC checks ran (\(afc.count))")
    if install {
        let inst = events.one("installed", ["device": d])
        r.check(
            inst.bool("has"),
            "\(d): IPA installed (\(format(inst.double("seconds"), 0)) s, attempt \(inst.int("attempt") ?? 0))"
        )
        // Files' Apps source (house_arrest): the app's container listed, a file copied into Documents and back,
        // renamed, a folder with a file in it, both deleted; read by the guest agent where it answers, and seen through
        // afc2 at the container's path on a jailbroken base.
        let files = events.one("appFiles", ["device": d])
        let steps = ["listed", "same", "renamed", "folder", "deleted"]
        let inside = files.bool("agent") ? ["agentRead", "agentRenamed", "agentDeleted"] : []
        let failed = (steps + inside + (args.jailbreak ? ["afc2"] : [])).filter { !files.bool($0) }
        // From 5.0 installd makes Documents in every container it populates; a container without one is an
        // install that failed part-way (issue 23: no protection classes on the data volume).
        let documentsMissing = base.major >= 5 && files.bool("madeDocuments")
        r.check(
            !files.has("error") && failed.isEmpty && !documentsMissing,
            "\(d): Files edits the app's container (\((files["top"] as? [String] ?? []).joined(separator: ", ")))"
                + (files.bool("agent") ? ", the app reads the changes" : ", no guest agent")
                + (files.bool("madeDocuments") ? ", Documents made" : "")
                + (failed.isEmpty ? "" : "; failed: \(failed.joined(separator: ", "))")
                + (files.string("error").map { ": \($0)" } ?? "")
        )
    }
    if args.upgradeIPA != nil {
        let up = events.one("upgraded", ["device": d])
        r.check(
            (up.string("error") ?? "x").isEmpty && !(up.string("after") ?? "").isEmpty && up.bool("kept"),
            "\(d): upgrade over the installed app keeps its data (now version \(up.string("version") ?? "")): \(up)"
        )
    }
    if args.launch {
        let launches = events.find("launched", ["device": d])
        r.check(
            !launches.isEmpty
                && launches.allSatisfy {
                    $0.string("via") == "agent" && !$0.has("launchError")
                        && $0.string("frontmost3") == $0.string("bundleID")
                },
            "\(d): the installed app is frontmost after the agent's launch: \(launches.map { $0.string("frontmost3") ?? $0.string("launchError") ?? "?" })"
        )
    }
    let boots = args.reboot ? 2 : 1
    let quits = events.find("quit", ["device": d])
    r.check(
        quits.count == boots
            && quits.allSatisfy {
                ($0.double("confirmed") ?? -1) >= 0 && $0.bool("exited")
                    && ($0.string("reason") ?? "").hasSuffix(" stopped.")
            },
        "\(d): \(quits.count)/\(boots) clean shutdowns, guest power-off confirmed "
            + quits.map { "in \(format($0.double("confirmed"))) s" }.joined(separator: ", ") + ", helper exited"
    )
    if args.hostPowerGesture {
        let gestures = events.find("hostPowerGesture", ["device": d])
        r.check(
            gestures.count == boots && gestures.allSatisfy { $0.bool("confirmed") && !$0.has("error") },
            "\(d): \(gestures.count)/\(boots) host power gestures confirmed by the guest's PMU"
        )
    }
    let zones = events.find("timezone", ["device": d])
    if !zones.isEmpty, !base.firstGeneration {  // 1.x: NITZ (the M68's modem) or nothing
        r.check(
            zones.allSatisfy { $0.string("zone") == $0.string("want") },
            "\(d): lockdown holds the zone asked for at each boot: \(zones.map { "\($0.string("want") ?? "") -> \($0.string("zone") ?? "")" })"
        )
    }
    if let zone = args.secondZone {
        r.check(
            zones.contains { $0.int("generation") == 2 && $0.string("zone") == zone },
            "\(d): the zone follows the Mac's change between boots"
        )
    }
    if args.reboot {
        let persisted = events.find("persist", ["device": d])
        r.check(
            !persisted.isEmpty && persisted.allSatisfy { $0.bool("kept") && $0.bool("same") },
            "\(d): an AFC file survives the cold reboot byte for byte\(persisted.first?.string("error").map { ": \($0)" } ?? "")"
        )
        if install {
            let restarted = events.find("restartedApps", ["device": d])
            r.check(
                !restarted.isEmpty && restarted.allSatisfy { $0.bool("has") },
                "\(d): the installed app survives the cold reboot"
            )
        }
        r.check(
            events.find("home", ["device": d]).count == boots && activation.count == boots
                && (d != "ipod" || ids.count == boots),
            "\(d): both boots reached Home, activation and identity"
        )
    }
    if args.jailbreak {
        let afc2 = events.one("afc2", ["device": d])
        let top = Set(afc2["top"] as? [String] ?? [])
        r.check(
            top.isSuperset(of: ["Applications", "System", "private"])
                && afc2.string("version") == base.version && afc2.bool("roundTrip"),
            "\(d): afc2 lists / (\(top.sorted().joined(separator: ", "))) and reads iOS \(afc2.string("version") ?? "none")"
                + (afc2.bool("roundTrip") ? ", a file round trip in /private/var/root" : "")
                + (afc2.string("error").map { ": \($0)" } ?? "")
        )
        let cydia = events.one("cydia", ["device": d])
        r.check(
            cydia.bool("onHome"),
            "\(d): Cydia on the Home screen" + (cydia.string("homeError").map { ": \($0)" } ?? "")
        )
        let fronts = (1...3).map { cydia.string("frontmost\($0)") ?? "" }
        r.check(
            fronts.allSatisfy { $0 == "com.saurik.Cydia" },
            "\(d): Cydia launched and stayed frontmost (\(fronts.joined(separator: ", ")))"
                + (cydia.string("launchError").map { ": \($0)" } ?? "")
        )
        // Online through the web proxy (on, its CA trusted): the front page and what it loads came through it
        // (over http on 3.x, where Cydia HTTPatch points Cydia there).
        let native = (try? String(contentsOf: work.appendingPathComponent("\(d)/native.log"), encoding: .utf8)) ?? ""
        let front = native.split(separator: "\n").filter {
            $0.contains("web-proxy: GET http") && $0.contains("://cydia.saurik.com")
        }
        r.check(
            cydia.bool("trusted") && front.contains { $0.contains("/home/") } && front.count > 1,
            "\(d): Cydia's front page and \(max(front.count - 1, 0)) of its resources through the web proxy"
                + (cydia.string("trustError").map { ": \($0)" } ?? "")
        )
        let lists = cydia["lists"] as? [String] ?? []
        r.check(
            ["apt.saurik.com", "apt.thebigboss.org", "modmyi.saurik.com", "zodttd.saurik.com"].allSatisfy { host in
                lists.contains { $0.hasPrefix(host) && $0.hasSuffix("_Packages") }
            },
            "\(d): Cydia refreshed its sources (\(lists.filter { $0.hasSuffix("_Packages") }.joined(separator: ", ")))"
        )
        if base.board != "k48ap" {
            r.check(
                (cydia.string("bcStatus") ?? "").hasPrefix("install ok installed") && cydia.string("bc") == "42",
                "\(d): Cydia installs bc from Cydia/Telesphoreo (\((cydia["taps"] as? [String] ?? []).joined(separator: ", ")))"
                    + " and it runs: 6*7 = \(cydia.string("bc") ?? "")"
            )
        }
        if (3...6).contains(base.major) {  // Substrate where FirmwareKit's jailbreak installs it
            let ms = events.one("substrate", ["device": d])
            let loaded = ms["loaded"] as? [String] ?? []
            let errors = ms["errors"] as? [String] ?? []
            r.check(
                ms.bool("injected") && loaded.contains { $0.hasSuffix("/LTMProbe.dylib") } && errors.isEmpty
                    && !ms.bool("safeMode") && ms.bool("back") && !ms.bool("locked"),
                "\(d): Substrate loads into SpringBoard with its extension (\(loaded.joined(separator: ", "))), no "
                    + "Safe Mode, SpringBoard back and unlocked"
                    + ((ms.string("error") ?? errors.first).map { ": \($0)" } ?? "")
            )
            if base.major == 3 {
                let cydia = ms["cydiaLoaded"] as? [String] ?? []
                r.check(
                    cydia.contains { $0.hasSuffix("/CydiaHTTPatch.dylib") },
                    "\(d): Cydia loads HTTPatch (\(cydia.joined(separator: ", ")))"
                )
            }
        }
    }
    // The device's language and region: create --language's (`--language`), else the Mac's (its first preferred
    // language the firmware has; English for one it has not).
    func languageCode(_ tag: String) -> String { Locale(identifier: tag).language.languageCode?.identifier ?? "en" }
    let language = languageCode(args.language ?? Locale.preferredLanguages.first ?? "en")
    let expectedLocale =
        (args.language.flatMap { Locale(identifier: $0).region } ?? Locale.current.region)
        .map { "\(language)_\($0.identifier)" } ?? language
    if args.language != nil {
        let seen = events.one("language", ["device": d])
        r.check(
            seen.string("language").map(languageCode) == language && seen.string("locale") == expectedLocale,
            "\(d): the language and region it was prepared with: \(seen.string("language") ?? "?") "
                + "\(seen.string("locale") ?? "?") (expected \(language) \(expectedLocale))"
        )
    }
    if let keyboards = args.keyboards {
        let seen = events.one("language", ["device": d]).string("keyboards") ?? "?"
        r.check(seen == keyboards, "\(d): the keyboards it was prepared with: \(seen) (expected \(keyboards))")
    }
    if args.skipSetup {
        // Prepared past Setup: no Setup page on the first boot (a phone's lock screen slide is the only step the
        // walker may take), and Setup's answers as FirmwareKit seeded them: the Mac's region, Location Services off.
        let pages = events.find("setup", ["device": d]).compactMap { $0.string("detail") }
        r.check(
            pages.allSatisfy { $0 == "Setup walked: (slide)" },
            "\(d): no Setup page came up: \(pages.isEmpty ? "no walk" : pages.joined(separator: "; "))"
        )
        let seed = events.one("setupSeed", ["device": d])
        let mac = expectedLocale
        r.check(
            !(seed.string("firstFront") ?? "com.apple.purplebuddy").hasPrefix("com.apple.purplebuddy")
                && seed.bool("setupDone"),
            "\(d): Setup finished before the first boot (first answer: \(seed.string("firstFront") ?? "none"), "
                + "SetupDone \(seed.bool("setupDone")))"
        )
        r.check(
            seed.string("locale") == mac && seed.string("language").map(languageCode) == language,
            "\(d): Setup's region and language: \(seed.string("locale") ?? "?") \(seed.string("language") ?? "?") "
                + "(expected \(mac) \(language))"
        )
        r.check(seed.string("location") == "0", "\(d): Location Services off: \(seed.string("location") ?? "?")")
    }
    if args.developerImage {
        let mount = events.one("developerImage", ["device": d])
        r.check(
            mount.string("mounted") == "mounted" && !mount.bool("before") && mount.bool("after"),
            "\(d): the Developer Disk Image mounts and Settings' Developer bundle appears (\(mount))"
        )
    }
    if args.accessibility {
        let ax = events.one("accessibility", ["device": d])
        let (inverted, restored) = (ax.double("inverted") ?? -1, ax.double("restored") ?? -1)
        // The share of the frame's sampled bytes that are the Home frame's inverse, then the Home frame again.
        r.check(
            ax.string("invertSet") == "ok" && ax.string("invertOn")?.contains("invertColors") == true && inverted > 0.6,
            "\(d): White on Black turns on, the guest says so and the frame inverts (\(format(inverted, 2)) of it, "
                + "on: \(ax.string("invertOn") ?? "?"))"
        )
        r.check(
            ax.string("voiceOverSet") == "ok" && ax.string("voiceOverOn")?.contains("voiceOver") == true,
            "\(d): VoiceOver turns on and the guest says so (on: \(ax.string("voiceOverOn") ?? "?"))"
        )
        if args.audioWAV != nil {
            let (quiet, spoken) = (ax.int("loudQuiet") ?? -1, ax.int("loudSpoken") ?? -1)
            r.check(
                spoken > max(quiet, 0) * 4 + 2000,
                "\(d): VoiceOver is heard (loud samples \(quiet) quiet, \(spoken) spoken; the guest's WAV "
                    + "\(ax.int("wavBytes") ?? -1) bytes)"
            )
        }
        if let triple = ax.string("tripleClick") {
            r.check(
                triple.contains("voiceOver") && ax.string("tripleClickAfter") == "",
                "\(d): three quick Home presses turn VoiceOver on with Triple-click Home set to it (on: '\(triple)', "
                    + "then '\(ax.string("tripleClickAfter") ?? "?")')"
            )
        }
        r.check(
            ax.string("voiceOverOff") == "ok" && ax.string("invertOff") == "ok" && ax.string("after") == ""
                && restored > 0.6,
            "\(d): both off again (on: '\(ax.string("after") ?? "?")', \(format(restored, 2)) of the frame as before)"
        )
    }
    if !args.media.isEmpty { judgeMedia(r, d, events: events, reboot: args.reboot, major: base.major) }
    if let file = args.readFile {
        let reads = events.find("fileRead", ["device": d])
        r.check(!reads.isEmpty && reads.allSatisfy { $0.bool("found") }, "\(d): the guest agent reads back \(file)")
    }
    let after = SessionJudge.tree(base.url)
    r.check(
        after == before,
        "\(d): the prepared base is unchanged" + (after == before ? "" : ": " + SessionJudge.treeDiff(before, after))
    )
    r.check(events.any("done") && status == 0, "driver finished (exit \(status.map(String.init) ?? "timeout"))")
    for e in events.find("screenshot") {
        print(
            "   \(e.string("path") ?? "")  (\(e.int("width") ?? 0)x\(e.int("height") ?? 0), brightness \(format(e.double("brightness"), 2)))"
        )
    }
    finish(r, work: work)
}

/// `single --panel`: free-form Apply on a running device. Both boots reach Home; the second's frame is the panel; the
/// dock (the shipped screen's bottom fifth) is the same picture in the new bottom band; a tap on its first icon
/// brings an app up; the second boot shuts down cleanly.
func resized(_ r: Report, _ d: String, panel: String, events: Events) {
    let homes = events.find("home", ["device": d])
    r.check(
        homes.count == 2 && homes.allSatisfy { ($0.double("brightness") ?? 0) >= 0.05 },
        "\(d): Home before and after the restart: \(homes.map { "\($0.string("screen") ?? "?") \(format($0.double("brightness"), 2))" })"
    )
    let size = events.one("resized", ["device": d])
    let got = "\(size.int("width") ?? 0)x\(size.int("height") ?? 0)"
    r.check(got == panel, "\(d): the frame after Apply is \(got), the panel \(panel)")
    let dock = events.one("dock", ["device": d])
    let differs = dock.double("differs") ?? -1
    r.check(
        differs >= 0 && differs <= 0.15,
        "\(d): the dock row is at the new bottom (\(format(differs * 100, 1))% of its blocks differ from before)"
    )
    let tap = events.one("tapLanded", ["device": d])
    let front = tap.string("frontmost") ?? ""
    let changed = tap.double("changed") ?? -1
    r.check(
        (front.isEmpty || front != "com.apple.springboard") && changed > 0.3,
        "\(d): a tap on the first dock icon opens \(front.isEmpty ? "an app" : front) (\(format(changed * 100, 0))% of the screen changed)"
    )
    let quits = events.find("quit", ["device": d])
    r.check(
        quits.count == 2 && quits.allSatisfy { $0.bool("exited") }
            && quits.contains { ($0.double("confirmed") ?? -1) >= 0 },
        "\(d): Stop for Apply, then a clean shutdown at the new size"
    )
    for e in events.find("screenshot") {
        print("   \(e.string("path") ?? "")  (\(e.int("width") ?? 0)x\(e.int("height") ?? 0))")
    }
}

/// `single --media`: every file imported; each song or video in the music library under its title, each photo a new
/// original in DCIM and (4.x on) a new asset in Photos' database; and on a reboot all of it still there.
func judgeMedia(_ r: Report, _ d: String, events: Events, reboot: Bool, major: Int) {
    let imports = events.find("mediaImport", ["device": d])
    for e in imports {
        r.check(
            e.bool("ok"),
            "\(d): \(e.string("destination") ?? "?") imports “\(e.string("title") ?? e.string("file") ?? "?")” in "
                + "\(format(e.double("seconds"), 0)) s" + (e.string("error").map { ": \($0)" } ?? "")
        )
    }
    let counts = events.find("mediaCount", ["device": d])
    guard let before = counts.first(where: { $0.string("stage") == "before" }) else {
        r.check(false, "\(d): the libraries were read before the import")
        return
    }
    let afters = counts.filter { $0.string("stage") != "before" }
    let photos = imports.filter { $0.string("destination") == "Photos" }.count
    let library = imports.filter { $0.string("destination") != "Photos" }.count
    func summary(_ e: Event) -> String {
        "\(e.string("library") ?? "?") items \(e.int("items") ?? -1), found \(e["found"] as? [String: Any] ?? [:]), "
            + "originals \(e.int("originals") ?? -1), assets \(e.int("assets").map(String.init) ?? "-")"
    }
    print("   media before: \(summary(before))")
    for e in afters {
        let found = (e["found"] as? [String: Any] ?? [:]).mapValues { ($0 as? NSNumber)?.intValue ?? 0 }
        let boot = e.int("generation") ?? 1
        let label =
            (boot == 1 ? "" : "after reboot \(boot - 1), ")
            + (e.string("stage") == "music" ? "Music opened" : "after the import")
        if library > 0 {
            r.check(
                found.count == library && found.values.allSatisfy { $0 >= 1 }
                    && (e.int("items") ?? 0) >= (before.int("items") ?? 0) + library,
                "\(d): \(label): the music library holds the imports (\(summary(e)))"
            )
        }
        if photos > 0 {
            let assets = major < 4 || before.int("assets").map { (e.int("assets") ?? 0) >= $0 + photos } ?? true
            r.check(
                (e.int("originals") ?? 0) >= (before.int("originals") ?? 0) + photos && assets,
                "\(d): \(label): Saved Photos holds the imports (\(summary(e)))"
            )
        }
    }
    let opened = events.find("musicOpened", ["device": d]).map { $0.string("front") ?? "" }
    let music = ["com.apple.mobileipod", "com.apple.Music"]
    r.check(
        afters.count == (reboot ? 3 : 2) && opened.count == (library == 0 ? 0 : reboot ? 2 : 1)
            && opened.allSatisfy { front in music.contains { front.hasPrefix($0) } },
        "\(d): the libraries were read after the import and with Music open on each boot (frontmost \(opened))"
    )
}

func hex(_ value: Int) -> String { "0x" + String(value, radix: 16) }
