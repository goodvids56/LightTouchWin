/*
 * usbmuxd on Windows: the POSIX it uses, over Winsock. Force-included into every source (-include), so the system
 * headers that declare read, write, close, send, recv and accept are all in before the wrappers take those names.
 *
 * Winsock reports errors through WSAGetLastError(), never errno, and the daemon's loops test errno for EAGAIN and
 * EINTR; each wrapper sets errno from the WSA error. A socket is a SOCKET, kept in an int as on POSIX (Windows' handle
 * values fit). close/read/write work on sockets and fall back to the C runtime for files.
 */
#ifndef LTW_USBMUXD_WIN32_COMPAT_H
#define LTW_USBMUXD_WIN32_COMPAT_H

#include <winsock2.h>
#include <ws2tcpip.h>
#include <afunix.h>
#include <windows.h>
/* objbase.h's COM spelling; the daemon has a field named interface. */
#undef interface
#include <errno.h>
#include <stdarg.h>
#include <stdbool.h>
#include <fcntl.h>
#include <io.h>
#include <unistd.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <pthread_time.h>
#include <sys/time.h>
#include <sys/types.h>
#include <sys/stat.h>

#ifndef O_NONBLOCK
#define O_NONBLOCK 04000
#endif
#ifndef F_GETFL
#define F_GETFL 3
#define F_SETFL 4
#endif
#ifndef SIGPIPE
#define SIGPIPE 13
#endif

typedef unsigned long nfds_t;

static inline int ltw_set_errno(void)
{
    switch (WSAGetLastError()) {
    case WSAEWOULDBLOCK:
    case WSAETIMEDOUT:
    case WSAEINPROGRESS:
        errno = EAGAIN;
        break;
    case WSAEINTR:
        errno = EINTR;
        break;
    case WSAECONNRESET:
    case WSAECONNABORTED:
        errno = ECONNRESET;
        break;
    case WSAENOTCONN:
    case WSAESHUTDOWN:
        errno = EPIPE;
        break;
    case WSAEADDRINUSE:
        errno = EADDRINUSE;
        break;
    case WSAECONNREFUSED:
        errno = ECONNREFUSED;
        break;
    case WSAEMSGSIZE:
        errno = EMSGSIZE;
        break;
    default:
        errno = EIO;
        break;
    }
    return -1;
}

/* Winsock wants WSAStartup before any socket call; the daemon's main runs it, this keeps a stray first call safe. */
static inline void ltw_wsa_start(void)
{
    static int started;
    if (!started) {
        WSADATA data;
        WSAStartup(MAKEWORD(2, 2), &data);
        started = 1;
    }
}

static inline int ltw_socket(int domain, int type, int protocol)
{
    ltw_wsa_start();
    SOCKET s = socket(domain, type, protocol);
    if (s == INVALID_SOCKET)
        return ltw_set_errno();
    return (int)s;
}

static inline int ltw_accept(int fd, struct sockaddr *addr, socklen_t *len)
{
    SOCKET s = accept((SOCKET)fd, addr, len);
    if (s == INVALID_SOCKET)
        return ltw_set_errno();
    return (int)s;
}

static inline int ltw_bind(int fd, const struct sockaddr *addr, socklen_t len)
{
    return bind((SOCKET)fd, addr, len) == SOCKET_ERROR ? ltw_set_errno() : 0;
}

static inline int ltw_listen(int fd, int backlog)
{
    return listen((SOCKET)fd, backlog) == SOCKET_ERROR ? ltw_set_errno() : 0;
}

static inline int ltw_connect(int fd, const struct sockaddr *addr, socklen_t len)
{
    return connect((SOCKET)fd, addr, len) == SOCKET_ERROR ? ltw_set_errno() : 0;
}

static inline ssize_t ltw_recv(int fd, void *buf, size_t len, int flags)
{
    int r = recv((SOCKET)fd, (char *)buf, (int)len, flags);
    return r == SOCKET_ERROR ? ltw_set_errno() : r;
}

static inline ssize_t ltw_send(int fd, const void *buf, size_t len, int flags)
{
    int r = send((SOCKET)fd, (const char *)buf, (int)len, flags);
    return r == SOCKET_ERROR ? ltw_set_errno() : r;
}

static inline int ltw_setsockopt(int fd, int level, int name, const void *value, socklen_t len)
{
    return setsockopt((SOCKET)fd, level, name, (const char *)value, len) == SOCKET_ERROR ? ltw_set_errno() : 0;
}

static inline int ltw_getsockopt(int fd, int level, int name, void *value, socklen_t *len)
{
    return getsockopt((SOCKET)fd, level, name, (char *)value, len) == SOCKET_ERROR ? ltw_set_errno() : 0;
}

/* Sockets only take O_NONBLOCK; F_GETFL reports nothing set. */
static inline int ltw_fcntl(int fd, int cmd, ...)
{
    va_list ap;
    int arg = 0;
    va_start(ap, cmd);
    if (cmd == F_SETFL)
        arg = va_arg(ap, int);
    va_end(ap);
    if (cmd == F_GETFL)
        return 0;
    if (cmd == F_SETFL) {
        u_long nonblocking = (arg & O_NONBLOCK) ? 1 : 0;
        return ioctlsocket((SOCKET)fd, FIONBIO, &nonblocking) == SOCKET_ERROR ? ltw_set_errno() : 0;
    }
    errno = EINVAL;
    return -1;
}

static inline int ltw_close(int fd)
{
    if (closesocket((SOCKET)fd) == 0)
        return 0;
    if (WSAGetLastError() == WSAENOTSOCK)
        return _close(fd);
    return ltw_set_errno();
}

static inline ssize_t ltw_read(int fd, void *buf, size_t len)
{
    int r = recv((SOCKET)fd, (char *)buf, (int)len, 0);
    if (r != SOCKET_ERROR)
        return r;
    if (WSAGetLastError() == WSAENOTSOCK)
        return _read(fd, buf, (unsigned)len);
    return ltw_set_errno();
}

static inline ssize_t ltw_write(int fd, const void *buf, size_t len)
{
    int r = send((SOCKET)fd, (const char *)buf, (int)len, 0);
    if (r != SOCKET_ERROR)
        return r;
    if (WSAGetLastError() == WSAENOTSOCK)
        return _write(fd, buf, (unsigned)len);
    return ltw_set_errno();
}

static inline int ltw_poll(struct pollfd *fds, nfds_t count, int timeout)
{
    /* WSAPoll with no sockets fails instead of sleeping. */
    if (count == 0) {
        Sleep(timeout < 0 ? INFINITE : (DWORD)timeout);
        return 0;
    }
    int r = WSAPoll(fds, count, timeout);
    return r == SOCKET_ERROR ? ltw_set_errno() : r;
}

#define socket(d, t, p) ltw_socket(d, t, p)
#define accept(f, a, l) ltw_accept(f, a, l)
#define bind(f, a, l) ltw_bind(f, a, l)
#define listen(f, b) ltw_listen(f, b)
#define connect(f, a, l) ltw_connect(f, a, l)
#define recv(f, b, l, fl) ltw_recv(f, b, l, fl)
#define send(f, b, l, fl) ltw_send(f, b, l, fl)
#define setsockopt(f, lv, n, v, l) ltw_setsockopt(f, lv, n, v, l)
#define getsockopt(f, lv, n, v, l) ltw_getsockopt(f, lv, n, v, l)
#define fcntl ltw_fcntl
#define close(f) ltw_close(f)
#define read(f, b, l) ltw_read(f, b, l)
#define write(f, b, l) ltw_write(f, b, l)
#define poll(f, n, t) ltw_poll(f, n, t)

/* No Unix permissions or syslog: the daemon logs to stderr, which the app keeps. */
#define umask(m) ((mode_t)0)
#define chmod(p, m) 0
#define LOG_PID 0
#define LOG_EMERG 0
#define LOG_ALERT 1
#define LOG_CRIT 2
#define LOG_ERR 3
#define LOG_WARNING 4
#define LOG_NOTICE 5
#define LOG_INFO 6
#define LOG_DEBUG 7
static inline void openlog(const char *ident, int option, int facility) { (void)ident; (void)option; (void)facility; }
static inline void closelog(void) {}
static inline void syslog(int priority, const char *format, ...) { (void)priority; (void)format; }
static inline void vsyslog(int priority, const char *format, va_list ap) { (void)priority; (void)format; (void)ap; }

#endif
