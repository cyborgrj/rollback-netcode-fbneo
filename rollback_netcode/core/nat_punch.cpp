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

int NatPunchResolvePeer(const char*     szRendezvousHost,
                        unsigned short  nRendezvousPort,
                        const char*     szMatchId,
                        int             nSide,
                        unsigned short  nLocalPort,
                        char*           szOutPeerIp,
                        int             nOutPeerIpLen,
                        unsigned short* pOutPeerPort,
                        int*            pbOutSameNat,
                        int             nTimeoutMs,
                        void          (*pfnLog)(const char*))
{
    if (!szRendezvousHost || !*szRendezvousHost || !szMatchId || !*szMatchId ||
        !szOutPeerIp || nOutPeerIpLen < 16 || !pOutPeerPort || nRendezvousPort == 0)
        return NAT_PUNCH_ERR_ARG;

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

    char szReg[160];
    int nReg = _snprintf(szReg, sizeof(szReg) - 1, "RBF1 REG %s %d\n", szMatchId, nSide);
    if (nReg < 0) nReg = 0;

    char szPeerIp[64] = "";
    unsigned int nPeerPort = 0;
    int bGotPeer = 0;
    char szSelfIp[64] = "";
    if (pbOutSameNat) *pbOutSameNat = 0;

    DWORD tStart = GetTickCount();
    while (!bGotPeer && (int)(GetTickCount() - tStart) < nTimeoutMs) {
        sendto(s, szReg, nReg, 0, (struct sockaddr*)&srv, sizeof(srv));

        fd_set rf;
        FD_ZERO(&rf);
        FD_SET(s, &rf);
        struct timeval tv;
        tv.tv_sec  = 0;
        tv.tv_usec = PUNCH_RETRY_MS * 1000;

        if (select(0, &rf, NULL, NULL, &tv) <= 0) continue;

        char buf[256];
        struct sockaddr_in from;
        int nFrom = sizeof(from);
        int n = recvfrom(s, buf, sizeof(buf) - 1, 0, (struct sockaddr*)&from, &nFrom);
        if (n <= 0) continue;
        buf[n] = '\0';

        char ip[64];
        unsigned int port = 0;
        if (sscanf(buf, "RBF1 PEER %63s %u", ip, &port) == 2 && port != 0) {
            strncpy(szPeerIp, ip, sizeof(szPeerIp) - 1);
            szPeerIp[sizeof(szPeerIp) - 1] = '\0';
            nPeerPort = port;
            bGotPeer  = 1;
        } else if (sscanf(buf, "RBF1 SELF %63s %u", ip, &port) == 2) {
            strncpy(szSelfIp, ip, sizeof(szSelfIp) - 1);
            szSelfIp[sizeof(szSelfIp) - 1] = '\0';
            punch_log(pfnLog, "nat punch: our public endpoint is %s:%u", ip, port);
        }
    }

    // Both of us seen at the same public address means we are behind the same
    // router. Reaching each other through it would need a hairpin, and a router
    // that hairpins in only one direction leaves one side deaf while the other
    // synchronises happily - which is exactly what a one-sided handshake looks
    // like. The LAN address the lobby already gave us is better in every way.
    int bSameNat = (bGotPeer && szSelfIp[0] && strcmp(szSelfIp, szPeerIp) == 0);
    if (pbOutSameNat) *pbOutSameNat = bSameNat;

    if (bSameNat) {
        punch_log(pfnLog, "nat punch: peer is behind the same NAT as us (%s) - staying on the LAN", szSelfIp);
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

        strncpy(szOutPeerIp, szPeerIp, nOutPeerIpLen - 1);
        szOutPeerIp[nOutPeerIpLen - 1] = '\0';
        *pOutPeerPort = (unsigned short)nPeerPort;
    } else {
        punch_log(pfnLog, "nat punch: peer never registered within %d ms", nTimeoutMs);
    }

    // Release the port so libggpo can rebind it. The NAT mapping outlives this
    // by tens of seconds, which is all we need.
    closesocket(s);
    if (wsaStarted) WSACleanup();

    return bGotPeer ? NAT_PUNCH_OK : NAT_PUNCH_ERR_TIMEOUT;
}
