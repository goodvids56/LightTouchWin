# Contributing to Light Touch

> **LightTouchWin is a fork.** The Windows build this fork adds lives in `windows/` and is described in
> [windows/README.md](windows/README.md): it cross-compiles from Linux and needs no Mac. Everything below is the
> upstream Mac app's guide, kept as it is so the fork can follow [LightTouchMac](https://github.com/samhenrigold/LightTouchMac);
> building or testing the Mac app needs a Mac with Xcode, and preparing devices with `firmwarekit` (below) still does.

Light Touch is a native macOS (AppKit) app that runs a library of emulated legacy iOS devices.
The device catalog covers the iPhone (m68ap), iPhone 3GS (n88ap), iPhone 4 (n90ap), iPod touch 1G to 4G
(n45ap, n72ap, n18ap, n81ap) and iPad (k48ap). Each device is prepared
from a stock Apple IPSW by the bundled Swift preparer, `firmwarekit` (`Packages/FirmwareKit`), using
the keys pinned in `LightTouchMac/Resources/firmware-catalog.json`; there are no hand-prepared images
per firmware. Each running device is its own helper process (`LightTouchDevice`), which is the only
thing that links the emulator.

## Firmwares in the catalog

The shared [firmware catalog](LightTouchMac/Resources/firmware-catalog.json) is the source of truth for
builds, availability, keys and preparation recipes. The CLI reads that same catalog:

```sh
firmwarekit create --catalog LightTouchMac/Resources/firmware-catalog.json \
  --id k48ap-7B500 --ipsw /path/to/stock.ipsw --out /path/to/new-device \
  --helper /path/to/LightTouchDevice --guest-tools /path/to/ipad-guest-tools
```

Catalog availability is distinct from measured acceptance.

## The three repositories

| Repo | What it is | How this app uses it |
|---|---|---|
| [LightTouchMac](https://github.com/samhenrigold/LightTouchMac) | This app, the per-device helper, the Swift preparer, the app tests, the product build | — |
| [qemu-ios](https://github.com/samhenrigold/qemu-ios) (branch `main`) | The emulator (`hw/arm/ipod_touch_*.c`, `hw/arm/ipad1.c`, `hw/arm/s5l8930_*.c`), the guest tools under `contrib/` (agent, GL shims, AppSync, guest packages), Python format inspection tools and frozen bake references, and the emulator gates under `tests/` | Pinned by commit in `build-support/sources.json`. Loaded by the helper as `libqemu-arm.dylib` (`contrib/macos-app/make-dylib-macos.sh`), which it refuses unless `qemu_ios_api_version()`'s major matches `Qemu.apiMajor` (bump both when an entry point is removed or changed); its `contrib/export-guest-artifacts.sh` builds and stages the guest tools, GL tables, entitlements and headers with a manifest, and `scripts/build-guest-tools.sh` is a thin caller of it |
| [usbmuxd](https://github.com/samhenrigold/usbmuxd) (fork, branch `idle-poll` on `qemu-zlp`) | The usbmuxd that bridges the emulated USB device to libimobiledevice | Built into the bundle from the commit pinned in `build-support/sources.json`; the emulator and this fork ship together, so bump both pins in one commit |

The emulator side's own entry point is qemu-ios `README.md`.

## Building

Open `LightTouchMac.xcodeproj` in Xcode and build the `LightTouchMac` scheme: the app, the device helper
(`LightTouchDevice`), the services helper (`LightTouchServices`) and the preparer (`firmwarekit`, from
`Packages/FirmwareKit/Sources/FirmwareKitCLI`). The app's one script phase stamps the build number
(`CFBundleVersion`, the commit count).

Debug and Release share `Configuration/Shared.xcconfig`. A Debug helper loads `libqemu-arm.dylib` from a
qemu-ios development build:

```
QEMU_IOS_DIR   = $(HOME)/Developer/qemu-ios-ipad1
QEMU_BUILD_DIR = $(QEMU_IOS_DIR)/build-w1-native          # libqemu-arm.dylib, on a Debug helper's rpath
```

These repeat **the pin**, `build-support/sources.json`: the qemu-ios commit, its expected checkout path and
development build directory, and the usbmuxd commit; iBoot32Patcher is pinned in `build-support/dependencies.json`.
`scripts/sources` resolves the pin for every script (`sources qemu-ios | usbmuxd | qemu-build | commit NAME`,
`sources check` for pinned vs actual; `QEMU_IOS_DIR`, `USBMUXD_SOURCE_DIR` and `QEMU_BUILD_DIR` override).
To produce the dylib, build `qemu-system-arm` in that checkout and run `contrib/macos-app/make-dylib-macos.sh BUILD_DIR`.
The services helper links libimobiledevice and libplist: from the vendor directory (below) when there is one,
else Homebrew's. Put local overrides of any of these in `Configuration/Local.xcconfig` (gitignored).

Debug-only knobs (compiled out of Release): `LTM_FIRMWAREKIT=/path/to/firmwarekit` (another preparer),
`LTM_QEMU_DYLIB` (another emulator library), `LTM_USBMUXD` (another usbmuxd). `LTM_STATE_DIR=/dir` keeps all
writable state and logs inside one directory.

## Releases

A release is an Xcode archive; nothing edits the bundle after Xcode.

1. **`scripts/vendor`**, once per pin change (`build-support/sources.json`, `dependencies.json`, the patches or the
   native recipes). It builds what Xcode can't, from the pins, into `~/Developer/ltm-vendor.noindex/<pin-hash>/`
   (`.noindex` keeps Spotlight out; `LTM_VENDOR_ROOT` moves it; nothing deletes it, and a build that only the old
   `~/Developer/ltm-vendor` has complete is still used there) and points `Configuration/Vendor.xcconfig` (`VENDOR_DIR`) at it:
   the native dependencies, usbmuxd and QEMU for arm64 and x86_64, merged universal with final install names
   (`Frameworks/`, `MacOS/`), the guest tools and the developer SSH payload packed into
   `Resources/Guest/guest.aar`, the SecureROMs and the built-in iPod (`Resources/Device/`), the licenses and
   `build-inputs.json`. A rerun with nothing changed does nothing; one after a FirmwareKit or helper change only
   prepares the built-in iPod again. Inputs: `ARMV6_SDK` (default
   `~/Developer/ipod2g-re/OldSDK/iPhoneOS3.1.3.sdk`), `LTM_ASSETS` (the SecureROMs, default
   `~/Developer/qemu-ios-files`), the n72ap-7E18 IPSW in the app's IPSW cache, and the network for the pinned
   source archives. Firmware and the iPhoneOS SDK are never downloaded or redistributed.
2. **Xcode ▸ Product ▸ Archive** (scheme `LightTouchMac`, Release, arm64 + x86_64, Developer ID). The archive's
   Copy Files phases bring in the vendor directory (Code Sign On Copy signs its binaries with the app's identity
   and the hardened runtime); only `LightTouchDevice` carries entitlements (`Configuration/LightTouchDevice.entitlements`).
   Archiving takes only Xcode's own time.
3. **Test the archive**: `TEST_RUNNER_LTM_RELEASE_ARCHIVE=path/to/X.xcarchive xcodebuild test -workspace
   LightTouchMac.xcworkspace -scheme LightTouchMac -testPlan Release` (`TEST_RUNNER_LTM_RELEASE_APP` takes an app
   instead). `Packages/ReleaseChecks` checks the archived app: it is built from the current pins; every Mach-O
   signed, hardened, universal, with the declared entitlements and an in-bundle load closure at the app's minimum
   macOS; the bundled helper, services worker and bridge run from the bundle and the helper loads the bundled
   emulator; bundle hygiene (licenses, sources, Help, no local paths, the built-in iPod's placeholder identity,
   stripped, no duplicates, nothing loose under `Resources`, the guest archive included); the identity scan; and the
   bundled `firmwarekit` unpacks the built-in iPod, which `sessions single` (tests/sessions) boots through
   the bundled helper, dylib, services worker and usbmuxd; and the prepare matrix: the bundled `firmwarekit create`,
   with its default helper and guest tools, prepares one firmware per preparation route (n45ap-4A102, n72ap-7E18,
   k48ap-7B500, n90ap-8A293, n88ap-10B500; two at a time) and each is booted the same way. Their IPSWs come from the
   app's IPSW cache (the catalog's sha1); a missing one fails the plan. `TEST_RUNNER_LTM_RELEASE_SKIP_PREPARE=1`
   skips the matrix, for development runs only.
4. **Notarize and make the download**, either way; both write `LightTouchMac-universal.zip` (`ditto -c -k
   --keepParent` of the stapled app) and `SHA256SUMS` naming it beside the exported app, since Xcode has no hook
   after notarization:
   - **`scripts/release path/to/X.xcarchive`** (from a shell that sees the `ltm-notary` notarytool keychain
     profile): exports for Developer ID (`Configuration/ExportOptions.plist`), notarizes a zip of the app
     (`notarytool submit --wait`; on Invalid it prints the log), staples and validates the ticket, writes the
     download, then runs the Release plan's static checks on the stapled app as an export, its boots (the
     built-in iPod and the prepare matrix; `LTM_RELEASE_SKIP_PREPARE=1` skips the matrix with `--dry-run` and is
     refused without it) and `spctl -a -vv`.
     It prints the steps, the zip's path and its SHA-256, or the failure. `--dry-run` skips notarizing and stapling.
   - **Organizer ▸ Distribute App ▸ Direct Distribution**, then **`scripts/check-export "path/to/Light Touch.app"`**:
     it writes the download and runs the Release plan on the zip's unzipped copy as an export (the stapled ticket
     and Gatekeeper's "Notarized Developer ID" besides the static checks and the built-in iPod's boot).

## Gates

Three commands, no runner around them:

```sh
# no emulator: every package's Swift Testing suites (the list is Unit.xctestplan)
xcodebuild test -workspace LightTouchMac.xcworkspace -scheme LightTouchMac -testPlan Unit
# emulator sessions on prepared bases (firmwarekit create output), one boot at a time; `sessions --help` for the rest
swift run --package-path tests/sessions sessions single BASE [--launch] [--reboot]
swift run --package-path tests/sessions sessions phone N90_BASE --only carrier --overlay PAST_SETUP_OVERLAY
swift run --package-path tests/sessions sessions local-network N72_BASE
# a built app (Releases, step 3)
TEST_RUNNER_LTM_RELEASE_APP="path/to/Light Touch.app" xcodebuild test -workspace LightTouchMac.xcworkspace \
    -scheme LightTouchMac -testPlan Release
```

`sessions` builds the Debug helper, services worker and firmwarekit (cached in `.build/sessions-xcode`) and boots with
scripts/vendor's dylib, usbmuxd, SecureROMs and guest package, or with a built app's (`--app`); `--dylib` takes a
development build for the Debug helper (it is refused with `--app`, whose helper loads the app's own dylib). It prints one ok/FAIL line per check and exits 1 on any FAIL; logs, screenshots and the driver's
events stay in `--work`. It judges the Home screen by the guest agent's frontmost app and screen where the base has an
agent, and by a frame reference in `tests/sessions/matrix-refs` where there is one. No test run writes the real app
state or logs: the Unit plan's packages run in a private home, and `sessions` keeps the drivers' state in its work
directory and fails a run that touched the real `app.log`.

| Directory | What is there |
|---|---|
| `Packages/*/Tests`, `Unit.xctestplan` | Swift Testing: LightTouchCoreTests (the app's logic), HostRuntimeTests, FirmwareKitTests, HostServiceWireTests, ReleaseChecksTests' fixtures, BuildToolsTests (the native merge, the dependency sources). LightTouchCore's tests run one at a time in a private home (Tests/TestIsolation) |
| `tests/offline/` | A Swift package (OfflineTests, in the Unit plan) for what needs the app target's own files: AppKit views rendered offscreen, the display's input and layout, the helper's C API version and LCD color, the services engine against a fake libimobiledevice. The production files are compiled whole through symlinks beside small stand-ins; no window is ordered in |
| `tests/sessions/` | A Swift package: `sessions` (the checks and their verdicts), the drivers that stand in for the app (`session-driver`: the app's session code; `helper-driver`: DeviceLink straight to the helper), and SessionKit (frame references, the Home verdict, the Setup walks), whose tests are in the Unit plan |
| `tests/activation/`, `tests/fixtures/` | C harnesses run by hand (`tests/activation/run.sh`: the activation finish and the lockdown zone writes); the fake preparer (`fake-firmwarekit`, bash), the store fixtures and `machines.json` the tests share |
| `swift test --package-path Packages/FirmwareKit` | Also runs FirmwareKit's suites alone. Optional corpus tests report real skips unless `FK_TEST_CORPUS=1` or `FK_REQUIRE_FIXTURES=1` selects them; selected missing inputs fail. Format/bake oracle checks compare against qemu-ios's imgtools through `FIRMWAREKIT_QEMU_IOS` |

Every headless boot passes `-audio driver=none`. The checks are headless; none of them launch the app.

## Style

Apple's [API Design Guidelines](https://www.swift.org/documentation/api-design-guidelines/) first; where they are
silent, [Google's Swift style guide](https://google.github.io/swift/). American spelling, except Apple's cancelled,
cancelling and cancellable.

**Format.** `swift-format` owns layout and the lint rules (`.swift-format`: 4 spaces, 120 columns, one argument per
line once a call wraps, no blank line after `{` or before `}`, implicit returns, `[]`/`[:]` for empty collections,
`for … where` over an `if` wrapping a loop body); `clang-format` (`.clang-format`) owns C. Before committing:
`swift-format format -i -r <paths>`; `swift-format lint -r <paths>` should stay quiet.

**Warnings.** None. Fix the cause: a `@Sendable` closure or a lock-guarded `@unchecked Sendable` box for state shared
across threads, `MainActor.assumeIsolated` in an AppKit completion that runs on the main thread, `nonisolated` on test
fixtures in the MainActor-by-default packages. A deprecated API we must keep goes in one small C file with a scoped
`#pragma clang diagnostic` and a comment saying why (`LightTouchDevice/GuestTLS.c`).

**Files.** One main type per file, named after it; helpers only it uses can live beside it. No created-by or license
headers. A file whose role isn't obvious from its name opens with a short `//` paragraph saying what it is and why,
then the imports (sorted, one per line). `// MARK: - Topic` separates the sections of a long type; short files have
none.

**Access and types.** `private` by default, `private(set)` for state others read; never spell out `internal`;
`public` only for what another module uses. Access levels go on members, not on `extension`. Classes are `final`
unless designed for subclassing; prefer structs and enums; `actor` or a lock for shared mutable state. A conformance
that is its own topic (a delegate, a data source) goes in its own `extension`; `Sendable`, `Hashable` and the like
stay on the declaration.

**Naming.** Clear at the call site, no `get` prefixes, Booleans read as assertions (`isRunning`), factories start
with `make`. Acronyms keep one case: `deviceID`, `ipaURL`, `urlSession`. Wire and catalog names live in raw values
and `CodingKeys`, not in Swift identifiers, in new code.

**Comments.** `///` for documentation, never `/** */`; the first sentence summarizes; `- Parameter`/`- Returns` only
when they add something. Comments say why, not what. A trailing comment that won't fit in 120 columns goes above the
line.

**Code.** `guard` for early exits; `for`-`in` for a loop body (`.forEach` for one call); no `!`, `try!` or implicitly unwrapped optionals in new
non-test code unless the value can't be absent. `@MainActor` for UI state; `async`/`await` over completion handlers
in new code; `[weak self]` with `guard let self` in escaping closures, on the outermost closure that captures it.

**SwiftUI.** `@Observable` models, not `ObservableObject`; `@State` and `@Environment` are `private`. Properties,
then `init`, then `body`, then private helpers. A piece with its own state, or longer than a screen, becomes a
`private struct`; a small piece stays a `private var something: some View`. Previews use `#Preview`.

**Tests.** Swift Testing (`@Test`, `#expect`); test names are camelCase sentences stating the behavior
(`aBootThatEndsBeforeUSBEndsItsStartup`).

## Boards

A board's facts have two homes. The emulator's: qemu-ios's `qemu_ios_device_info` table
(`contrib/ios-app/qemu-ios-ui.c`): the `-M` machine, the board ID, the screen, the modem, the USB host, the compass,
the USB charger and the `panel=` limits; the helper reports them in its hello and lists them with
`LightTouchDevice '{"mode":{"machines":{}}}'`, and the app reads them as `DeviceInfo` (`Board.hardware`). The app's: one `Board`
case and its `Facts` in `Packages/HostRuntime/Sources/HostRuntime/Board.swift`: the names, the model ID, the SoC
family (which decides how its prepared base boots and its guest architecture) and the art. Adding a board touches
those two places; its firmware is catalog entries, and preparing it is a FirmwareKit recipe if no existing one fits.
`tests/fixtures/machines.json` is the listing the tests use without an emulator; the Release plan
(`ReleaseAppStaticTests`) holds each of its machines to the bundled library's.

`device.lock.json` is `DeviceLock` (HostRuntime): FirmwareKit writes it, everything else reads it through that type.

## Rules

- **No Python bridge.** The app runs one preparer, the Swift `firmwarekit`. qemu-ios's Python `imgtools/` is the test oracle, not a runtime.
- **Activation is maintained separately.** FirmwareKit runs it as a built-in preparation step
  (`Packages/FirmwareKit/Sources/CActivation`, `tools/activation/`). Treat it as a black box.
- **No firmware in any repo.** IPSWs, decrypted components, NAND images, SDKs and prepared devices stay
  outside the three repositories; the app downloads or imports them on the user's Mac.
- **Branches.** Both repositories work on `main`. Make a short branch per change and open a pull request;
  `main` doesn't accept force-pushes. A qemu-ios change the app needs lands there first, then the app's
  pin (`build-support/sources.json`) moves to it in the same pull request as the app change.

## Layout

| Path | What |
|---|---|
| `LightTouchMac/` | The app, one directory per layer (below), plus `Resources/firmware-catalog.json` (its `bundled` names the built-in iPod, `Resources/Device/n72ap-7E18.itbase`, which a fresh install unpacks and selects; `first_run` is what a first launch selects without it: an `available` build Apple still serves), `Assets.xcassets`, `Shim/` |
| `LightTouchMac/Transport/` | The wire to a device and the app's logs: `USBMux` (each device's usbmuxd), `NativeLogging`, `AppEventLog` |
| `LightTouchMac/Services/` | What the app layers on the stock services: `LockdownTools` (the lockdown-tz and lockdown-mcinstall writes run as child processes of the services helper, `LightTouchServices/Lockdown`), `MediaStaging`, `HomeScreenOrdering`, `ClockRegion` |
| `LightTouchMac/Guest/` | The guest agent: `GuestAgent` (the wire and typed ops), `GuestServices` (media commit, trust, proxy route, respring, launch), `GuestPackage` |
| `LightTouchMac/Library/` | What the app keeps: `DeviceInstance`, `DeviceLibrary`, `DeviceStateStorage`, `StorageLocations`, `Bundled`, `LegacyState`, `IPSWStore`, `FirmwareCatalog`, `FirmwareJobs`, `FirmwareDownloads`, `PreparationJob`, `IPALibrary`, `IPAMembers`, `AppMetadataCache` |
| `LightTouchMac/Device/` | One running device: `Board+App` (what the app derives from a board), `DeviceSession`, `DeviceProcess` (its helper), `EmulatorController` (lifecycle and input; vends `services`, `guest`, `installPipeline`), `DeviceRow`, `DeviceConnectionIssue`, `DeviceFileWatch`, `WebProxyConfiguration` |
| `LightTouchMac/Features/` | What the app does with a device: `AppInstaller` (the per-device install and removal queue), `AppInstallPipeline`, `MediaImport` (+ `Media*`, `PreparedMedia`), `WebProxySetup`, `CaptureController` (+ recording, movie writer, canvas capture, capture preferences), `CatalogClient`/`CatalogCopy`, `DiagnosticsExport` |
| `LightTouchMac/UI/` | Windows, views and view controllers: `MainWindowController`, the sidebar, placeholder, device and inspector view controllers, `DisplayView`, `DeviceModelView`, `DroppedFiles`, the Files, log, storage, proxy and capture panels, small controls |
| `LightTouchMac/App/` | `main`, `AppDelegate`, `MainMenu`, `WindowRestorationPolicy`, `NetworkAccessPreference` |
| `LightTouchDevice/` | The per-device helper: one QEMU instance, frames over IOSurface, control over the `DeviceRuntime` link, and the device's web proxy (`WebProxy` on URLSession, `WebProxyAdapters`; `GuestTLS.c`, the TLS 1.0 old guests speak, on SecureTransport) behind the 10.0.2.100:3128 guestfwd |
| `Packages/DeviceRuntime/` | The app–helper link (`DeviceLink`, `DeviceLinkProtocol`, `DeviceRendezvous`, the `CLink` module); `WebProxyCA`, the per-device proxy CA both sides use |
| `Packages/LightTouchCore/` | The app's logic that needs no window, mirroring `LightTouchMac/`'s layers (Library, Device, Session, Features, Guest, Apps, Catalog, Input, Capture, UI models); the app target keeps the AppKit and SwiftUI |
| `Packages/ReleaseChecks/` | The checks of a built app (`Release.xctestplan`): signatures, entitlements, slices and load closure, bundle hygiene, the bundled tools, boots through the bundle |
| `Packages/DeviceServices/` | One device's stock lockdown services: `HostServiceWire` (the request/event protocol, errors, timeouts, device paths, the Home screen layout) and `HostServiceClient` (the app's `DeviceServices` calls, `HostServiceWorkers`, `NotificationProxy`) |
| `Packages/FirmwareKit/` | `FirmwareKit` (IPSW → device), `FirmwareSchema` (the wire types, `StorageCapacity`, `DeveloperTools`, `GuestArchive`: what the app links), the `firmwarekit` CLI (`Sources/FirmwareKitCLI`), `CActivation` |
| `scripts/` | `vendor` (with `build-package-native.sh`, `build-static-deps.sh`, `build-iboot32patcher.sh`, `build-guest-tools.sh`), `ltm-build` (Packages/BuildTools: vendor, check-macho, merge-native, sources), `sources`, `release`, `check-export` |
| `tests/` | `offline/` and `sessions/` (Swift packages), `activation/` (C harnesses), `fixtures/` |
| `build-support/` | `dependencies.json` (pinned archives) and build patches |
| `Configuration/` | `Shared.xcconfig` (with the gitignored `Vendor.xcconfig` and `Local.xcconfig`), `LightTouchDevice.entitlements` |
| `Models/` | The 3D device models and their lighting (`Resources/Models/` in the app) |
| `LightTouchServices/` | The services helper: `Engine/` (libimobiledevice: installation_proxy, AFC, springboardservices, notification_proxy and lockdown on one `run` kernel, the serial gate and deadlines), and the lockdown writes (`Lockdown/`) it runs as child processes |
| `tools/activation/` | Sam's activation tool (its `build/` output is ignored) |
