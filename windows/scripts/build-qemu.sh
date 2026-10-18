#!/bin/bash
# Cross-builds the emulator for Windows x64 from Linux with mingw-w64: qemu-ios at the commit pinned in
# build-support/sources.json, against the same patched FFmpeg the Mac build uses, then relinks the objects as
# qemu-arm.dll exporting the qemu_ios_* ABI (the Windows twin of qemu-ios's contrib/macos-app/make-dylib-macos.sh).
#
#     windows/scripts/build-qemu.sh OUT_DIR
#
# Runs on Fedora with its mingw64 packages (windows/Dockerfile; windows/build.sh runs it there). Inputs come from
# the network unless already present: QEMU_IOS_DIR (a qemu-ios checkout at the pin), FFMPEG_TARBALL and SLIRP_TARBALL. WORK_DIR
# (default OUT_DIR/../work) keeps the build trees, so a rerun only rebuilds what changed.
#
# OUT_DIR gets qemu-arm.dll, qemu-system-arm.exe, every DLL either needs from the mingw sysroot, and licenses/.
set -euo pipefail

OUT="$(mkdir -p "$1" && cd "$1" && pwd)"
SRC="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${WORK_DIR:-$OUT/../work}"
mkdir -p "$WORK"
WORK="$(cd "$WORK" && pwd)"
JOBS="${JOBS:-$(nproc)}"
HOST=x86_64-w64-mingw32
SYSROOT="/usr/$HOST/sys-root/mingw"
FFMPEG_VERSION=9.0.1
FFMPEG_SHA256=cf38e0e28c7e5605942c4a77755349b0145804a397af37eb1fb4c77cb237f635
SLIRP_VERSION=4.9.4
SLIRP_SHA256=3998863b020aeda34bddc567097c6efba55a78cdf6eeee6bcd42c11ef23967da

pin() { jq -r ".\"qemu-ios\".$1" "$SRC/build-support/sources.json"; }
QEMU_COMMIT="$(pin commit)"
QEMU_REPO="$(pin repository)"

# MARK: - Sources

QEMU="${QEMU_IOS_DIR:-$WORK/qemu-ios}"
if [ ! -d "$QEMU/.git" ]; then
    git init -q "$QEMU"
    git -C "$QEMU" remote add origin "$QEMU_REPO"
fi
if [ "$(git -C "$QEMU" rev-parse HEAD 2>/dev/null || true)" != "$QEMU_COMMIT" ]; then
    git -C "$QEMU" fetch -q --depth 1 origin "$QEMU_COMMIT"
    git -C "$QEMU" checkout -q --force "$QEMU_COMMIT"
    git -C "$QEMU" clean -qfdx
fi
# The Windows patches go on a clean tree, so a changed patch never lands on its older self; an unchanged set is left
# applied, so the tree's mtimes (and the build) stay as they are.
shopt -s nullglob
PATCHES=("$SRC"/windows/patches/qemu-ios/*.patch)
shopt -u nullglob
STAMP="$(cat ${PATCHES[@]+"${PATCHES[@]}"} | sha256sum | cut -c1-64) $QEMU_COMMIT"
if [ "$(cat "$QEMU/.git/ltm-windows-patches" 2>/dev/null || true)" != "$STAMP" ]; then
    git -C "$QEMU" reset -q --hard "$QEMU_COMMIT"
    git -C "$QEMU" clean -qfd
    for p in ${PATCHES[@]+"${PATCHES[@]}"}; do
        git -C "$QEMU" apply --whitespace=nowarn "$p"
    done
    echo "$STAMP" > "$QEMU/.git/ltm-windows-patches"
fi
echo "qemu-ios $QEMU_COMMIT with ${#PATCHES[@]} Windows patches"

FFMPEG_TARBALL="${FFMPEG_TARBALL:-$WORK/ffmpeg-$FFMPEG_VERSION.tar.xz}"
if [ ! -f "$FFMPEG_TARBALL" ]; then
    curl -sSfL -o "$FFMPEG_TARBALL.part" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
    mv "$FFMPEG_TARBALL.part" "$FFMPEG_TARBALL"
fi
echo "$FFMPEG_SHA256  $FFMPEG_TARBALL" | sha256sum -c --quiet

# MARK: - FFmpeg (the AMC audio and the H.264 slice bridge), as scripts/build-package-native.sh configures it

# FFmpeg and libslirp install into the mingw sysroot: Fedora's mingw pkg-config prefixes every path it reports with
# the sysroot, so a prefix outside it would not be found.
PREFIX="$SYSROOT"
if [ ! -f "$PREFIX/lib/pkgconfig/libavcodec.pc" ]; then
    rm -rf "$WORK/ffmpeg-$FFMPEG_VERSION"
    tar -xf "$FFMPEG_TARBALL" -C "$WORK"
    (
        cd "$WORK/ffmpeg-$FFMPEG_VERSION"
        patch -p1 < "$QEMU/contrib/ffmpeg/h264-chunk-er.patch"
        patch -p1 < "$QEMU/contrib/ffmpeg/h264-cavlc-pcm-offset.patch"
        ./configure --prefix="$PREFIX" --enable-cross-compile --arch=x86_64 --target-os=mingw32 \
            --cross-prefix="$HOST-" --pkg-config="$HOST-pkg-config" \
            --disable-everything --disable-autodetect --disable-programs --disable-doc \
            --disable-avdevice --disable-avformat --disable-avfilter --disable-swscale --disable-swresample \
            --enable-decoder=aac,mp3,alac,h264 --enable-shared --disable-static
        make -j"$JOBS"
        make install
    )
fi

# MARK: - libslirp, with qemu-ios's restrict and local-network patch (static, as on the Mac)

if [ ! -f "$PREFIX/lib/pkgconfig/slirp.pc" ]; then
    SLIRP_TARBALL="${SLIRP_TARBALL:-$WORK/libslirp-v$SLIRP_VERSION.tar.gz}"
    if [ ! -f "$SLIRP_TARBALL" ]; then
        curl -sSfL -o "$SLIRP_TARBALL.part" \
            "https://gitlab.freedesktop.org/slirp/libslirp/-/archive/v$SLIRP_VERSION/libslirp-v$SLIRP_VERSION.tar.gz"
        mv "$SLIRP_TARBALL.part" "$SLIRP_TARBALL"
    fi
    echo "$SLIRP_SHA256  $SLIRP_TARBALL" | sha256sum -c --quiet
    rm -rf "$WORK/libslirp-v$SLIRP_VERSION" "$WORK/slirp-build"
    tar -xf "$SLIRP_TARBALL" -C "$WORK"
    (cd "$WORK/libslirp-v$SLIRP_VERSION" && patch -p1 < "$QEMU/subprojects/packagefiles/libslirp-set-restricted.patch")
    meson setup "$WORK/slirp-build" "$WORK/libslirp-v$SLIRP_VERSION" --cross-file /usr/share/mingw/toolchain-mingw64.meson \
        --prefix="$PREFIX" --libdir=lib --buildtype=release -Ddefault_library=static --wrap-mode=nofallback
    ninja -C "$WORK/slirp-build" -j"$JOBS" install
fi

# MARK: - QEMU

BUILD="$WORK/qemu-build"
if [ ! -f "$BUILD/build.ninja" ]; then
    mkdir -p "$BUILD"
    (
        cd "$BUILD"
        # What the Mac build enables, with DirectSound for CoreAudio and no Cocoa UI (the app draws the frames).
        # No host GL bridge yet: gles-host-stub.c, so guests use their software renderer.
        "$QEMU/configure" --cross-prefix="$HOST-" --target-list=arm-softmmu --without-default-features \
            --enable-pixman --enable-slirp --enable-dsound --disable-werror \
            --extra-cflags="-DLIBSLIRP_STATIC -fmacro-prefix-map=$QEMU/= -fmacro-prefix-map=$BUILD/=" \
            --extra-ldflags="-lcrypto"
    )
fi
# keeprsp: the link's response file is the DLL's link line below.
rm -f "$BUILD/qemu-system-arm.exe"
ninja -C "$BUILD" -j"$JOBS" -d keeprsp qemu-system-arm.exe

# MARK: - qemu-arm.dll

# The app-facing pieces, compiled with the emulator's own include set; qemu-macos-extras.c is the keyboard, pinch and
# machine-control half of the ABI and has nothing Mac-only in it.
cd "$BUILD"
DEP_CFLAGS="$($HOST-pkg-config --cflags glib-2.0 pixman-1)"
for f in "$QEMU/contrib/ios-app/qemu-ios-entry.c" \
    "$QEMU/contrib/ios-app/qemu-ios-ui.c" \
    "$QEMU/contrib/macos-app/qemu-macos-extras.c"; do
    "$HOST-gcc" -c "$f" -o "$(basename "${f%.c}").o" \
        -I. -I.. -Iqapi -Itrace -Iui \
        -I"$QEMU/contrib/ios-app" -I"$QEMU/contrib/macos-app" \
        $DEP_CFLAGS \
        -iquote . -iquote "$QEMU" -iquote "$QEMU/include" \
        -iquote "$QEMU/host/include/x86_64" -iquote "$QEMU/host/include/generic" \
        -iquote "$QEMU/tcg/i386" \
        -D_GNU_SOURCE -D_FILE_OFFSET_BITS=64 -D_LARGEFILE_SOURCE -std=gnu11 -O2 -mms-bitfields \
        -fmacro-prefix-map="$QEMU/=" -fmacro-prefix-map="$BUILD/="
done

# Every qemu_ios_* entry point the three files define is the ABI.
{
    echo "LIBRARY qemu-arm.dll"
    echo "EXPORTS"
    "$HOST-nm" -g --defined-only qemu-ios-entry.o qemu-ios-ui.o qemu-macos-extras.o \
        | awk '$2 == "T" && $3 ~ /^qemu_ios_/ { print "    " $3 }' | sort -u
    # Defined in the board code rather than the three files above.
    for sym in qemu_ios_agent_request qemu_ios_agent_cancel qemu_ios_agent_result qemu_ios_agent_free_result \
        qemu_ios_agent_status qemu_ios_gles_contexts qemu_ios_guest_package_report qemu_ios_gles_protocol \
        qemu_ios_build_id qemu_ios_api_version; do
        echo "    $sym"
    done
} | awk '!seen[$0]++' > qemu-arm.def

# ninja's own link line, so this cannot drift from the real build: system/main.c's main() becomes qemu_ios_main().
eval "LINK=($(ninja -t commands qemu-system-arm.exe | tail -1))"
# meson links through @qemu-system-arm.exe.rsp; its words go inline so main.c's object can be swapped.
EXPANDED=()
for a in "${LINK[@]}"; do
    case "$a" in
        @*) eval "EXPANDED+=($(tr '\n' ' ' < "${a#@}"))" ;;
        *) EXPANDED+=("$a") ;;
    esac
done
LINK=("${EXPANDED[@]}")
ARGS=()
skip=0
for a in "${LINK[@]}"; do
    if [ $skip = 1 ]; then
        skip=0
        continue
    fi
    case "$a" in
        -o) skip=1 ;;
        *system_main.c.o*) ARGS+=(qemu-ios-entry.o qemu-ios-ui.o qemu-macos-extras.o) ;;
        # the console-subsystem and executable-only flags
        -Wl,--subsystem,* | -mwindows | -mconsole) ;;
        *) ARGS+=("$a") ;;
    esac
done
ARGS+=(-shared -o qemu-arm.dll qemu-arm.def -Wl,--out-implib,libqemu-arm.dll.a)
"${ARGS[@]}"

# MARK: - Stage

cp qemu-arm.dll qemu-system-arm.exe "$OUT/"
# The DLL closure: every import that resolves inside the mingw sysroot, transitively.
stage_closure() {
    local queue=("$@") seen=" " dll found dir
    while [ ${#queue[@]} -gt 0 ]; do
        local next="${queue[0]}"
        queue=("${queue[@]:1}")
        for dll in $("$HOST-objdump" -p "$next" | awk '/DLL Name:/ { print $3 }'); do
            case "$seen" in *" ${dll,,} "*) continue ;; esac
            seen+="${dll,,} "
            found=""
            for dir in "$SYSROOT/bin"; do
                if [ -f "$dir/$dll" ]; then found="$dir/$dll"; break; fi
            done
            [ -n "$found" ] || continue
            cp "$found" "$OUT/"
            queue+=("$found")
        done
    done
}
stage_closure "$OUT/qemu-arm.dll" "$OUT/qemu-system-arm.exe"
"$HOST-strip" --strip-unneeded "$OUT"/*.dll "$OUT"/*.exe

mkdir -p "$OUT/licenses/qemu-ios" "$OUT/licenses/ffmpeg"
cp "$QEMU/COPYING" "$QEMU/LICENSE" "$OUT/licenses/qemu-ios/"
cp "$WORK/ffmpeg-$FFMPEG_VERSION/COPYING.LGPLv2.1" "$OUT/licenses/ffmpeg/" 2>/dev/null || true
cp ${PATCHES[@]+"${PATCHES[@]}"} "$OUT/licenses/qemu-ios/"
{
    echo "qemu-ios: $QEMU_REPO at $QEMU_COMMIT, with the patches beside this file."
    echo "FFmpeg $FFMPEG_VERSION: https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz (sha256 $FFMPEG_SHA256),"
    echo "with qemu-ios's contrib/ffmpeg/h264-chunk-er.patch and h264-cavlc-pcm-offset.patch."
    echo "The mingw-w64 runtime DLLs (GLib, pixman, OpenSSL, zlib, ...) are Fedora's mingw64 packages."
} > "$OUT/licenses/SOURCES.txt"
echo "built $OUT/qemu-arm.dll ($(grep -c '^    ' "$BUILD/qemu-arm.def") exports)"
