// ---------------------------------------------------------------------------
// fbneo_host.cpp - see fbneo_host.h for the contract.
//
// Build include paths:
//   -I <fbneo>/src/burn                 (burnint.h and friends)
//   -I <repo>/rollback_netcode/core     (or use the relative includes below)
//   -I <repo>/rollback_netcode/third_party/ggpo/src/include  (via ggpo_bridge)
// ---------------------------------------------------------------------------
#include "burnint.h"      // pBurnDraw, pBurnSoundOut, bBurnRunAheadFrame,
                          // BurnDrvFrame, BurnDrvGetInputInfo, BIT_DIGITAL, bprintf

#include "fbneo_host.h"
#include "../core/state_ring.h"
#include "../core/ggpo_bridge.h"
#include "../core/nat_punch.h"
#include "../core/relay.h"
#include "../core/port_map.h"
#include "match_score.h"
#include "overlay.h"

#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <time.h>

// Burner-level flag (declared per-platform in burner_*.h, also in burn/cheat.cpp).
// Re-declared here so this file needs only burnint.h. 1 once BurnDrvInit succeeded.
extern int bDrvOkay;

// intf/interface.h - runs one frame through the video blitter (which sets the
// pitch and presents to screen). Re-declared to avoid pulling in burner.h.
extern INT32 VidFrame();

static int g_liveDraw = 1;   // set from FbnHostRunFrame each tick

// ---- rbf-netplay.log --------------------------------------------------------
// Dedicated, low-frequency diagnostic log next to fbneo.exe. Works in release
// builds too (unlike FBNeo's zzBurnDebug.html). Opened per line - never hot.
static void RbfLog(const char* fmt, ...)
{
	FILE* f = fopen("rbf-netplay.log", "a");
	if (!f) return;
	time_t t = time(NULL);
	struct tm* lt = localtime(&t);
	if (lt) fprintf(f, "[%02d:%02d:%02d] ", lt->tm_hour, lt->tm_min, lt->tm_sec);
	va_list ap;
	va_start(ap, fmt);
	vfprintf(f, fmt, ap);
	va_end(ap);
	fputc('\n', f);
	fclose(f);
}

// plain callback shape for modules that take a logger (nat_punch, ram_probe)
static void RbfLogLine(const char* s) { RbfLog("%s", s ? s : ""); }

// ...and the same thing for the FBNeo side of the fence, which has no other way
// to reach this log.
void FbnHostLogLine(const char* s) { RbfLogLine(s); }

// ===========================================================================
//  Driver-input map:  FBNeo digital inputs  <->  per-player bit index
// ===========================================================================
#define FBN_MAX_BITS_PER_PLAYER (GGPO_BRIDGE_MAX_INPUT_BYTES * 8)

typedef struct {
	UINT8* pVal[FBN_MAX_BITS_PER_PLAYER]; // driver byte backing bit b
	int    nBits;
} FbnPlayerMap;

static FbnPlayerMap  g_map[GGPO_BRIDGE_MAX_PLAYERS];
static int           g_nPlayers    = 0;
static int           g_nInputBytes = 0;
static int           g_localPlayer = 1;   // our side in the match (1 or 2)
static int           g_inputPlayer = 1;   // driver player the LOCAL controls are bound to
static int           g_active      = 0;
static int           g_ringReady   = 0;
static int           g_analogWarned = 0;
static FbnHostConfig g_cfg;

// ---- spectating ----------------------------------------------------------
static int           g_watch        = 0;   // this process is a viewer, not a player
static int           g_relayPending = 0;   // publish as soon as the ring is up
static long long     g_watchFrames  = 0;
// Stay a fifth of a second behind the broadcast; below that a hiccup on the
// host would stall the picture, above it we are needlessly late.
#define FBN_WATCH_CUSHION  12
// Silent frames per tick while catching up. 60 clears a five-minute backlog in
// under ten seconds and still leaves the window responsive between ticks.
#define FBN_WATCH_BURST    60
static GgpoBridgeHost g_hostVtbl;

// "P1 Fire 2" -> 1, "P3 Up" -> 3, "Reset"/"Service" -> 0
static int playerOfInput(const char* szName)
{
	if (!szName) return 0;
	if ((szName[0] == 'P' || szName[0] == 'p') && szName[1] >= '1' && szName[1] <= '9')
		return szName[1] - '0';
	return 0;
}

// Walk the driver input list in its natural order and assign bit indices.
// Order is stable for a given driver build => both peers agree implicitly.
static int buildInputMap(void)
{
	memset(g_map, 0, sizeof(g_map));
	g_analogWarned = 0;

	struct BurnInputInfo bii;
	for (UINT32 i = 0; ; i++) {
		memset(&bii, 0, sizeof(bii));
		if (BurnDrvGetInputInfo(&bii, i)) break;   // non-zero => index past end

		const int pl = playerOfInput(bii.szName);
		if (pl < 1 || pl > g_nPlayers) continue;    // genuine common inputs / spectator players

		if (bii.nType != BIT_DIGITAL) {             // analog / constant: out of scope for v1
			if (!g_analogWarned) {
				bprintf(PRINT_IMPORTANT,
				        _T("[fbneo_host] WARNING: non-digital input \"%S\" is NOT rolled back (v1 digital-only).\n"),
				        bii.szName);
				g_analogWarned = 1;
			}
			continue;
		}

		FbnPlayerMap* m = &g_map[pl - 1];
		if (m->nBits >= FBN_MAX_BITS_PER_PLAYER) {
			bprintf(PRINT_ERROR,
			        _T("[fbneo_host] ERROR: player %d has more than %d digital inputs.\n"),
			        pl, FBN_MAX_BITS_PER_PLAYER);
			return -1;
		}
		m->pVal[m->nBits++] = bii.pVal;
	}

	int maxBits = 0;
	for (int p = 0; p < g_nPlayers; p++)
		if (g_map[p].nBits > maxBits) maxBits = g_map[p].nBits;

	if (maxBits == 0) {
		bprintf(PRINT_ERROR, _T("[fbneo_host] ERROR: no digital inputs mapped for %d players.\n"), g_nPlayers);
		return -1;
	}

	g_nInputBytes = (maxBits + 7) / 8;              // uniform across players (libggpo: one input_size)
	return 0;
}

// ===========================================================================
//  GgpoBridgeHost callbacks
// ===========================================================================

// ---- reading OUR controls, and only ours ----------------------------------
//
// The driver's input bytes are shared ground: FBNeo writes this machine's
// controls into them, and applyInputs writes the whole match's synchronized
// inputs into the same bytes. Reading our own controls straight out of them is
// therefore only safe at one instant - right after FBNeo has written and
// before anything else has. Any later, and a rollback has already re-simulated
// several frames on top, so we read the PEER's input back out and send it as
// our own. That is what "player one is driving both characters" looks like.
//
// So we take a copy at that one safe instant (FbnHostAfterInput, called from
// run.cpp immediately after GetInput) and poll from the copy.
//
// The same moment answers a question we used to assume the answer to: WHICH
// driver player holds this machine's controls. FBNeo only writes the inputs
// the user has actually bound, so by filling every input byte with a value it
// would never produce and seeing which ones come back changed, we learn what is
// bound instead of hoping it is player one. Somebody who mapped only the P2
// controls used to end up polling the bytes their opponent's inputs land in.
#define FBN_INPUT_SENTINEL 0xAA

static unsigned char g_snap[GGPO_BRIDGE_MAX_PLAYERS][GGPO_BRIDGE_MAX_INPUT_BYTES];
static int           g_boundMask   = 0;   // bit p-1 set when player p is bound here
static int           g_boundLogged = 0;

void FbnHostBeforeInput(void)
{
	if (!g_active || g_watch) return;
	for (int p = 0; p < g_nPlayers; p++) {
		const FbnPlayerMap* m = &g_map[p];
		for (int b = 0; b < m->nBits; b++)
			if (m->pVal[b]) *m->pVal[b] = FBN_INPUT_SENTINEL;
	}
}

void FbnHostAfterInput(void)
{
	if (!g_active || g_watch) return;

	int mask = 0;
	memset(g_snap, 0, sizeof(g_snap));

	for (int p = 0; p < g_nPlayers; p++) {
		const FbnPlayerMap* m = &g_map[p];
		int bTouched = 0;
		for (int b = 0; b < m->nBits; b++) {
			if (!m->pVal[b]) continue;
			const unsigned char v = *m->pVal[b];
			if (v == FBN_INPUT_SENTINEL) {
				*m->pVal[b] = 0;    // unbound: never leave the sentinel where the game could read it
				continue;
			}
			bTouched = 1;
			if (v) g_snap[p][b >> 3] |= (unsigned char)(1 << (b & 7));
		}
		if (bTouched) mask |= 1 << p;
	}

	g_boundMask = mask;

	// P1 first, because that is where FBNeo puts a single player's controls by
	// default and changing that for somebody whose setup already works would be
	// a regression. Only when P1 is not bound do we go looking.
	int want = g_inputPlayer;
	if (mask & 1) {
		want = 1;
	} else if (mask) {
		for (int p = 0; p < g_nPlayers; p++) if (mask & (1 << p)) { want = p + 1; break; }
	}

	if (!g_boundLogged) {
		g_boundLogged = 1;
		RbfLog("input: controls bound here = mask 0x%X, reading player %d (we are side %d)",
		       mask, want, g_localPlayer);
	}
	g_inputPlayer = want;
}

static void host_poll_local_input(void* out, int nInputBytes, void* /*user*/)
{
	memset(out, 0, nInputBytes);
	const int p = (g_inputPlayer >= 1 && g_inputPlayer <= g_nPlayers) ? g_inputPlayer - 1 : 0;
	memcpy(out, g_snap[p], (nInputBytes < GGPO_BRIDGE_MAX_INPUT_BYTES)
	                        ? nInputBytes : GGPO_BRIDGE_MAX_INPUT_BYTES);
}

// Write libggpo's synchronized inputs (all players, prediction-corrected) back
// into the driver bytes. Unmapped bytes (DIPs, common) are left untouched.
static void applyInputs(const void* syncInputs, int nPlayers)
{
	const unsigned char* in = (const unsigned char*)syncInputs;
	for (int p = 0; p < nPlayers && p < g_nPlayers; p++) {
		const unsigned char* pin = in + (size_t)p * g_nInputBytes;
		const FbnPlayerMap* m = &g_map[p];
		for (int b = 0; b < m->nBits; b++) {
			if (!m->pVal[b]) continue;
			*m->pVal[b] = (UINT8)((pin[b >> 3] >> (b & 7)) & 1);
		}
	}
}

static int host_step_frame(const void* syncInputs, int nPlayers,
                           int /*disconnectFlags*/, int bRollback, void* /*user*/)
{
	applyInputs(syncInputs, nPlayers);

	if (bRollback) {
		// Silent re-simulation - mirror FBNeo's RunAhead frame (run.cpp).
		// Drivers gate rendering on pBurnDraw, so NULL == no draw, pitch unused.
		UINT8* savedDraw  = pBurnDraw;
		INT16* savedSound = pBurnSoundOut;
		pBurnDraw          = NULL;
		pBurnSoundOut      = NULL;
		bBurnRunAheadFrame = 1;

		BurnDrvFrame();

		bBurnRunAheadFrame = 0;
		pBurnDraw          = savedDraw;
		pBurnSoundOut      = savedSound;
	} else if (g_liveDraw) {
		// Live drawn frame: go through the blitter, which sets nBurnPitch,
		// calls BurnDrvFrame() itself (via VidFrameCallback) and presents to
		// screen. Bare BurnDrvFrame() here would render with pitch 0 -> black.
		if (VidFrame()) {          // blitter unavailable this frame -> stock fallback
			pBurnDraw = NULL;
			BurnDrvFrame();
		}
	} else {
		// Live but frame-skipped (libggpo catching up): advance, don't draw.
		pBurnDraw = NULL;
		BurnDrvFrame();
	}
	return 0;
}

static void host_on_event(const GgpoBridgeEvent* ev, void* /*user*/)
{
	const char* n = "?";
	switch (ev->code) {
	case GGPO_BRIDGE_EV_CONNECTED:              n = "connected";     break;
	case GGPO_BRIDGE_EV_SYNCHRONIZING:          n = "synchronizing"; break;
	case GGPO_BRIDGE_EV_SYNCHRONIZED:           n = "synchronized";  break;
	case GGPO_BRIDGE_EV_RUNNING:                n = "running";       break;
	case GGPO_BRIDGE_EV_DISCONNECTED:           n = "disconnected";  break;
	case GGPO_BRIDGE_EV_CONNECTION_INTERRUPTED: n = "interrupted";   break;
	case GGPO_BRIDGE_EV_CONNECTION_RESUMED:     n = "resumed";       break;
	case GGPO_BRIDGE_EV_TIMESYNC:               n = "timesync";      break;
	}
	RbfLog("ggpo: %s (player=%d a=%d b=%d)", n, ev->player, ev->a, ev->b);
	bprintf(PRINT_IMPORTANT, _T("[fbneo_host] ggpo: %S (player=%d a=%d b=%d)\n"), n, ev->player, ev->a, ev->b);
	// TODO: forward to the gRPC agent event sink (via the command/event queue).
}

// Side 1 only: feed the spectator relay. Called once per frame that libggpo
// can no longer revise, already in order, so this just queues bytes.
static void host_confirmed_inputs(int frame, const void* inputs, int nBytes, void* /*user*/)
{
	RelayPublishInputs(frame, inputs, nBytes);
}

static void fillHostVtbl(void)
{
	memset(&g_hostVtbl, 0, sizeof(g_hostVtbl));
	g_hostVtbl.poll_local_input = host_poll_local_input;
	g_hostVtbl.step_frame       = host_step_frame;
	g_hostVtbl.on_event         = host_on_event;
	g_hostVtbl.log_state        = NULL;
	// Only the match host broadcasts - one publisher per match, and side 1 is
	// the one both peers agree on without any extra negotiation.
	g_hostVtbl.on_confirmed_inputs =
		(g_localPlayer == 1 && g_cfg.szRelayIp[0] && g_cfg.nRelayPort && g_cfg.szMatchId[0])
		? host_confirmed_inputs : NULL;
	g_hostVtbl.user             = NULL;
}

// ===========================================================================
//  Lifecycle
// ===========================================================================
static int startCommon(const FbnHostConfig* cfg)
{
	if (!cfg)                                                   return -1;
	if (cfg->nPlayers < 2 || cfg->nPlayers > GGPO_BRIDGE_MAX_PLAYERS) return -1;
	if (cfg->nLocalPlayer < 1 || cfg->nLocalPlayer > cfg->nPlayers)   return -1;
	if (!bDrvOkay)                                             return -1; // driver must be initialised

	g_cfg         = *cfg;
	g_nPlayers    = cfg->nPlayers;
	g_localPlayer = cfg->nLocalPlayer;
	g_inputPlayer = (cfg->nInputPlayer >= 1 && cfg->nInputPlayer <= cfg->nPlayers) ? cfg->nInputPlayer : 1;
	g_ringReady   = 0;
	g_watch       = 0;
	g_watchFrames = 0;

	// Only side 1 broadcasts, and only if the lobby gave us a relay to dial.
	g_relayPending = (g_localPlayer == 1 && g_cfg.szRelayIp[0] &&
	                  g_cfg.nRelayPort && g_cfg.szMatchId[0]) ? 1 : 0;

	// A game we have no addresses for is not an error - the match runs, it
	// just goes unscored.
	MatchScoreStart(RbfLogLine);
	OverlayShow(g_cfg.szP1Name, g_cfg.szP2Name);

	return buildInputMap();
}

static void toBridgeConfig(GgpoBridgeConfig* bc)
{
	memset(bc, 0, sizeof(*bc));
	strncpy(bc->szGameId, g_cfg.szGameId, sizeof(bc->szGameId) - 1);
	bc->nPlayers       = g_nPlayers;
	bc->nInputBytes    = g_nInputBytes;
	bc->nLocalPlayer   = g_localPlayer;
	bc->nLocalPort     = g_cfg.nLocalPort;
	strncpy(bc->szRemoteIp, g_cfg.szRemoteIp, sizeof(bc->szRemoteIp) - 1);
	bc->nRemotePort    = g_cfg.nRemotePort;
	bc->nFrameDelay    = (g_cfg.nFrameDelay > 0) ? g_cfg.nFrameDelay : 2;
	bc->nIdleTimeoutMs = 0;
	bc->nStateSlots    = g_cfg.nStateSlots;
}

int FbnHostStart(const FbnHostConfig* cfg)
{
	if (g_active) FbnHostStop();
	if (startCommon(cfg) != 0) {
		RbfLog("FbnHostStart: startCommon failed (bad cfg / driver not ready / no digital inputs)");
		return -1;
	}

	fillHostVtbl();
	GgpoBridgeConfig bc;
	toBridgeConfig(&bc);

	RbfLog("---- starting session: game=%s  side=P%d/%d  controls=P%d  bind :%d  peer %s:%d  delay %d ----",
	       bc.szGameId, bc.nLocalPlayer, bc.nPlayers, g_inputPlayer,
	       bc.nLocalPort, bc.szRemoteIp, bc.nRemotePort, bc.nFrameDelay);

	// Ask the router for an explicit mapping first. This is what turns a home
	// router with a per-destination NAT back into a direct connection: an
	// explicit mapping is not per-destination, so the endpoint the rendezvous
	// observes is reachable from anywhere. Useless against a carrier NAT - you
	// cannot UPnP your operator - and those pairs still go to the relay.
	if (PortMapOpenUdp(bc.nLocalPort, RbfLogLine) != PORT_MAP_OK)
		RbfLog("upnp: no mapping - relying on hole punching alone");

	// NAT hole punching, if the lobby gave us a rendezvous. Must run BEFORE the
	// GGPO session so it can use the very port libggpo is about to bind.
	if (g_cfg.szPunchIp[0] && g_cfg.nPunchPort && g_cfg.szMatchId[0]) {
		NatPunchPlan plan;
		int rc = NatPunchResolvePeer(g_cfg.szPunchIp, g_cfg.nPunchPort, g_cfg.nGameRelayPort,
		                             g_cfg.szMatchId, bc.nLocalPlayer, bc.nLocalPort,
		                             &plan, 15000, RbfLogLine);
		if (rc != NAT_PUNCH_OK) {
			RbfLog("nat punch failed (%d) - falling back to %s:%d (LAN / port-forward)",
			       rc, bc.szRemoteIp, bc.nRemotePort);
		} else if (plan.mode == NAT_PUNCH_MODE_LAN) {
			// Same router: the lobby already handed us the peer's LAN address,
			// which beats asking that router to hairpin its own WAN address.
			RbfLog("nat punch: same NAT - keeping the LAN peer %s:%d",
			       bc.szRemoteIp, bc.nRemotePort);
		} else if (plan.mode == NAT_PUNCH_MODE_RELAY && g_cfg.nGameRelayPort) {
			// Point libggpo at the relay and let it believe that IS the peer.
			// Every packet it receives then comes from that one address, so its
			// own peer-address check passes and no libggpo change is needed.
			RbfLog("relaying the match through %s:%d (symmetric NAT)",
			       g_cfg.szPunchIp, g_cfg.nGameRelayPort);
			strncpy(bc.szRemoteIp, g_cfg.szPunchIp, sizeof(bc.szRemoteIp) - 1);
			bc.szRemoteIp[sizeof(bc.szRemoteIp) - 1] = 0;
			bc.nRemotePort = g_cfg.nGameRelayPort;
		} else {
			RbfLog("nat punch OK: peer %s:%d (lobby had said %s:%d)",
			       plan.szPeerIp, plan.nPeerPort, bc.szRemoteIp, bc.nRemotePort);
			strncpy(bc.szRemoteIp, plan.szPeerIp, sizeof(bc.szRemoteIp) - 1);
			bc.szRemoteIp[sizeof(bc.szRemoteIp) - 1] = 0;
			bc.nRemotePort = plan.nPeerPort;
		}
	}

	int r = GgpoBridgeStart(&bc, &g_hostVtbl);
	if (r < 0) {
		RbfLog("GgpoBridgeStart failed: %d (ggpo err %d)", r, GgpoBridgeLastError());
		bprintf(PRINT_ERROR, _T("[fbneo_host] GgpoBridgeStart failed: %d\n"), r);
		return r;
	}

	g_active = 1;
	RbfLog("session up.");
	bprintf(PRINT_IMPORTANT,
	        _T("[fbneo_host] session up: %d players, local P%d, %d input byte(s)/player, delay %d, ")
	        _T("bind :%d, peer %S:%d.\n"),
	        g_nPlayers, g_localPlayer, g_nInputBytes, bc.nFrameDelay,
	        bc.nLocalPort, bc.szRemoteIp, bc.nRemotePort);
	return 0;
}

int FbnHostStartSyncTest(const FbnHostConfig* cfg, int nCheckDistance)
{
	if (g_active) FbnHostStop();
	if (startCommon(cfg) != 0) return -1;

	fillHostVtbl();
	GgpoBridgeConfig bc;
	toBridgeConfig(&bc);

	int r = GgpoBridgeStartSyncTest(&bc, &g_hostVtbl, nCheckDistance);
	if (r < 0) {
		bprintf(PRINT_ERROR, _T("[fbneo_host] GgpoBridgeStartSyncTest failed: %d\n"), r);
		return r;
	}

	g_active = 1;
	bprintf(PRINT_IMPORTANT, _T("[fbneo_host] synctest up (check distance %d).\n"), nCheckDistance);
	return 0;
}

// ===========================================================================
//  Spectating
// ===========================================================================
int FbnHostStartWatch(const FbnWatchConfig* cfg)
{
	if (g_active) FbnHostStop();
	if (!cfg || !cfg->szRelayIp[0] || !cfg->nRelayPort || !cfg->szMatchId[0]) return -1;
	if (!bDrvOkay) return -1;

	RelayStreamInfo info;
	memset(&info, 0, sizeof(info));

	int rc = RelayWatchStart(cfg->szRelayIp, cfg->nRelayPort, cfg->szMatchId, 20000, RbfLogLine);
	if (rc != RELAY_OK) {
		RbfLog("watch: could not join match %s (%d)", cfg->szMatchId, rc);
		bprintf(PRINT_ERROR, _T("[fbneo_host] watch: relay refused (%d).\n"), rc);
		return -1;
	}
	RelayWatchInfo(&info);

	g_nPlayers    = info.nPlayers;
	g_localPlayer = 1;
	g_inputPlayer = 1;
	g_boundMask   = 0;
	g_boundLogged = 0;
	g_ringReady   = 0;
	g_watchFrames = 0;

	if (buildInputMap() != 0) {
		RelayWatchStop();
		return -1;
	}
	if (g_nInputBytes != info.nInputBytes) {
		// Different FBNeo build or driver revision on the two ends: the bitmask
		// layouts would not line up and the replay would be garbage.
		RbfLog("watch: input size mismatch (stream %d, local %d) - refusing",
		       info.nInputBytes, g_nInputBytes);
		bprintf(PRINT_ERROR, _T("[fbneo_host] watch: input size mismatch - update your build.\n"));
		RelayWatchStop();
		return -1;
	}

	g_watch  = 1;
	g_active = 1;
	// A viewer knows the names from the stream header, so the overlay works
	// there too - and knowing who is playing is half the point of watching.
	OverlayShow(info.szP1, info.szP2);
	RbfLog("watching %s: %s vs %s (%s)", cfg->szMatchId, info.szP1, info.szP2, info.szGame);
	bprintf(PRINT_IMPORTANT, _T("[fbneo_host] watching %S vs %S.\n"), info.szP1, info.szP2);
	return 0;
}

int FbnHostIsWatching(void) { return g_watch; }

// One replayed frame. bDraw == 0 is the silent catch-up path: no video and no
// audio, which is what makes fast-forward fast.
static int watchStepOne(int bDraw)
{
	unsigned char in[GGPO_BRIDGE_MAX_PLAYERS * GGPO_BRIDGE_MAX_INPUT_BYTES];
	const int n = g_nPlayers * g_nInputBytes;

	int r = RelayWatchNext(in, n);
	if (r == 0) return 0;     // the host has not sent this frame yet
	if (r < 0)  return -1;    // stream finished and drained

	applyInputs(in, g_nPlayers);

	if (bDraw) {
		if (VidFrame()) { pBurnDraw = NULL; BurnDrvFrame(); }
	} else {
		UINT8* savedDraw  = pBurnDraw;
		INT16* savedSound = pBurnSoundOut;
		pBurnDraw          = NULL;
		pBurnSoundOut      = NULL;
		bBurnRunAheadFrame = 1;
		BurnDrvFrame();
		bBurnRunAheadFrame = 0;
		pBurnDraw          = savedDraw;
		pBurnSoundOut      = savedSound;
	}
	g_watchFrames++;
	return 1;
}

static int watchRunFrame(int bDraw)
{
	if (!g_ringReady) {
		int rc = StateRingInit(0);
		if (rc != STATE_RING_OK) {
			RbfLog("watch: StateRingInit failed: %d", rc);
			return -1;
		}

		int nState = 0;
		const void* pState = RelayWatchState(&nState);
		if (!pState || nState <= 0) {
			RbfLog("watch: the broadcast carried no opening state");
			return -1;
		}

		// Loading the host's state is what guarantees we start from exactly the
		// machine they did - NVRAM, EEPROM, boot moment and all - instead of
		// hoping two fresh boots happen to agree.
		rc = StateRingLoad(0, pState, nState);
		if (rc != STATE_RING_OK) {
			RbfLog("watch: could not load the opening state: %d (stream %d bytes, local slot %d)",
			       rc, nState, StateRingSlotSize());
			bprintf(PRINT_ERROR,
			        _T("[fbneo_host] watch: save state mismatch - both sides need the same build.\n"));
			return -1;
		}
		g_ringReady = 1;
		RbfLog("watch: opening state loaded (%d bytes), replaying.", nState);
	}

	// Behind the broadcast? Burn through the backlog silently first.
	int guard = FBN_WATCH_BURST;
	while (RelayWatchPending() > FBN_WATCH_CUSHION && guard-- > 0)
		if (watchStepOne(0) <= 0) break;

	int r = watchStepOne(bDraw);
	if (r < 0) {
		RbfLog("watch: broadcast ended after %lld frames.", g_watchFrames);
		return -1;
	}
	return r;
}

// Start broadcasting this match. Runs once, on the first frame after the state
// ring is up: at that instant the emulator is in exactly the state libggpo is
// about to save as frame 0, which is what a viewer needs to start from.
static void startPublishing(void)
{
	g_relayPending = 0;

	void* pState = NULL;
	int   nState = 0;
	if (StateRingSave(0, &pState, &nState, NULL) != STATE_RING_OK || !pState) {
		RbfLog("relay: could not capture the opening state - match will not be watchable");
		return;
	}

	RelayStreamInfo info;
	memset(&info, 0, sizeof(info));
	strncpy(info.szGame, g_cfg.szGameId, sizeof(info.szGame) - 1);
	strncpy(info.szP1, g_cfg.szP1Name[0] ? g_cfg.szP1Name : "P1", sizeof(info.szP1) - 1);
	strncpy(info.szP2, g_cfg.szP2Name[0] ? g_cfg.szP2Name : "P2", sizeof(info.szP2) - 1);
	info.nPlayers    = g_nPlayers;
	info.nInputBytes = g_nInputBytes;
	info.nStateLen   = nState;

	int rc = RelayPublishStart(g_cfg.szRelayIp, g_cfg.nRelayPort, g_cfg.szMatchId,
	                           &info, pState, nState, RbfLogLine);
	if (rc != RELAY_OK)
		RbfLog("relay: publish start failed (%d) - match will not be watchable", rc);
}

void FbnHostStop(void)
{
	if (g_active) {
		if (g_watch) {
			RelayWatchStop();
			RbfLog("watch closed after %lld frames.", g_watchFrames);
		} else {
			GgpoBridgeClose();
			RelayPublishStop();
			PortMapClose();
			// Before the log line that closes the session, so the result and
			// the match it belongs to sit together in the file.
			MatchScoreStop(RbfLogLine);
			RbfLog("session closed.");
		}
		g_active = 0;
		bprintf(PRINT_IMPORTANT, _T("[fbneo_host] session closed.\n"));
	}
	OverlayHide();
	g_watch        = 0;
	g_relayPending = 0;
	g_ringReady    = 0;
}

int FbnHostIsActive(void) { return g_active; }

int FbnHostRunFrame(int bDraw)
{
	if (!g_active) return -1;
	g_liveDraw = bDraw;

	if (g_watch) return watchRunFrame(bDraw);

	// Deferred: the driver has now executed >=1 frame, so its volatile state
	// size is stable and safe to lock into the ring.
	if (!g_ringReady) {
		int rc = StateRingInit(g_cfg.nStateSlots);
		if (rc != STATE_RING_OK) {
			RbfLog("StateRingInit failed: %d", rc);
			bprintf(PRINT_ERROR, _T("[fbneo_host] StateRingInit failed: %d\n"), rc);
			return -1;
		}
		g_ringReady = 1;
		RbfLog("state ring: %d slots x %d bytes.", StateRingSlotCount(), StateRingSlotSize());

		// The emulator is now in exactly the state libggpo will save as frame 0,
		// so this is the one moment a viewer can be handed a starting point.
		if (g_relayPending) startPublishing();
	}

	int r = GgpoBridgeTick();
	if (r == GGPO_BRIDGE_OK)      return 1;
	if (r == GGPO_BRIDGE_SKIPPED) return 0;

	RbfLog("ggpo tick FATAL (bridge %d, ggpo err %d) - dropping to offline.", r, GgpoBridgeLastError());
	bprintf(PRINT_ERROR,
	        _T("[fbneo_host] ggpo tick fatal (bridge %d, ggpo err %d) - dropping to offline.\n"),
	        r, GgpoBridgeLastError());
	return -1;
}

int       FbnHostInputBytesPerPlayer(void) { return g_nInputBytes; }
long long FbnHostFrameCount(void)          { return GgpoBridgeFrameCount(); }
int       FbnHostLastError(void)           { return GgpoBridgeLastError(); }
