#!/bin/bash
# Cross-builds what media import needs on Windows x64 with mingw-w64:
#
#   lzfse.dll    Apple's LZFSE (github.com/lzfse/lzfse at the pin below), which Light Touch for Mac's Guest/guest.aar is
#                compressed with: the import unpacks the guest's media helpers (itmedia, itphoto) from it.
#   ffmpeg.exe   FFmpeg, from the same release build-support/dependencies.json pins for the emulator, cut down to what
#   ffprobe.exe  preparing songs and videos takes: reading tags, durations and cover art, remuxing ADTS AAC into M4A,
#                and converting videos to the iPod's H.264/AAC M4V with Windows' own H.264 encoder (Media Foundation,
#                h264_mf), where the Mac converts with AVFoundation's device presets; and encoding screen recordings.
#
#     windows/scripts/build-media.sh OUT_DIR
#
# Runs where build-qemu.sh does. WORK_DIR (default OUT_DIR/../work) keeps the downloads and build trees.
set -euo pipefail

OUT="$(mkdir -p "$1" && cd "$1" && pwd)"
SRC="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="${WORK_DIR:-$OUT/../work}"
mkdir -p "$WORK/downloads"
WORK="$(cd "$WORK" && pwd)"
JOBS="${JOBS:-$(nproc)}"
HOST=x86_64-w64-mingw32

LZFSE_REPO=https://github.com/lzfse/lzfse
LZFSE_COMMIT=88e2d2788b4021d0b2eb9fe2d97352ae9190f128 # tag lzfse-1.0

package() { jq -r --arg n "$1" ".packages[] | select(.name == \$n) | .$2" "$SRC/build-support/dependencies.json"; }

# MARK: - lzfse.dll

LZFSE="$WORK/lzfse"
if [ "$(git -C "$LZFSE" rev-parse HEAD 2>/dev/null)" != "$LZFSE_COMMIT" ]; then
    rm -rf "$LZFSE"
    git init -q "$LZFSE"
    git -C "$LZFSE" fetch -q --depth 1 "$LZFSE_REPO" "$LZFSE_COMMIT"
    git -C "$LZFSE" checkout -q FETCH_HEAD
fi
"$HOST-gcc" -O2 -shared -o "$OUT/lzfse.dll" -Wl,--out-implib,"$WORK/liblzfse.dll.a" \
    "$LZFSE"/src/lzfse_{decode,decode_base,encode,encode_base,fse}.c "$LZFSE"/src/lzvn_{decode,encode}_base.c \
    -static-libgcc

# MARK: - ffmpeg.exe and ffprobe.exe

FFMPEG_VERSION="$(package ffmpeg version)"
FFMPEG_TARBALL="$WORK/downloads/$(package ffmpeg archive)"
if [ ! -f "$FFMPEG_TARBALL" ]; then
    curl -sSfL -o "$FFMPEG_TARBALL.part" "$(package ffmpeg url)"
    mv "$FFMPEG_TARBALL.part" "$FFMPEG_TARBALL"
fi
echo "$(package ffmpeg sha256)  $FFMPEG_TARBALL" | sha256sum -c --quiet
FFMPEG="$WORK/ffmpeg-programs"
STAMP="$FFMPEG/.ltw-configured"
CONFIGURE=(
    --enable-cross-compile --arch=x86_64 --target-os=mingw32 --cross-prefix="$HOST-"
    --disable-everything --disable-autodetect --disable-doc --disable-ffplay --disable-network
    # h264_mf takes D3D11 frames too, so it needs d3d11va.
    --enable-static --disable-shared --enable-w32threads --enable-mediafoundation --enable-d3d11va
    --enable-protocol=file,pipe
    # What MediaSong, MediaVideo and MediaPhoto accept: MP3, M4A, AAC, WAV; MP4, M4V, MOV; HEIC (the rest of the photos
    # System.Drawing reads itself).
    --enable-demuxer=mov,mp3,aac,wav,image2
    # Screen recording: the device window's frames (BGRA) and the mixer's PCM, piped in.
    --enable-demuxer=rawvideo,pcm_s16le --enable-decoder=rawvideo
    --enable-decoder=h264,hevc,mpeg4,h263,mjpeg,aac,aac_latm,mp3float,mp3,alac
    --enable-decoder=pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_f32le,pcm_f64le,pcm_u8
    --enable-parser=h264,hevc,mpeg4video,aac,mpegaudio,mjpeg
    --enable-bsf=aac_adtstoasc,h264_metadata,extract_extradata
    --enable-encoder=h264_mf,aac,bmp,mjpeg
    --enable-muxer=ipod,mp4,mov,image2
    --enable-filter=buffer,buffersink,abuffer,abuffersink,format,aformat,scale,aresample,null,anull,fps,pad,setsar
    --enable-filter=transpose,hflip,vflip,rotate,crop,xstack,tile,trim,atrim,copy,acopy
)
if [ ! -f "$STAMP" ] || [ "$(cat "$STAMP")" != "$FFMPEG_VERSION ${CONFIGURE[*]}" ]; then
    rm -rf "$FFMPEG"
    mkdir -p "$FFMPEG"
    tar -xf "$FFMPEG_TARBALL" -C "$FFMPEG" --strip-components=1
    (cd "$FFMPEG" && ./configure "${CONFIGURE[@]}")
    echo "$FFMPEG_VERSION ${CONFIGURE[*]}" > "$STAMP"
fi
make -C "$FFMPEG" -j"$JOBS" ffmpeg.exe ffprobe.exe
cp "$FFMPEG/ffmpeg.exe" "$FFMPEG/ffprobe.exe" "$OUT/"
"$HOST-strip" "$OUT/ffmpeg.exe" "$OUT/ffprobe.exe" "$OUT/lzfse.dll"

# MARK: - Licenses

mkdir -p "$OUT/licenses/lzfse" "$OUT/licenses/ffmpeg"
cp "$LZFSE/LICENSE" "$OUT/licenses/lzfse/"
cp "$FFMPEG/COPYING.LGPLv2.1" "$OUT/licenses/ffmpeg/"
{
    echo "lzfse: $LZFSE_REPO at $LZFSE_COMMIT (lzfse-1.0)."
    echo "ffmpeg.exe and ffprobe.exe: FFmpeg $FFMPEG_VERSION ($(package ffmpeg url)), LGPL, configured with"
    echo "${CONFIGURE[*]}"
} > "$OUT/licenses/MEDIA-SOURCES.txt"
echo "built $OUT/lzfse.dll, ffmpeg.exe and ffprobe.exe"
