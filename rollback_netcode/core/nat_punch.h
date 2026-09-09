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

// Blocks for at most nTimeoutMs. On NAT_PUNCH_OK, szOutPeerIp / pOutPeerPort
// hold the peer's PUBLIC endpoint, ready to hand to ggpo_add_player.
//
// pbOutSameNat (optional) comes back 1 when the rendezvous saw BOTH of us at the
// same public address - i.e. we are on the same LAN. Talking through the public
// address then means asking the router to hairpin, which plenty of consumer
// routers do in only one direction; the caller should use the LAN address it
// already has instead. pfnLog is optional and receives progress lines.
int NatPunchResolvePeer(const char*     szRendezvousHost,
                        unsigned short  nRendezvousPort,
                        const char*     szMatchId,
                        int             nSide,          // 1 or 2
                        unsigned short  nLocalPort,     // the port libggpo will bind
                        char*           szOutPeerIp,
                        int             nOutPeerIpLen,
                        unsigned short* pOutPeerPort,
                        int*            pbOutSameNat,
                        int             nTimeoutMs,
                        void          (*pfnLog)(const char*));

#ifdef __cplusplus
}
#endif

#endif // ROLLBACK_NAT_PUNCH_H
