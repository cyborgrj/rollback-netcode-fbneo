// ---------------------------------------------------------------------------
// fbneo_host.h - Concrete FBNeo implementation of GgpoBridgeHost.
//
// This is the one module that is *allowed* to know both FBNeo and the rollback
// core. It:
//   * builds a deterministic driver-input <-> compact-bitmask map at Start;
//   * implements poll_local_input / step_frame / on_event for ggpo_bridge;
//   * defers StateRingInit until the driver has run its first frame;
//   * exposes FbnHostRunFrame() to be called once per frame from RunFrame().
//
// v1 scope: digital inputs only (fighting games). Analog inputs are logged and
// left unsynchronised. DIP switches are match-static and travel in the save
// state, not per frame - both peers must pick the same DIPs before Start.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_FBNEO_HOST_H
#define ROLLBACK_FBNEO_HOST_H

#ifdef __cplusplus
extern "C" {
#endif

typedef struct FbnHostConfig {
	char           szGameId[64];     // opaque id for libggpo (use the FBNeo short name)
	int            nPlayers;         // 2 .. 4
	int            nLocalPlayer;     // 1-based side this machine plays in the match
	int            nInputPlayer;     // 1-based driver player the LOCAL controls are
	                                 // bound to in FBNeo. 0 => 1 (the default binding).
	                                 // NOT the same as nLocalPlayer: the keyboard is
	                                 // wired to P1 even when you are P2 in the match.
	unsigned short nLocalPort;       // local UDP port
	char           szRemoteIp[32];   // remote peer (v1: single remote, 2p)
	unsigned short nRemotePort;
	int            nFrameDelay;      // local input delay frames; 0 => default 2
	int            nStateSlots;      // 0 => state_ring default (16)

	// NAT hole punching (optional). When all three are set, we announce at
	// szPunchIp:nPunchPort from nLocalPort before opening the GGPO session and
	// replace szRemoteIp/nRemotePort with the peer's PUBLIC endpoint.
	char           szPunchIp[64];
	unsigned short nPunchPort;
	char           szMatchId[40];

	// udp port of the game relay on the SAME host as the rendezvous. Used when
	// the rendezvous rules that this pair cannot be punched. 0 = no relay.
	unsigned short nGameRelayPort;

	// Spectator relay (optional). Side 1 publishes the match here so others can
	// watch it; side 2 ignores these. The names are only shown to viewers.
	char           szRelayIp[64];
	unsigned short nRelayPort;
	char           szP1Name[32];
	char           szP2Name[32];
} FbnHostConfig;

// Watching somebody else's match: no GGPO session, no input of our own. The
// emulator replays the host's confirmed input stream on top of the save state
// the relay opens with, fast-forwarding silently whenever it falls behind.
typedef struct FbnWatchConfig {
	char           szRelayIp[64];
	unsigned short nRelayPort;
	char           szMatchId[40];
} FbnWatchConfig;

// Start a networked session / an offline determinism check. The active FBNeo
// driver must already be initialised (BurnDrvInit done). Returns 0 on success.
int  FbnHostStart(const FbnHostConfig* cfg);
int  FbnHostStartSyncTest(const FbnHostConfig* cfg, int nCheckDistance);
int  FbnHostStartWatch(const FbnWatchConfig* cfg);
void FbnHostStop(void);
int  FbnHostIsActive(void);    // a match OR a spectated stream is running
int  FbnHostIsWatching(void);

// Bracket FBNeo GetInput() call. Before fills the driver input bytes with a
// sentinel; after takes this machine-s copy of the controls and works out which
// driver player they belong to. Both are no-ops outside a match.
void FbnHostBeforeInput(void);
void FbnHostAfterInput(void);

// Call once per emulated frame from RunFrame(), AFTER GetInput(true) has
// populated the driver input bytes. Runs exactly one synchronized frame; any
// rollback re-simulation runs silently and synchronously inside this call.
// The live frame is drawn+presented via FBNeo's own VidFrame() when bDraw != 0.
//   1  => a frame advanced
//   0  => no advance this tick (libggpo is catching up)
//  <0  => fatal; caller should FbnHostStop()
int  FbnHostRunFrame(int bDraw);

// Append one line to rbf-netplay.log, the emulator-side diagnostic log. Shared
// so every module that runs inside the emulator writes to one place, in order.
void FbnHostLogLine(const char* s);

// diagnostics (emu thread) - snapshots for the gRPC agent
int       FbnHostInputBytesPerPlayer(void);
long long FbnHostFrameCount(void);
int       FbnHostLastError(void);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_FBNEO_HOST_H
