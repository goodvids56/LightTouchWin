/*
 * usbmuxd's main on Windows, in place of src/main.c (which daemonizes, takes a lock file, drops privileges and waits
 * in ppoll with signals masked). Light Touch runs one daemon per device in the foreground, so this keeps what that
 * needs: the options the app passes, a TCP listener for libimobiledevice clients (libusbmuxd's
 * USBMUXD_SOCKET_ADDRESS=host:port), preflight pointed back at it, and the same event loop over WSAPoll. Ctrl+C, a
 * closed console or the end of the session stop it as SIGTERM does.
 *
 *     usbmuxd.exe -f [-v ...] -S 127.0.0.1:PORT -C CONFIG_DIR [-p]      (USBMUXD_QEMU_ADDR=127.0.0.1:PORT)
 */
#include <config.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <getopt.h>

#include "log.h"
#include "usb.h"
#include "device.h"
#include "client.h"
#include "conf.h"
#include "utils.h"

int should_exit;
int should_discover;
int use_logfile = 0;
int no_preflight = 0;

static const char *listen_addr = "127.0.0.1:27015";

static BOOL WINAPI on_console_event(DWORD event)
{
    (void)event;
    usbmuxd_log(LL_NOTICE, "Caught console event %lu, exiting", (unsigned long)event);
    should_exit = 1;
    return TRUE;
}

static int create_socket(void)
{
    char host[128];
    const char *colon = strrchr(listen_addr, ':');
    if (!colon || (size_t)(colon - listen_addr) >= sizeof(host)) {
        usbmuxd_log(LL_FATAL, "Listen address must be host:port, not %s", listen_addr);
        return -1;
    }
    memcpy(host, listen_addr, colon - listen_addr);
    host[colon - listen_addr] = '\0';

    struct addrinfo hints = { .ai_family = AF_INET, .ai_socktype = SOCK_STREAM, .ai_flags = AI_PASSIVE };
    struct addrinfo *result = NULL;
    if (getaddrinfo(host, colon + 1, &hints, &result) != 0 || !result) {
        usbmuxd_log(LL_FATAL, "Can't resolve %s", listen_addr);
        return -1;
    }
    int fd = socket(result->ai_family, result->ai_socktype, result->ai_protocol);
    if (fd < 0) {
        freeaddrinfo(result);
        usbmuxd_log(LL_FATAL, "socket() failed: %s", strerror(errno));
        return -1;
    }
    /* Exclusive: a second daemon on the same port must fail, not share it. */
    BOOL exclusive = TRUE;
    setsockopt(fd, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, &exclusive, sizeof(exclusive));
    if (bind(fd, result->ai_addr, (int)result->ai_addrlen) < 0 || listen(fd, 256) < 0) {
        usbmuxd_log(LL_FATAL, "Can't listen on %s: %s", listen_addr, strerror(errno));
        freeaddrinfo(result);
        close(fd);
        return -1;
    }
    freeaddrinfo(result);
    usbmuxd_log(LL_INFO, "Listening on %s", listen_addr);
    return fd;
}

static int main_loop(int listenfd)
{
    struct fdlist pollfds;
    fdlist_create(&pollfds);
    while (!should_exit) {
        int to = usb_get_timeout();
        int dto = device_get_timeout();
        if (dto < to)
            to = dto;
        /* Short enough that a console event's should_exit is seen promptly. */
        if (to > 500)
            to = 500;

        fdlist_reset(&pollfds);
        fdlist_add(&pollfds, FD_LISTEN, listenfd, POLLIN);
        usb_get_fds(&pollfds);
        client_get_fds(&pollfds);

        int cnt = poll(pollfds.fds, pollfds.count, to);
        if (cnt < 0) {
            if (errno == EINTR)
                continue;
            usbmuxd_log(LL_FATAL, "poll() failed: %s", strerror(errno));
            fdlist_free(&pollfds);
            return -1;
        }
        if (cnt == 0) {
            if (usb_process() < 0) {
                usbmuxd_log(LL_FATAL, "usb_process() failed");
                fdlist_free(&pollfds);
                return -1;
            }
            device_check_timeouts();
            continue;
        }
        int done_usb = 0;
        for (int i = 0; i < pollfds.count; i++) {
            short revents = pollfds.fds[i].revents;
            if (revents) {
                if (!done_usb && pollfds.owners[i] == FD_USB) {
                    if (usb_process() < 0) {
                        fdlist_free(&pollfds);
                        return -1;
                    }
                    done_usb = 1;
                }
                if (pollfds.owners[i] == FD_LISTEN && client_accept(listenfd) < 0) {
                    usbmuxd_log(LL_FATAL, "client_accept() failed");
                    fdlist_free(&pollfds);
                    return -1;
                }
                if (pollfds.owners[i] == FD_CLIENT)
                    client_process((int)pollfds.fds[i].fd, revents);
            }
        }
        /* The QEMU backend has no fd that turns readable when the device has data (main.c's note): it gets its turn on
         * every wakeup, once. */
        if (!done_usb && usb_process() < 0) {
            fdlist_free(&pollfds);
            return -1;
        }
    }
    fdlist_free(&pollfds);
    return 0;
}

static void usage(void)
{
    printf("Usage: usbmuxd -f [-v] [-S HOST:PORT] [-C CONFIG_DIR] [-p] [-P NONE]\n");
}

int main(int argc, char *argv[])
{
    static struct option options[] = {
        { "help", no_argument, NULL, 'h' },
        { "foreground", no_argument, NULL, 'f' },
        { "verbose", no_argument, NULL, 'v' },
        { "no-preflight", no_argument, NULL, 'p' },
        { "config-dir", required_argument, NULL, 'C' },
        { "socket", required_argument, NULL, 'S' },
        { "pidfile", required_argument, NULL, 'P' },
        { NULL, 0, NULL, 0 },
    };
    int verbose = 0, c;
    while ((c = getopt_long(argc, argv, "hfvpC:S:P:", options, NULL)) != -1) {
        switch (c) {
        case 'f':
        case 'P':  /* there is no lock file on Windows; NONE is what the app passes */
            break;
        case 'v':
            verbose++;
            break;
        case 'p':
            no_preflight = 1;
            break;
        case 'C':
            config_set_config_dir(optarg);
            break;
        case 'S':
            listen_addr = optarg;
            break;
        case 'h':
            usage();
            return 0;
        default:
            usage();
            return 2;
        }
    }
    setvbuf(stderr, NULL, _IONBF, 0);
    log_level = LL_NOTICE + verbose;
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fprintf(stderr, "usbmuxd: WSAStartup failed\n");
        return 1;
    }
    SetConsoleCtrlHandler(on_console_event, TRUE);

    int listenfd = create_socket();
    if (listenfd < 0)
        return 1;
    /* Preflight connects back to this daemon through libusbmuxd, as main.c's configure_preflight_socket. */
    _putenv_s("USBMUXD_SOCKET_ADDRESS", listen_addr);
    const char *conf = config_get_config_dir();
    CreateDirectoryA(conf, NULL);

    client_init();
    device_init();
    int res = usb_init();
    if (res >= 0) {
        res = main_loop(listenfd);
        if (res < 0)
            usbmuxd_log(LL_FATAL, "main_loop failed");
        device_kill_connections();
        usb_shutdown();
        device_shutdown();
    }
    client_shutdown();
    close(listenfd);
    WSACleanup();
    return res < 0 ? 1 : 0;
}
