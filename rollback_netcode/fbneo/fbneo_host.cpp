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

#include <stdio.h>
#include <string.h>

// Burner-level flag (declared per-platform in burner_*.h, also in burn/cheat.cpp).
// Re-declared here so this file needs only burnint.h. 1 once BurnDrvInit succeeded.
extern int bDrvOkay;

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
static int           g_localPlayer = 1;
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
	const FbnPlayerMap* m = &g_map[g_localPlayer - 1];
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
		UINT8* savedDraw  = pBurnDraw;
		INT16* savedSound = pBurnSoundOut;
		pBurnDraw          = NULL;
		pBurnSoundOut      = NULL;
		bBurnRunAheadFrame = 1;

		BurnDrvFrame();

		bBurnRunAheadFrame = 0;
		pBurnDraw          = savedDraw;
		pBurnSoundOut      = savedSound;
	} else {
		// Live frame: the RunFrame() caller owns pBurnDraw / pBurnSoundOut.
		BurnDrvFrame();
	}
	return 0;
}

static void host_on_event(const GgpoBridgeEvent* ev, void* /*user*/)
{
	const TCHAR* n = _T("?");
	switch (ev->code) {
	case GGPO_BRIDGE_EV_CONNECTED:              n = _T("connected");     break;
	case GGPO_BRIDGE_EV_SYNCHRONIZING:          n = _T("synchronizing"); break;
	case GGPO_BRIDGE_EV_SYNCHRONIZED:           n = _T("synchronized");  break;
	case GGPO_BRIDGE_EV_RUNNING:                n = _T("running");       break;
	case GGPO_BRIDGE_EV_DISCONNECTED:           n = _T("disconnected");  break;
	case GGPO_BRIDGE_EV_CONNECTION_INTERRUPTED: n = _T("interrupted");   break;
	case GGPO_BRIDGE_EV_CONNECTION_RESUMED:     n = _T("resumed");       break;
	case GGPO_BRIDGE_EV_TIMESYNC:               n = _T("timesync");      break;
	}
	bprintf(PRINT_IMPORTANT, _T("[fbneo_host] ggpo: %s (player=%d a=%d b=%d)\n"), n, ev->player, ev->a, ev->b);
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
	if (startCommon(cfg) != 0) return -1;

	fillHostVtbl();
	GgpoBridgeConfig bc;
	toBridgeConfig(&bc);

	int r = GgpoBridgeStart(&bc, &g_hostVtbl);
	if (r < 0) {
		bprintf(PRINT_ERROR, _T("[fbneo_host] GgpoBridgeStart failed: %d\n"), r);
		return r;
	}

	g_active = 1;
	bprintf(PRINT_IMPORTANT,
	        _T("[fbneo_host] session up: %d players, local P%d, %d input byte(s)/player, delay %d.\n"),
	        g_nPlayers, g_localPlayer, g_nInputBytes, bc.nFrameDelay);
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
		bprintf(PRINT_IMPORTANT, _T("[fbneo_host] session closed.\n"));
	}
	g_ringReady = 0;
}

int FbnHostIsActive(void) { return g_active; }

int FbnHostRunFrame(void)
{
	if (!g_active) return -1;

	// Deferred: the driver has now executed >=1 frame, so its volatile state
	// size is stable and safe to lock into the ring.
	if (!g_ringReady) {
		int rc = StateRingInit(g_cfg.nStateSlots);
		if (rc != STATE_RING_OK) {
			bprintf(PRINT_ERROR, _T("[fbneo_host] StateRingInit failed: %d\n"), rc);
			return -1;
		}
		g_ringReady = 1;
	}

	int r = GgpoBridgeTick();
	if (r == GGPO_BRIDGE_OK)      return 1;
	if (r == GGPO_BRIDGE_SKIPPED) return 0;
	return -1;
}

int       FbnHostInputBytesPerPlayer(void) { return g_nInputBytes; }
long long FbnHostFrameCount(void)          { return GgpoBridgeFrameCount(); }
int       FbnHostLastError(void)           { return GgpoBridgeLastError(); }
