#!/bin/bash
# Builds Light Touch for Windows (x64) from Linux: the emulator, the USB services and the media tools cross-compiled with
# mingw-w64 (scripts/build-qemu.sh, build-usb.sh and build-media.sh, in the windows/Dockerfile image unless this already
# runs on Fedora with its mingw64 packages), the app published with the .NET 8 SDK, and both put together as dist/LightTouch-windows-x64.zip.
#
#     windows/build.sh                 # everything
#     windows/build.sh --emulator      # only the emulator, into work/emulator
#     windows/build.sh --app           # only the app and the zip (the emulator already built)
#
# DOTNET (default: dotnet on PATH) must be Microsoft's SDK, which carries the Windows Desktop targets; distribution
# builds of .NET (Ubuntu's, Fedora's) don't. JOBS, QEMU_IOS_DIR, FFMPEG_TARBALL and SLIRP_TARBALL pass through.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$(cd "$HERE/.." && pwd)"
WORK="$HERE/work"
DIST="$HERE/dist"
NAME=LightTouch-windows-x64
DOTNET="${DOTNET:-dotnet}"
emulator=1
app=1
case "${1:-}" in
    --emulator) app=0 ;;
    --app) emulator=0 ;;
    "") ;;
    *) echo "usage: $0 [--emulator | --app]" >&2; exit 64 ;;
esac

if [ $emulator = 1 ]; then
    if command -v x86_64-w64-mingw32-gcc >/dev/null && [ -d /usr/x86_64-w64-mingw32/sys-root/mingw ]; then
        WORK_DIR="$WORK/emulator-build" "$SRC/windows/scripts/build-qemu.sh" "$WORK/emulator"
        WORK_DIR="$WORK/usb-build" "$SRC/windows/scripts/build-usb.sh" "$WORK/emulator"
        WORK_DIR="$WORK/media-build" "$SRC/windows/scripts/build-media.sh" "$WORK/emulator"
    else
        docker build -t lighttouch-windows-build "$HERE"
        docker run --rm --network host -u "$(id -u):$(id -g)" -e HOME=/tmp -e JOBS \
            -v "$SRC:/src" -w /src lighttouch-windows-build \
            bash -c 'WORK_DIR=/src/windows/work/emulator-build windows/scripts/build-qemu.sh /src/windows/work/emulator &&
                WORK_DIR=/src/windows/work/usb-build windows/scripts/build-usb.sh /src/windows/work/emulator &&
                WORK_DIR=/src/windows/work/media-build windows/scripts/build-media.sh /src/windows/work/emulator'
    fi
fi

if [ $app = 1 ]; then
    [ -f "$WORK/emulator/qemu-arm.dll" ] || { echo "no emulator in $WORK/emulator: run $0 --emulator first" >&2; exit 1; }
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    "$DOTNET" test "$HERE/LightTouch.Tests/LightTouch.Tests.csproj"
    rm -rf "$WORK/app" "$DIST/$NAME"
    "$DOTNET" publish "$HERE/LightTouch/LightTouch.csproj" -c Release -o "$WORK/app"
    mkdir -p "$DIST/$NAME"
    cp "$WORK/app/LightTouch.exe" "$DIST/$NAME/"
    # The app loads qemu-arm.dll; the standalone qemu-system-arm.exe stays in work/emulator for debugging.
    cp -r "$WORK/emulator/." "$DIST/$NAME/"
    rm -f "$DIST/$NAME/qemu-system-arm.exe"
    cp "$SRC/LICENSE" "$DIST/$NAME/LICENSE.txt"
    cp "$HERE/README-Windows.txt" "$DIST/$NAME/README.txt"
    (cd "$DIST" && rm -f "$NAME.zip" && zip -qr "$NAME.zip" "$NAME" && sha256sum "$NAME.zip" > "$NAME.zip.sha256")
    echo "built $DIST/$NAME.zip"
    cat "$DIST/$NAME.zip.sha256"
fi
