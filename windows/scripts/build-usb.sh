#!/bin/bash
# Cross-builds the USB half of Light Touch for Windows x64 with mingw-w64: libimobiledevice and its libraries at the
# versions build-support/dependencies.json pins (the Mac build's), with the Mac's patch for iPhone OS 1's SSLv3 lockdown
# over an OpenSSL built enable-ssl3, and the usbmuxd fork build-support/sources.json pins (its QEMU backend carries the
# emulated USB device to libimobiledevice) with windows/patches/usbmuxd/.
#
#     windows/scripts/build-usb.sh OUT_DIR
#
# Runs where build-qemu.sh does (Fedora with its mingw64 packages, plus mingw64-curl, mingw64-libusb1 and autotools).
# WORK_DIR (default OUT_DIR/../work) keeps the downloads and build trees. OUT_DIR gets usbmuxd.exe, the libraries' DLLs
# (libimobiledevice-1.0.dll and libplist-2.0.dll are what the app calls) and licenses/.
set -euo pipefail

OUT="$(mkdir -p "$1" && cd "$1" && pwd)"
SRC="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${WORK_DIR:-$OUT/../work}"
mkdir -p "$WORK/downloads"
WORK="$(cd "$WORK" && pwd)"
JOBS="${JOBS:-$(nproc)}"
HOST=x86_64-w64-mingw32
SYSROOT="/usr/$HOST/sys-root/mingw"
# Fedora's mingw pkg-config prefixes every path with the sysroot, so the libraries install there (as FFmpeg does).
PREFIX="$SYSROOT"
STATIC="$WORK/openssl-static"

package() { jq -r --arg n "$1" ".packages[] | select(.name == \$n) | .$2" "$SRC/build-support/dependencies.json"; }

# The pinned archive of `name`, checked against its sha256, unpacked fresh into WORK; prints the source directory.
fetch() {
    local name="$1" url sha archive directory
    url="$(package "$name" url)"
    sha="$(package "$name" sha256)"
    archive="$WORK/downloads/$(package "$name" archive)"
    directory="$(package "$name" directory)"
    if [ ! -f "$archive" ]; then
        curl -sSfL -o "$archive.part" "$url"
        mv "$archive.part" "$archive"
    fi
    echo "$sha  $archive" | sha256sum -c --quiet >&2
    rm -rf "${WORK:?}/$directory"
    tar -xf "$archive" -C "$WORK"
    echo "$WORK/$directory"
}

# An autotools library, shared, into the sysroot; skipped when its pkg-config file is there.
autolib() {
    local name="$1" pc="$2"
    shift 2
    if [ -f "$PREFIX/lib/pkgconfig/$pc.pc" ]; then return; fi
    local dir
    dir="$(fetch "$name")"
    (
        cd "$dir"
        for p in ${PATCHES:-}; do patch -p1 < "$p"; done
        ./configure --host="$HOST" --prefix="$PREFIX" --enable-shared --disable-static "$@"
        make -j"$JOBS"
        make install
    )
}

# The libraries name Windows import libraries as Windows spells them (-lIphlpapi); the sysroot's are lower case, and
# libtool, not finding the file, quietly builds a static library instead of the DLL.
CASE_LIBS="$WORK/case-libs"
mkdir -p "$CASE_LIBS"
ln -sf "$SYSROOT/lib/libiphlpapi.a" "$CASE_LIBS/libIphlpapi.a"
export LDFLAGS="-L$CASE_LIBS"

# MARK: - OpenSSL, static and with SSLv3 (iPhone OS 1.x lockdownd speaks nothing newer), as the Mac's static build

if [ ! -f "$STATIC/lib64/libssl.a" ] && [ ! -f "$STATIC/lib/libssl.a" ]; then
    dir="$(fetch openssl)"
    (
        cd "$dir"
        ./Configure mingw64 --cross-compile-prefix="$HOST-" no-shared no-tests no-docs no-module \
            enable-ssl3 enable-ssl3-method --prefix="$STATIC" --openssldir=C:/ProgramData/LightTouch/ssl
        make -j"$JOBS"
        make install_sw
    )
fi
OPENSSL_LIB="$STATIC/lib64"
[ -d "$OPENSSL_LIB" ] || OPENSSL_LIB="$STATIC/lib"
# libimobiledevice's configure takes OpenSSL from openssl_CFLAGS/openssl_LIBS (its PKG_CHECK_MODULES overrides): by
# path, since -lssl would find the sysroot's import library (Fedora's OpenSSL DLL, built without SSLv3), and a .pc
# outside the sysroot can't name it (Fedora's pkg-config prefixes every path with the sysroot).
OPENSSL_CFLAGS="-I$STATIC/include"
OPENSSL_LIBS="$OPENSSL_LIB/libssl.a $OPENSSL_LIB/libcrypto.a -lws2_32 -lgdi32 -lcrypt32 -luser32 -ladvapi32"

# MARK: - libplist, libimobiledevice-glue, libusbmuxd, libtatsu, libimobiledevice

autolib libplist libplist-2.0 --without-cython
autolib libimobiledevice-glue libimobiledevice-glue-1.0
autolib libusbmuxd libusbmuxd-2.0
autolib libtatsu libtatsu-1.0
PATCHES="$SRC/build-support/patches/libimobiledevice-sslv3-ios1.patch" \
    openssl_CFLAGS="$OPENSSL_CFLAGS" openssl_LIBS="$OPENSSL_LIBS" \
    autolib libimobiledevice libimobiledevice-1.0 --without-cython --enable-debug

# MARK: - usbmuxd: the fork's QEMU backend, with a Windows main (windows/usbmuxd/main-win32.c) and the Winsock compat

pin() { jq -r ".usbmuxd.$1" "$SRC/build-support/sources.json"; }
MUX="${USBMUXD_SOURCE_DIR:-$WORK/usbmuxd}"
if [ ! -d "$MUX/.git" ]; then
    git init -q "$MUX"
    git -C "$MUX" remote add origin "$(pin repository)"
fi
if [ "$(git -C "$MUX" rev-parse HEAD 2>/dev/null || true)" != "$(pin commit)" ]; then
    git -C "$MUX" fetch -q --depth 1 origin "$(pin commit)"
fi
git -C "$MUX" checkout -q --force "$(pin commit)"
git -C "$MUX" clean -qfd
for p in "$SRC"/windows/patches/usbmuxd/*.patch; do git -C "$MUX" apply "$p"; done
MUX_BUILD="$WORK/usbmuxd-build"
mkdir -p "$MUX_BUILD"
{
    echo '#define PACKAGE_NAME "usbmuxd"'
    echo "#define PACKAGE_STRING \"usbmuxd $(pin commit | cut -c1-10)\""
    echo "#define PACKAGE_VERSION \"$(pin commit | cut -c1-10)\""
    echo '#define HAVE_LIBIMOBILEDEVICE 1'
    echo '#define HAVE_ENUM_IDEVICE_CONNECTION_TYPE 1'
    echo '#define HAVE_CLOCK_GETTIME 1'
    echo '#define HAVE_SYS_TYPES_H 1'
    echo '#define WIN32 1'
} > "$MUX_BUILD/config.h"
LIBS_PC="libplist-2.0 libimobiledevice-glue-1.0 libimobiledevice-1.0"
# shellcheck disable=SC2046
"$HOST-gcc" -O2 -std=gnu11 -D_WIN32_WINNT=0x0A00 -DHAVE_CONFIG_H -I"$MUX_BUILD" -I"$SRC/windows/usbmuxd/include" \
    -include win32-compat.h -I"$MUX/src" $("$HOST-pkg-config" --cflags $LIBS_PC) \
    -Wno-incompatible-pointer-types -Wno-int-conversion \
    "$MUX/src/client.c" "$MUX/src/device.c" "$MUX/src/preflight.c" "$MUX/src/log.c" "$MUX/src/usb-qemu.c" \
    "$MUX/src/utils.c" "$MUX/src/conf.c" "$SRC/windows/usbmuxd/main-win32.c" \
    -o "$MUX_BUILD/usbmuxd.exe" $("$HOST-pkg-config" --libs $LIBS_PC) -lws2_32 -lshell32 -lole32 -lpthread

# MARK: - Stage

cp "$MUX_BUILD/usbmuxd.exe" "$SYSROOT/bin/libimobiledevice-1.0.dll" "$SYSROOT/bin/libplist-2.0.dll" "$OUT/"
# The DLL closure of the daemon and of the libraries the app calls.
stage_closure() {
    local queue=("$@") seen=" " dll found
    while [ ${#queue[@]} -gt 0 ]; do
        local next="${queue[0]}"
        queue=("${queue[@]:1}")
        for dll in $("$HOST-objdump" -p "$next" | awk '/DLL Name:/ { print $3 }'); do
            case "$seen" in *" ${dll,,} "*) continue ;; esac
            seen+="${dll,,} "
            found="$SYSROOT/bin/$dll"
            [ -f "$found" ] || continue
            cp "$found" "$OUT/"
            queue+=("$found")
        done
    done
}
stage_closure "$OUT/usbmuxd.exe" "$OUT/libimobiledevice-1.0.dll" "$OUT/libplist-2.0.dll"
"$HOST-strip" --strip-unneeded "$OUT"/*.dll "$OUT"/*.exe

for name in libplist libimobiledevice-glue libusbmuxd libtatsu libimobiledevice openssl; do
    dir="$WORK/$(package "$name" directory)"
    mkdir -p "$OUT/licenses/$name"
    cp "$dir"/COPYING* "$dir"/LICENSE* "$OUT/licenses/$name/" 2>/dev/null || true
    echo "$name $(package "$name" version): $(package "$name" url) (sha256 $(package "$name" sha256))" \
        > "$OUT/licenses/$name/SOURCE.txt"
done
cp "$SRC/build-support/patches/libimobiledevice-sslv3-ios1.patch" "$OUT/licenses/libimobiledevice/"
mkdir -p "$OUT/licenses/usbmuxd"
cp "$MUX"/COPYING* "$OUT/licenses/usbmuxd/" 2>/dev/null || true
cp "$SRC"/windows/patches/usbmuxd/*.patch "$SRC/windows/usbmuxd/main-win32.c" "$OUT/licenses/usbmuxd/"
echo "usbmuxd: $(pin repository) at $(pin commit), with the patches and main-win32.c beside this file" \
    > "$OUT/licenses/usbmuxd/SOURCE.txt"
echo "built $OUT/usbmuxd.exe"
