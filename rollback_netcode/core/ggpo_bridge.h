// ---------------------------------------------------------------------------
// ggpo_bridge.h - Session + callback glue between libggpo and the FBNeo core.
//
// Responsibilities:
//   * Own the GGPOSession, player handles and the 7 GGPOSessionCallbacks.
//   * Wire save/load/free callbacks to state_ring (zero per-frame alloc).
//   * Drive one deterministic core step per synchronized frame, including the
//     silent re-simulation steps libggpo requests during a rollback.
//   * Expose a tiny host interface so this file never references FBNeo symbols
//     directly (keeps it unit-testable and reusable by the gRPC agent).
//
// Threading: every function here, and every callback, runs on the EMULATION
// thread only. The gRPC control agent lives on another thread and must hand
// work to the emu thread via a queue - it never calls into this module.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_GGPO_BRIDGE_H
#define ROLLBACK_GGPO_BRIDGE_H

#include <stddef.h>   // size_t (used by the inline helper below)

#ifdef __cplusplus
extern "C" {
#endif

#define GGPO_BRIDGE_MAX_PLAYERS     4
#define GGPO_BRIDGE_MAX_INPUT_BYTES 8   // per player

// ---- status codes --------------------------------------------------------
enum GgpoBridgeResult {
	GGPO_BRIDGE_OK          =  0,  // frame advanced
	GGPO_BRIDGE_SKIPPED     =  1,  // no advance this tick (libggpo is catching up)
	GGPO_BRIDGE_ERR_STATE  = -1,  // no active session
	GGPO_BRIDGE_ERR_ARG    = -2,  // bad config
	GGPO_BRIDGE_ERR_GGPO   = -3,  // libggpo call failed (see GgpoBridgeLastError)
	GGPO_BRIDGE_ERR_HOST   = -4,  // host step_frame returned non-zero
	GGPO_BRIDGE_ERR_RING   = -5   // state_ring save/load failed
};

// ---- events (mirror of GGPO's, so consumers need not include ggponet.h) --
typedef enum {
	GGPO_BRIDGE_EV_CONNECTED = 1,
	GGPO_BRIDGE_EV_SYNCHRONIZING,      // a = count, b = total
	GGPO_BRIDGE_EV_SYNCHRONIZED,
	GGPO_BRIDGE_EV_RUNNING,
	GGPO_BRIDGE_EV_DISCONNECTED,
	GGPO_BRIDGE_EV_CONNECTION_INTERRUPTED, // a = timeout_ms
	GGPO_BRIDGE_EV_CONNECTION_RESUMED,
	GGPO_BRIDGE_EV_TIMESYNC               // a = frames_ahead
} GgpoBridgeEventCode;

typedef struct {
	GgpoBridgeEventCode code;
	int player;   // GGPO player handle, when relevant (-1 otherwise)
	int a;
	int b;
} GgpoBridgeEvent;

// ---- host interface (supplied by the FBNeo integration layer) -----------
typedef struct GgpoBridgeHost {
	// Fill `out` (nInputBytes) with this machine's current input for the local
	// player. Called once per tick, before prediction.
	void (*poll_local_input)(void* out, int nInputBytes, void* user);

	// Run EXACTLY ONE deterministic core frame.
	//   syncInputs : nPlayers * nInputBytes bytes, player-major order
	//   bRollback  : 1 => silent re-simulation (MUST NOT draw or emit audio)
	// Return 0 on success, non-zero to abort the session.
	int  (*step_frame)(const void* syncInputs, int nPlayers,
	                   int disconnectFlags, int bRollback, void* user);

	// Optional. Session lifecycle / link-quality notifications.
	void (*on_event)(const GgpoBridgeEvent* ev, void* user);

	// Optional. Desync dump target; NULL => bridge skips logging.
	void (*log_state)(const char* filename, const unsigned char* buf, int len, void* user);

	// Optional. One call per frame whose inputs libggpo can no longer revise,
	// in strict order from frame 0, with no gaps. This is exactly the stream a
	// spectator can replay: predicted frames are never reported, and a frame is
	// only handed over once the simulation has moved further ahead than libggpo
	// is ever allowed to roll back.
	//   inputs : nPlayers * nInputBytes bytes, player-major (same as step_frame)
	void (*on_confirmed_inputs)(int frame, const void* inputs, int nBytes, void* user);

	void* user;
} GgpoBridgeHost;

// ---- session config ----------------------------------------------------------
typedef struct GgpoBridgeConfig {
	char           szGameId[64];        // opaque id handed to libggpo
	int            nPlayers;            // 2 .. GGPO_BRIDGE_MAX_PLAYERS
	int            nInputBytes;         // per player, 1 .. GGPO_BRIDGE_MAX_INPUT_BYTES
	int            nLocalPlayer;        // 1-based player_num that is local
	unsigned short nLocalPort;
	char           szRemoteIp[32];      // v1: single remote peer (2p)
	unsigned short nRemotePort;
	int            nFrameDelay;         // local input delay, frames (e.g. 2)
	int            nDisconnectTimeoutMs;// 0 => libggpo default
	int            nDisconnectNotifyMs; // 0 => libggpo default
	int            nIdleTimeoutMs;      // passed to ggpo_idle each tick (0 ok)
	int            nStateSlots;         // forwarded to StateRingInit (0 => default)
} GgpoBridgeConfig;

// ---- lifecycle ----------------------------------------------------------------
// state_ring MUST already be initialised (host runs >=1 core frame, then
// StateRingInit). Returns GGPO_BRIDGE_OK or a negative code.
int  GgpoBridgeStart(const GgpoBridgeConfig* cfg, const GgpoBridgeHost* host);

// Offline determinism check: no networking; libggpo saves every frame, rolls
// back `nCheckDistance` frames and compares checksums. Same host interface.
int  GgpoBridgeStartSyncTest(const GgpoBridgeConfig* cfg, const GgpoBridgeHost* host,
                             int nCheckDistance);

void GgpoBridgeClose(void);
int  GgpoBridgeIsRunning(void);

// ---- per-rendered-frame driver ---------------------------------------------
// Call once for every frame the host wants to present. Internally: ggpo_idle,
// poll local input, predict + synchronize, one live step_frame, ggpo_advance.
// Rollback re-simulation happens synchronously inside this call via callbacks.
// Returns GGPO_BRIDGE_OK (advanced -> present) / GGPO_BRIDGE_SKIPPED / <0.
int  GgpoBridgeTick(void);

// ---- introspection (emu thread; snapshot for the gRPC agent) --------------
typedef struct {
	int  valid;
	int  ping_ms;
	int  send_queue_len;
	int  recv_queue_len;
	int  kbps_sent;
	int  local_frames_behind;
	int  remote_frames_behind;
} GgpoBridgeNetStats;

int GgpoBridgeGetNetworkStats(int nPlayerHandle, GgpoBridgeNetStats* out);

// The other player, without the caller having to know about handles. In a
// two-player match that is the only peer there is; with more, it is the first
// one that is not us.
int GgpoBridgeGetPeerStats(GgpoBridgeNetStats* out);

int GgpoBridgeLastError(void);        // last raw GGPOErrorCode seen
long long GgpoBridgeFrameCount(void); // live frames advanced since Start

// Input delay in frames, as the session was actually started - which is the
// average of what the two players asked for, decided by the server. Not what
// this machine requested.
int GgpoBridgeFrameDelay(void);

// ---- rollback, as it is happening -----------------------------------------
// How many frames libggpo re-simulated during the most recent live frame, and
// the worst such burst in about the last second.
//
// The peak is the one worth showing a player. The per-frame number is 0 most
// of the time and jumps to 3 or 5 for a single frame, sixty times a second -
// a display of that is a blur nobody can read. The peak holds long enough to
// mean something and decays on its own when the line settles down.
int GgpoBridgeRollbackFrames(void);
int GgpoBridgeRollbackPeak(void);

// Extract player `idx`'s input word from a syncInputs buffer (nInputBytes<=4).
static inline unsigned int GgpoBridgeInputWord(const void* syncInputs, int idx, int nInputBytes)
{
	const unsigned char* p = (const unsigned char*)syncInputs + (size_t)idx * nInputBytes;
	unsigned int v = 0;
	for (int i = 0; i < nInputBytes && i < 4; i++) v |= (unsigned int)p[i] << (8 * i);
	return v;
}

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_GGPO_BRIDGE_H
