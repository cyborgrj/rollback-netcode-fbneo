// ---------------------------------------------------------------------------
// nat_punch.cpp - see nat_punch.h for the protocol and the reasoning.
// ---------------------------------------------------------------------------
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include "nat_punch.h"

#include <stdio.h>
#include <stdarg.h>
#include <string.h>

#define PUNCH_RETRY_MS   250   // how often we re-announce at the rendezvous
#define PUNCH_SHOTS      12    // packets fired at the peer to open our NAT
#define PUNCH_SHOT_GAP   40    // ms between them

static void punch_log(void (*pfnLog)(const char*), const char* fmt, ...)
{
    if (!pfnLog) return;
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf(buf, sizeof(buf) - 1, fmt, ap);
    va_end(ap);
    buf[sizeof(buf) - 1] = '\0';
    pfnLog(buf);
}

int NatPunchResolvePeer(const char*      szRendezvousHost,
                        unsigned short   nRendezvousPort,
                        unsigned short   nGameRelayPort,
                        const char*      szMatchId,
                        int              nSide,
                        unsigned short   nLocalPort,
                        NatPunchPlan*  out,
                        int              nTimeoutMs,
                        void           (*pfnLog)(const char*))
{
    if (!szRendezvousHost || !*szRendezvousHost || !szMatchId || !*szMatchId ||
        !out || nRendezvousPort == 0)
        return NAT_PUNCH_ERR_ARG;

    memset(out, 0, sizeof(*out));
    out->mode = NAT_PUNCH_MODE_DIRECT;

    // libggpo/FBNeo have not started Winsock yet at this point.
    WSADATA wsad;
    int wsaStarted = (WSAStartup(MAKEWORD(2, 2), &wsad) == 0);

    struct addrinfo hints, *ai = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family   = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    char szPort[16];
    _snprintf(szPort, sizeof(szPort) - 1, "%u", (unsigned)nRendezvousPort);
    szPort[sizeof(szPort) - 1] = '\0';

    if (getaddrinfo(szRendezvousHost, szPort, &hints, &ai) != 0 || ai == NULL) {
        punch_log(pfnLog, "nat punch: could not resolve rendezvous %s", szRendezvousHost);
        if (wsaStarted) WSACleanup();
        return NAT_PUNCH_ERR_RESOLVE;
    }
    struct sockaddr_in srv = *(struct sockaddr_in*)ai->ai_addr;
    freeaddrinfo(ai);

    SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
    if (s == INVALID_SOCKET) {
        if (wsaStarted) WSACleanup();
        return NAT_PUNCH_ERR_SOCKET;
    }
    int on = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof(on));

    struct sockaddr_in local;
    memset(&local, 0, sizeof(local));
    local.sin_family      = AF_INET;
    local.sin_addr.s_addr = htonl(INADDR_ANY);
    local.sin_port        = htons(nLocalPort);
    if (bind(s, (struct sockaddr*)&local, sizeof(local)) == SOCKET_ERROR) {
        punch_log(pfnLog, "nat punch: cannot bind local port %u (err %d)", nLocalPort, WSAGetLastError());
        closesocket(s);
        if (wsaStarted) WSACleanup();
        return NAT_PUNCH_ERR_BIND;
    }

    // The relay is on the same host as the rendezvous, so it is the same address
    // with a different port - no second lookup needed.
    struct sockaddr_in relay = srv;
    relay.sin_port = htons(nGameRelayPort);

    char szReg[160];
    int nReg = _snprintf(szReg, sizeof(szReg) - 1, "RBF1 REG %s %d\n", szMatchId, nSide);
    if (nReg < 0) nReg = 0;

    char szGreg[160];
    int nGreg = _snprintf(szGreg, sizeof(szGreg) - 1, "RBF1 GREG %s %d\n", szMatchId, nSide);
    if (nGreg < 0) nGreg = 0;

    char szPeerIp[64] = "";
    unsigned int nPeerPort = 0;
    int bGotPeer = 0;
    char szSelfIp[64] = "";
    unsigned int nSelfPort = 0;
    int bRelaySeen = 0;

    DWORD tStart    = GetTickCount();
    DWORD tLastSend = 0;
    int   bEverSent = 0;

    while (!bGotPeer && (int)(GetTickCount() - tStart) < nTimeoutMs) {
        // Announce on a clock. Announcing once per loop turn instead would feed
        // itself: every announcement draws a reply, a queued reply makes select
        // return at once, and that immediately announces again - a storm of a
        // hundred-odd packets a second where four will do.
        DWORD now = GetTickCount();
        if (!bEverSent || (int)(now - tLastSend) >= PUNCH_RETRY_MS) {
            sendto(s, szReg, nReg, 0, (struct sockaddr*)&srv, sizeof(srv));
            // Registering at the relay from this same socket is what lets the
            // rendezvous compare the two source endpoints and spot a NAT that
            // remaps per destination.
            if (nGameRelayPort) sendto(s, szGreg, nGreg, 0, (struct sockaddr*)&relay, sizeof(relay));
            tLastSend = now;
            bEverSent = 1;
        }

        fd_set rf;
        FD_ZERO(&rf);
        FD_SET(s, &rf);
        struct timeval tv;
        tv.tv_sec  = 0;
        tv.tv_usec = 50 * 1000;   // short: the clock above decides when to talk

        if (select(0, &rf, NULL, NULL, &tv) <= 0) continue;

        char buf[256];
        struct sockaddr_in from;
        int nFrom = sizeof(from);
        int n = recvfrom(s, buf, sizeof(buf) - 1, 0, (struct sockaddr*)&from, &nFrom);
        if (n <= 0) continue;
        buf[n] = '\0';

        char ip[64];
        char szMode[16] = "";
        unsigned int port = 0;
        if (sscanf(buf, "RBF1 PEER %63s %u %15s", ip, &port, szMode) >= 2 && port != 0) {
            strncpy(szPeerIp, ip, sizeof(szPeerIp) - 1);
            szPeerIp[sizeof(szPeerIp) - 1] = '\0';
            nPeerPort = port;
            bGotPeer  = 1;
            // An older server sends no mode; DIRECT is what it always meant.
            if      (strcmp(szMode, "relay") == 0) out->mode = NAT_PUNCH_MODE_RELAY;
            else if (strcmp(szMode, "lan")   == 0) out->mode = NAT_PUNCH_MODE_LAN;
            else                                   out->mode = NAT_PUNCH_MODE_DIRECT;
        } else if (sscanf(buf, "RBF1 GSELF %63s %u", ip, &port) == 2) {
            // The rendezvous compares the two sightings itself; all we need to
            // know here is that the relay is reachable at all.
            if (!bRelaySeen) {
                bRelaySeen = 1;
                punch_log(pfnLog, "nat punch: game relay answered from our side (%s:%u)", ip, port);
            }
        } else if (sscanf(buf, "RBF1 SELF %63s %u", ip, &port) == 2) {
            // Only worth a line when it is news. The rendezvous answers every
            // announcement, and a hundred identical lines buried the log.
            if (strcmp(szSelfIp, ip) != 0 || nSelfPort != port) {
                punch_log(pfnLog, "nat punch: our public endpoint is %s:%u", ip, port);
                strncpy(szSelfIp, ip, sizeof(szSelfIp) - 1);
                szSelfIp[sizeof(szSelfIp) - 1] = '\0';
                nSelfPort = port;
            }
        }
    }

    // Worth shouting about: with no sighting at the relay the rendezvous cannot
    // tell a symmetric NAT from an honest one, so it falls back to "direct" and
    // a pair that needed relaying just fails to connect. Almost always a closed
    // udp port on the server.
    if (nGameRelayPort && !bRelaySeen)
        punch_log(pfnLog, "nat punch: WARNING game relay udp/%u never answered - "
                          "symmetric NAT cannot be detected (port closed on the server?)",
                  (unsigned)nGameRelayPort);

    // A server too old to send a verdict still tells us both public addresses,
    // and equal addresses mean one router in front of both of us.
    if (bGotPeer && out->mode == NAT_PUNCH_MODE_DIRECT &&
        szSelfIp[0] && strcmp(szSelfIp, szPeerIp) == 0)
        out->mode = NAT_PUNCH_MODE_LAN;

    if (bGotPeer && out->mode == NAT_PUNCH_MODE_LAN) {
        punch_log(pfnLog, "nat punch: peer is behind the same NAT as us (%s) - staying on the LAN", szSelfIp);
    } else if (bGotPeer && out->mode == NAT_PUNCH_MODE_RELAY) {
        // No point punching: our NAT hands out a different mapping per
        // destination, so the endpoint the peer was given is good for nobody.
        punch_log(pfnLog, "nat punch: NAT simetrico - a partida vai pelo relay udp/%u", (unsigned)nGameRelayPort);
    } else if (bGotPeer) {
        punch_log(pfnLog, "nat punch: peer public endpoint %s:%u - opening NAT", szPeerIp, nPeerPort);

        struct sockaddr_in peer;
        memset(&peer, 0, sizeof(peer));
        peer.sin_family = AF_INET;
        peer.sin_port   = htons((unsigned short)nPeerPort);
        if (inet_pton(AF_INET, szPeerIp, &peer.sin_addr) == 1) {
            for (int i = 0; i < PUNCH_SHOTS; i++) {
                sendto(s, "RBF1 PUNCH\n", 11, 0, (struct sockaddr*)&peer, sizeof(peer));
                Sleep(PUNCH_SHOT_GAP);
            }
        }

        strncpy(out->szPeerIp, szPeerIp, sizeof(out->szPeerIp) - 1);
        out->szPeerIp[sizeof(out->szPeerIp) - 1] = '\0';
        out->nPeerPort = (unsigned short)nPeerPort;
    } else {
        punch_log(pfnLog, "nat punch: peer never registered within %d ms", nTimeoutMs);
    }

    // Release the port so libggpo can rebind it. The NAT mapping outlives this
    // by tens of seconds, which is all we need.
    closesocket(s);
    if (wsaStarted) WSACleanup();

    return bGotPeer ? NAT_PUNCH_OK : NAT_PUNCH_ERR_TIMEOUT;
}
