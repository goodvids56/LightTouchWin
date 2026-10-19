# The Windows build

Light Touch for Windows is two pieces, both built from Linux:

| Piece | What it is | Built by |
|---|---|---|
| `qemu-arm.dll` | The emulator: qemu-ios at the commit `build-support/sources.json` pins, with `patches/qemu-ios/`, relinked as a DLL that exports the `qemu_ios_*` ABI (the Mac's `libqemu-arm.dylib`) | `scripts/build-qemu.sh`, with mingw-w64 on Fedora (`Dockerfile`) |
| `LightTouch.exe` | The app (C#, .NET 8, WinForms): the device library and, as `LightTouch.exe run DEVICE_DIR`, one running device, as the Mac's `LightTouchDevice` helper is | `dotnet publish LightTouch` |

```sh
windows/build.sh              # both, then dist/LightTouch-windows-x64.zip
windows/build.sh --emulator   # only the emulator, into work/emulator (docker unless on Fedora with mingw64)
windows/build.sh --app        # the app's tests, its publish and the zip, over work/emulator
```

`DOTNET` must be Microsoft's .NET 8 SDK (`dotnet-install.sh` or `actions/setup-dotnet`): distribution builds of
.NET lack the Windows Desktop targets. The emulator build fetches qemu-ios, FFmpeg 9.0.1 and libslirp 4.9.4
(the Mac build's versions, with qemu-ios's own patches to them); `QEMU_IOS_DIR`, `FFMPEG_TARBALL` and `SLIRP_TARBALL`
supply them instead, and `work/emulator-build` keeps the trees so a rerun rebuilds only what changed.

## Layout

| Path | What |
|---|---|
| `scripts/build-qemu.sh` | FFmpeg and libslirp into the mingw sysroot, QEMU configured as the Mac build is (DirectSound for CoreAudio, no Cocoa), then ninja's own link line with `qemu-ios-entry.c` for `main.c` as `qemu-arm.dll`, and its DLL closure |
| `patches/qemu-ios/` | What qemu-ios needs to build and run on Windows: `memmem`, a Win32 mapping for packed FMSS images, page files replacing their older selves, socket I/O through `recv`/`send`, a same-file check by file index (Windows' `stat` has no inode), no directory `fsync`, the NITZ zone offset from GLib, process CPU time and the build ID from the PE header |
| `LightTouch/Native/` | `Qemu.cs` (the ABI by P/Invoke), `ChildProcess.cs` (a device process with its stdout and stderr in its `emulator.log`) |
| `LightTouch/Device/` | Ports of HostRuntime: `Board`, `DeviceLock`, `BootRecipe` and `PreparedDevice` (argv for a prepared base, as the Mac boots it), `UnitIdentity` and `N72Nor` (a unit identity from a seed), `SparseFile` (the NAND chip files as NTFS sparse files, where the Mac clones them on APFS) |
| `LightTouch/Library/` | `DeviceLibrary` (`%LOCALAPPDATA%\LightTouch`), `PackedBase` (the built-in iPod's `.itbase`, both the directory format and the older single stream, and its reseed) |
| `LightTouch/UI/` | `LibraryForm`, `DeviceForm` (frames from `qemu_ios_ui_frame`, mouse as touch, keys as macOS keycodes through `qemu_ios_ui_key_mac`, the pause-then-quit stop), `MacKeys` |
| `LightTouch.Tests/` | xunit, on any OS, over the sources above but the windows: identities against FirmwareKit's recorded values, both packed formats and the reseed, and the boot argv against HostRuntime's `BootTests` |

## Notes

- The C runtime opens files in text mode unless told otherwise, and qemu-ios (written for POSIX) never says: a GID
  blob file read in text mode stops at its first `0x1A`. The device process sets `_set_fmode(_O_BINARY)` before
  QEMU starts.
- msvcrt keeps its own copy of the environment, so the boot's variables (`IT_TVOUT_READY`) go through `_putenv_s`.
- QEMU exits at startup when DirectSound has no output device, so a PC without one boots silent.
- `LTW_TRACE_INPUT=1` logs every touch to `emulator.log`.
- Under Wine (for checking a build on Linux) a window manager is needed for timely mouse input; the app and the
  built-in iPod run there with `wine LightTouch.exe`.
