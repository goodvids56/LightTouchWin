# The Windows cross-build environment (windows/scripts/build-qemu.sh, build-usb.sh and build-media.sh): Fedora's
# mingw-w64 toolchain and libraries. windows/build.sh builds and runs it; CI runs the same packages in a fedora:42 container.
FROM quay.io/fedora/fedora:42
RUN dnf install -y \
        mingw64-gcc mingw64-gcc-c++ mingw64-glib2 mingw64-pixman mingw64-zlib mingw64-openssl mingw64-pkg-config \
        mingw64-curl autoconf automake libtool which perl-FindBin perl-IPC-Cmd perl-File-Compare perl-File-Copy \
        perl-Time-Piece perl-lib \
        meson ninja-build python3 git curl jq bzip2 xz patch flex bison diffutils findutils make nasm gcc \
    && dnf clean all
RUN git config --global --add safe.directory '*'
