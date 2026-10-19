# Light Touch for Windows

> **This is a fork.** LightTouchWin is a Windows port of [Light Touch](https://github.com/samhenrigold/LightTouchMac)
> by Sam Henri Gold, which runs on the Mac. The emulator, the device preparation and nearly all of the work are the
> original project's; this fork adds a Windows build (`windows/`). Report problems with the Windows build here, and
> problems with the Mac app [upstream](https://github.com/samhenrigold/LightTouchMac/issues).

Built with use from agentic coding products.

Light Touch runs classic Apple devices on your PC: the original iPhone and iPod touch through the iPhone 4, iPod
touch 4 and the first iPad, on the iPhone OS and iOS versions they shipped with. Each device runs Apple's own
firmware in an emulator ([qemu-ios](https://github.com/samhenrigold/qemu-ios)), so it behaves like the real thing:
the Home screen, the built-in apps, App Store apps and games of the era, Wi-Fi, music and photos.

## Download

Get `LightTouch-windows-x64.zip` from [Releases](https://github.com/goodvids56/LightTouchWin/releases) (or from the
latest run of the **Windows build** workflow under Actions), unzip it anywhere, and run **LightTouch.exe**.

- Windows 10 or 11, 64-bit (x64). Nothing to install: the folder carries everything, .NET included.
- Keep the folder's files together: LightTouch.exe loads the emulator, `qemu-arm.dll`, from beside it.

## Getting a device

The Windows build doesn't prepare devices from Apple's IPSWs yet (the Mac app's preparer, FirmwareKit, is
macOS-only so far). There are two ways in:

1. **The built-in iPod touch (2nd generation) on iOS 3.1.3.** Download `LightTouchMac-universal.zip` from
   [Light Touch for Mac's releases](https://github.com/samhenrigold/LightTouchMac/releases) (no Mac needed, and no need
   to unzip it), then choose **Add ▸ Import from Light Touch for Mac…** and pick the zip. Light Touch unpacks the iPod,
   gives it an identity of its own, and installs the SecureROMs the emulator boots.
2. **A device prepared on a Mac** with `firmwarekit create … --out FOLDER` (see [CONTRIBUTING.md](CONTRIBUTING.md)):
   copy the folder to the PC and choose **Add ▸ Import Prepared Device…**.

## Using a device

Double-click a device to start it; each runs in its own window and process.

| Input | Does |
|---|---|
| Click and drag on the screen | Touch (one finger); **Alt**-drag pinches with a mirrored second finger |
| Typing | Goes to the device |
| **Ctrl+H** / **Ctrl+L** | Home / Sleep/Wake |
| **Ctrl+Up** / **Ctrl+Down** | Volume |
| **Ctrl+Left** / **Ctrl+Right** | Rotate |
| **Ctrl+Shift+Z** | Shake |
| **Ctrl+V** | Paste the PC's clipboard text into the device (and text copied in the device lands on the PC's clipboard) |
| **Ctrl+S** | Screenshot, to `Pictures\Light Touch` |

Closing the window stops the device the way the Mac app does (the VM pauses, which flushes its storage, then quits).
**Device ▸ Erase All Content and Settings** starts it over from its base. Devices, logs and settings live in
`%LOCALAPPDATA%\LightTouch` (`LTM_STATE_DIR` moves them).

## Devices

| Device | Versions |
|---|---|
| iPhone | iPhone OS 1.0 |
| iPhone 3GS | iPhone OS 3.0 to iOS 6.1.6 |
| iPhone 4 (GSM) | iOS 4.0 to 7.1.2, plus iOS 6.0 and 7.0 betas |
| iPod touch (1st gen) | iPhone OS 1.1 to 1.1.5 |
| iPod touch (2nd gen) | iPhone OS 2.1.1 to iOS 4.2.1 |
| iPod touch (3rd gen) | iPhone OS 3.1.1 to iOS 5.1.1 |
| iPod touch (4th gen) | iOS 4.1 to 6.1.6, plus the iOS 6.0 beta |
| iPad | iPhone OS 3.2 to iOS 5.1.1 |

These are the boards and versions Light Touch supports. The Windows emulator builds all eight machines and boots
prepared devices of each with the Mac's boot recipes; the one checked end to end so far is the built-in iPod touch
(2nd gen) on iOS 3.1.3: it boots to the lock screen, unlocks, and runs its apps with touch, typing, rotation and a
clean stop.

## What the Windows build has, and what it doesn't yet

Has: the device library, importing the built-in iPod or prepared devices, the device window (touch, buttons,
keyboard, rotation, shake, pasteboard both ways, screenshots), Wi-Fi through the emulator's user networking
(off the PC's local network unless **Settings ▸ Attach to Local Network** is on), sound through DirectSound, and
the iOS 5 Setup flow's offline start (**Internet** in the device's toolbar opens the network once Setup is done).

Not yet: preparing devices from IPSWs, app (.ipa) installs and the other USB services (the Mac app's usbmuxd and
libimobiledevice helper), media import, the web proxy that lets the old devices' TLS reach today's HTTPS, the
host GL bridge (guests fall back to their own software renderer), the 3D bezels, and recording.

## Building from source

The Windows build cross-compiles from Linux; see [windows/README.md](windows/README.md).

```sh
windows/build.sh            # the emulator (in a Fedora mingw-w64 container) and the app, into windows/dist/
```

CI (`.github/workflows/windows.yml`) runs the same steps on every change under `windows/` and attaches the zip to a
release for each `v*` tag.

The rest of the repository is the upstream Mac app, kept as it is so the fork can follow upstream; its build
and tests are in [CONTRIBUTING.md](CONTRIBUTING.md) and need a Mac with Xcode.

## License and credits

Light Touch is free software under the GNU General Public License, version 2 or (at your option) any
later version; see [LICENSE](LICENSE). The Windows build is too.

It stands on a lot of other work:

- [Light Touch](https://github.com/samhenrigold/LightTouchMac) by Sam Henri Gold, of which this is a fork.
- [qemu-ios](https://github.com/samhenrigold/qemu-ios), the emulator, built on [QEMU](https://www.qemu.org)
  and grown out of [devos50's qemu-ios](https://github.com/devos50/qemu-ios) (GPL-2.0). The Windows build applies
  `windows/patches/qemu-ios/` to the pinned commit.
- FFmpeg, GLib, libslirp, PCRE2, pixman, OpenSSL and zlib, as built by Fedora's mingw-w64 packages or from the
  pinned sources; the Mac app also uses libimobiledevice, libusbmuxd, libplist, libtatsu, a
  [usbmuxd fork](https://github.com/samhenrigold/usbmuxd), iBoot32Patcher and Unrar.swift with RARLAB's UnRAR.

The Windows zip carries each bundled component's license, and where to get its source, under `licenses\`.

iPhone, iPod touch and iPad are trademarks of Apple Inc. Light Touch is not affiliated with Apple.
