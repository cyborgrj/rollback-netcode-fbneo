// ---------------------------------------------------------------------------
// nat_punch.h - UDP hole punching so two players behind home NATs can reach
// each other without anyone touching a router.
//
// Why it lives here and not in the launcher: a NAT mapping is keyed by the
// LOCAL port a packet leaves from. The punch therefore has to happen from the
// very same port libggpo will bind - i.e. inside fbneo.exe, immediately before
// ggpo_start_session().
//
// Flow (rendezvous server = the RBF lobby's UDP port):
//   1. bind UDP on localPort
//   2. spam  "RBF1 REG <matchId> <side>"  at the rendezvous
//   3. server sees our PUBLIC ip:port (the NAT mapping) and, once both sides
//      have registered, answers both with "RBF1 PEER <ip> <port>"
//   4. we fire a few packets straight at the peer's public endpoint, which
//      opens our NAT for their inbound traffic
//   5. close the socket; libggpo rebinds the same local port and talks to the
//      peer's public endpoint - the mapping is still live
//
// Symmetric NAT defeats this (the mapping differs per destination); those
// connections need a relay, which is a later problem.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_NAT_PUNCH_H
#define ROLLBACK_NAT_PUNCH_H

#ifdef __cplusplus
extern "C" {
#endif

enum NatPunchResult {
    NAT_PUNCH_OK          =  0,
    NAT_PUNCH_ERR_ARG     = -1,
    NAT_PUNCH_ERR_RESOLVE = -2,   // rendezvous host did not resolve
    NAT_PUNCH_ERR_SOCKET  = -3,
    NAT_PUNCH_ERR_BIND    = -4,   // local port already taken
    NAT_PUNCH_ERR_TIMEOUT = -5    // peer never showed up at the rendezvous
};

// How the two peers should reach each other. The RENDEZVOUS decides this, not
// the emulator: it is the only party that sees both NATs, and a split verdict
// (one side relaying while the other aims at a public endpoint) meets nowhere.
typedef enum {
    NAT_PUNCH_MODE_DIRECT = 0,  // use the peer's public endpoint below
    NAT_PUNCH_MODE_LAN    = 1,  // same router: keep the LAN address the lobby gave
    NAT_PUNCH_MODE_RELAY  = 2   // symmetric NAT: send everything to the game relay
} NatPunchMode;

typedef struct NatPunchPlan {
    NatPunchMode   mode;
    char           szPeerIp[64];   // peer's PUBLIC endpoint (DIRECT only)
    unsigned short nPeerPort;
} NatPunchPlan;

// Blocks for at most nTimeoutMs, then fills `out`.
//
// nGameRelayPort is the udp port of the relay on the SAME host. We register
// there from this very socket, which does two jobs at once: it primes the relay
// in case the verdict is RELAY, and it gives the rendezvous a second vantage
// point on our NAT. A NAT that remaps per destination shows a different source
// endpoint at the two ports, and that is precisely the case hole punching cannot
// win. Pass 0 to skip the probe (no relay configured).
//
// pfnLog is optional and receives one-line progress messages.
int NatPunchResolvePeer(const char*      szRendezvousHost,
                        unsigned short   nRendezvousPort,
                        unsigned short   nGameRelayPort,
                        const char*      szMatchId,
                        int              nSide,          // 1 or 2
                        unsigned short   nLocalPort,     // the port libggpo will bind
                        NatPunchPlan*  out,
                        int              nTimeoutMs,
                        void           (*pfnLog)(const char*));

#ifdef __cplusplus
}
#endif

#endif // ROLLBACK_NAT_PUNCH_H
