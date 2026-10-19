/* usbmuxd on Windows: netinet/tcp.h is the compat layer, and the BSD TCP header usbmuxd's mux speaks to the device
 * (device.c builds and parses it; Winsock has no such struct). Little-endian layout, as on x86 BSD. */
#include "win32-compat.h"
#ifndef LTW_NETINET_TCP_H
#define LTW_NETINET_TCP_H
#include <stdint.h>

struct tcphdr {
    uint16_t th_sport;
    uint16_t th_dport;
    uint32_t th_seq;
    uint32_t th_ack;
    uint8_t th_x2 : 4;
    uint8_t th_off : 4;
    uint8_t th_flags;
    uint16_t th_win;
    uint16_t th_sum;
    uint16_t th_urp;
};

#define TH_FIN 0x01
#define TH_SYN 0x02
#define TH_RST 0x04
#define TH_PUSH 0x08
#define TH_ACK 0x10
#define TH_URG 0x20
#endif
