// ---------------------------------------------------------------------------
// relay.h - Spectator transport: the match host publishes its confirmed input
// stream to the RBF lobby machine over TCP, and spectators subscribe to it.
//
// Why a relay and not GGPO's own spectator mode: libggpo refuses a spectator
// once the game is running (p2p.cpp, AddSpectator -> INVALID_REQUEST if
// !_synchronizing), its spectator buffer is 64 frames deep, and every viewer
// costs the host upload and another NAT traversal. Routing through the lobby
// costs ~480 bytes/s per match, lets people join a fight already in progress,
// works through any NAT because both ends dial OUT, and leaves a replay behind.
//
// What travels:
//   * once, at match start - a full save state, so a spectator never diverges
//     because of a different NVRAM/EEPROM or a different boot moment;
//   * then, one record per frame, but only for frames libggpo can no longer
//     roll back (see ggpo_bridge's confirm lag).
//
// Threading: every public call here runs on the EMULATION thread and never
// blocks on the network. One worker thread per role owns the socket; the emu
// thread only touches a small buffer under a critical section.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_RELAY_H
#define ROLLBACK_RELAY_H

#ifdef __cplusplus
extern "C" {
#endif

enum RelayResult {
	RELAY_OK           =  0,
	RELAY_ERR_ARG      = -1,
	RELAY_ERR_SOCKET   = -2,
	RELAY_ERR_RESOLVE  = -3,
	RELAY_ERR_CONNECT  = -4,
	RELAY_ERR_PROTOCOL = -5,
	RELAY_ERR_TIMEOUT  = -6,
	RELAY_ERR_CLOSED   = -7,   // the stream ended (host left)
	RELAY_ERR_MEMORY   = -8
};

#define RELAY_MAX_NAME 32

typedef struct RelayStreamInfo {
	char szGame[64];
	char szP1[RELAY_MAX_NAME];
	char szP2[RELAY_MAX_NAME];
	int  nPlayers;
	int  nInputBytes;      // per player
	int  nStateLen;        // bytes in the opening save state
} RelayStreamInfo;

// ===========================================================================
//  Publisher - the machine playing side 1
// ===========================================================================
// Copies the state, spawns the worker and returns immediately; the connection
// is made on the worker, so a dead relay never delays the match. Failures are
// reported through the log callback and simply leave the match unwatchable.
int  RelayPublishStart(const char* szHost, unsigned short nPort,
                       const char* szMatchId, const RelayStreamInfo* info,
                       const void* pState, int nStateLen,
                       void (*pfnLog)(const char*));

// Emu thread. Queues one confirmed frame. Never blocks and never allocates.
// Frames must arrive in order with no gaps - a gap would desync every viewer,
// so if the queue ever fills the whole stream is torn down instead.
void RelayPublishInputs(int nFrame, const void* pInputs, int nBytes);

void RelayPublishStop(void);
int  RelayPublishIsUp(void);          // 1 while the socket is alive

// ===========================================================================
//  Subscriber - a spectator
// ===========================================================================
// Blocks up to nTimeoutMs for the header and the opening save state, because
// nothing can be emulated without them. The frame stream then fills in the
// background.
int  RelayWatchStart(const char* szHost, unsigned short nPort,
                     const char* szMatchId, int nTimeoutMs,
                     void (*pfnLog)(const char*));

int         RelayWatchInfo(RelayStreamInfo* out);
const void* RelayWatchState(int* pnLen);   // borrowed until RelayWatchStop
int         RelayWatchPending(void);       // frames received but not consumed
int         RelayWatchEnded(void);         // 1 once the host stopped publishing

// Emu thread. Copies the next frame's inputs (nBytes = players*inputBytes).
//   1  => got one
//   0  => nothing buffered yet (stall a frame)
//  <0  => stream finished and drained
int  RelayWatchNext(void* pOut, int nBytes);

void RelayWatchStop(void);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_RELAY_H
