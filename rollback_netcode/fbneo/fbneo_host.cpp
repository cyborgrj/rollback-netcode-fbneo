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

// plain callback shape for modules that take a logger (nat_punch)
static void RbfLogLine(const char* s) { RbfLog("%s", s ? s : ""); }

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

// GetInput(true) has already written THIS machine's reading into every driver
// input byte. Pack the local player's digital slice into a little-endian bitmask.
static void host_poll_local_input(void* out, int nInputBytes, void* /*user*/)
{
	memset(out, 0, nInputBytes);
	// FBNeo binds the physical controls to ONE driver player (P1 by default),
	// whichever side we are in the match - so "my" input always comes from that
	// player's bytes, never from g_localPlayer's.
	const FbnPlayerMap* m = &g_map[g_inputPlayer - 1];
	unsigned char* o = (unsigned char*)out;
	for (int b = 0; b < m->nBits; b++)
		if (m->pVal[b] && *m->pVal[b]) o[b >> 3] |= (unsigned char)(1 << (b & 7));
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

static void fillHostVtbl(void)
{
	memset(&g_hostVtbl, 0, sizeof(g_hostVtbl));
	g_hostVtbl.poll_local_input = host_poll_local_input;
	g_hostVtbl.step_frame       = host_step_frame;
	g_hostVtbl.on_event         = host_on_event;
	g_hostVtbl.log_state        = NULL;
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

	// NAT hole punching, if the lobby gave us a rendezvous. Must run BEFORE the
	// GGPO session so it can use the very port libggpo is about to bind.
	if (g_cfg.szPunchIp[0] && g_cfg.nPunchPort && g_cfg.szMatchId[0]) {
		char szPeer[64] = "";
		unsigned short nPeer = 0;
		int rc = NatPunchResolvePeer(g_cfg.szPunchIp, g_cfg.nPunchPort, g_cfg.szMatchId,
		                             bc.nLocalPlayer, bc.nLocalPort,
		                             szPeer, sizeof(szPeer), &nPeer, 15000, RbfLogLine);
		if (rc == NAT_PUNCH_OK) {
			RbfLog("nat punch OK: peer %s:%d (lobby had said %s:%d)",
			       szPeer, nPeer, bc.szRemoteIp, bc.nRemotePort);
			strncpy(bc.szRemoteIp, szPeer, sizeof(bc.szRemoteIp) - 1);
			bc.szRemoteIp[sizeof(bc.szRemoteIp) - 1] = 0;
			bc.nRemotePort = nPeer;
		} else {
			RbfLog("nat punch failed (%d) - falling back to %s:%d (LAN / port-forward)",
			       rc, bc.szRemoteIp, bc.nRemotePort);
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

void FbnHostStop(void)
{
	if (g_active) {
		GgpoBridgeClose();
		g_active = 0;
		RbfLog("session closed.");
		bprintf(PRINT_IMPORTANT, _T("[fbneo_host] session closed.\n"));
	}
	g_ringReady = 0;
}

int FbnHostIsActive(void) { return g_active; }

int FbnHostRunFrame(int bDraw)
{
	if (!g_active) return -1;
	g_liveDraw = bDraw;

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
