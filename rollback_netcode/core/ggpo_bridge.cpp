// ---------------------------------------------------------------------------
// ggpo_bridge.cpp - see ggpo_bridge.h for the contract.
//
// Build needs libggpo's public header on the include path:
//   -I <libggpo>/src/include        (provides ggponet.h)
// and links against the libggpo static/shared lib.
// ---------------------------------------------------------------------------
#if defined(_WIN32)
#include <winsock2.h>   // must precede <windows.h>; libggpo/FBNeo never call WSAStartup
#endif

#include "ggpo_bridge.h"
#include "state_ring.h"

#include "ggponet.h"

#include <stdio.h>
#include <string.h>

#if defined(_WIN32)
static int g_wsaUp = 0;
static void RbfWsaStartup(void)
{
	if (!g_wsaUp) {
		WSADATA wsad;
		if (WSAStartup(MAKEWORD(2, 2), &wsad) == 0) g_wsaUp = 1;
	}
}
static void RbfWsaCleanup(void)
{
	if (g_wsaUp) { WSACleanup(); g_wsaUp = 0; }
}
#else
static void RbfWsaStartup(void) {}
static void RbfWsaCleanup(void) {}
#endif

// ---- module state (emulation thread only) ------------------------------------
static GGPOSession*      g_session = NULL;
static GGPOSessionCallbacks g_cb;
static GgpoBridgeConfig  g_cfg;
static GgpoBridgeHost    g_host;

static GGPOPlayerHandle  g_handle[GGPO_BRIDGE_MAX_PLAYERS];  // index 0..nPlayers-1 => player_num 1..nPlayers
static GGPOPlayerHandle  g_localHandle = GGPO_INVALID_HANDLE;

static int        g_running    = 0;
static int        g_syncTest   = 0;
static int        g_lastError  = GGPO_OK;
static long long  g_frameCount = 0;

// Sync watchdog: if we never advance a live frame within this many ticks the
// peers never synchronised (unreachable / wrong address) - bail out so the
// caller can drop to offline instead of a frozen black screen forever.
static long long  g_ticksSinceStart = 0;
static int        g_everAdvanced    = 0;
#define GGPO_BRIDGE_SYNC_WATCHDOG_TICKS 900   /* ~15s at 60fps */

// Scratch for synchronize_input. Sized for the worst case; never on the heap.
static unsigned char g_syncBuf[GGPO_BRIDGE_MAX_PLAYERS * GGPO_BRIDGE_MAX_INPUT_BYTES];

// ---- confirmed-input tap (spectator relay) --------------------------------
// libggpo never rolls back further than GGPO_MAX_PREDICTION_FRAMES, so once the
// simulation is that far past a frame its inputs can no longer change. We keep
// a short ring of what each recent frame actually ran with - rollback steps
// overwrite their frames with the corrected values - and hand frames off in
// order once they fall outside the window.
#define GGPO_BRIDGE_CONFIRM_LAG   (GGPO_MAX_PREDICTION_FRAMES + 2)
#define GGPO_BRIDGE_CONFIRM_RING  32   /* power of two, comfortably > the lag */

static unsigned char g_confInputs[GGPO_BRIDGE_CONFIRM_RING][GGPO_BRIDGE_MAX_PLAYERS * GGPO_BRIDGE_MAX_INPUT_BYTES];
static int  g_confFrame[GGPO_BRIDGE_CONFIRM_RING];
static int  g_ggpoFrame     = 0;    // libggpo's _framecount, mirrored from cb_save_game_state
static int  g_publishedUpTo = -1;

// ---- helpers ---------------------------------------------------------------
static int syncBufBytes(void)
{
	return g_cfg.nInputBytes * g_cfg.nPlayers;
}

static void emitEvent(GgpoBridgeEventCode code, int player, int a, int b)
{
	if (!g_host.on_event) return;
	GgpoBridgeEvent ev;
	ev.code = code;
	ev.player = player;
	ev.a = a;
	ev.b = b;
	g_host.on_event(&ev, g_host.user);
}

static void recordInputs(int frame)
{
	if (frame < 0) return;
	const int i = frame & (GGPO_BRIDGE_CONFIRM_RING - 1);
	g_confFrame[i] = frame;
	memcpy(g_confInputs[i], g_syncBuf, syncBufBytes());
}

// Hand over every frame that has fallen outside libggpo's rollback window and
// has not been handed over yet. Strictly in order: a consumer replaying the
// stream cannot tolerate a gap, so a missing frame stops the tap instead.
static void publishConfirmed(int liveFrame)
{
	if (!g_host.on_confirmed_inputs) return;
	const int upTo = liveFrame - GGPO_BRIDGE_CONFIRM_LAG;
	for (int f = g_publishedUpTo + 1; f <= upTo; f++) {
		const int i = f & (GGPO_BRIDGE_CONFIRM_RING - 1);
		if (g_confFrame[i] != f) break;   /* evicted - never publish a gap */
		g_host.on_confirmed_inputs(f, g_confInputs[i], syncBufBytes(), g_host.user);
		g_publishedUpTo = f;
	}
}

// Read synchronized inputs for the frame libggpo is currently on and run one
// core step. Shared by the live path and the rollback callback.
static int stepOnce(int bRollback)
{
	int disconnectFlags = 0;
	GGPOErrorCode r = ggpo_synchronize_input(g_session, g_syncBuf, syncBufBytes(), &disconnectFlags);
	if (!GGPO_SUCCEEDED(r)) {
		g_lastError = r;
		return GGPO_BRIDGE_ERR_GGPO;
	}

	// Which frame these inputs belong to. Live: libggpo is ON that frame right
	// now, and cb_save_game_state told us its number. Rollback: our mirror is
	// stale on the first re-simulated step (LoadFrame rewinds _framecount with
	// no save callback), so there we read it back AFTER the advance instead.
	const int liveFrame = g_ggpoFrame;
	if (!bRollback) recordInputs(liveFrame);

	int rc = g_host.step_frame(g_syncBuf, g_cfg.nPlayers, disconnectFlags, bRollback, g_host.user);
	if (rc != 0) return GGPO_BRIDGE_ERR_HOST;

	// Careful: for a LIVE step this call can run a whole rollback inside itself
	// (advance_frame -> DoPoll -> AdjustSimulation), which re-runs recent frames
	// with corrected inputs and overwrites their ring entries. That is exactly
	// what we want, and it is why the live frame is recorded before the call.
	ggpo_advance_frame(g_session);

	if (bRollback) {
		recordInputs(g_ggpoFrame - 1);
	} else {
		g_frameCount++;
		g_everAdvanced = 1;
		publishConfirmed(liveFrame);
	}
	return GGPO_BRIDGE_OK;
}

// ---- GGPO callbacks ------------------------------------------------------------
static bool __cdecl cb_begin_game(const char* /*game*/)
{
	return true;
}

static bool __cdecl cb_save_game_state(unsigned char** buffer, int* len, int* checksum, int frame)
{
	if (!StateRingIsInited()) {
		// The host was supposed to init the ring after warming the driver.
		// Fall back so we don't hard-crash, but make the mistake loud.
		fprintf(stderr, "[ggpo_bridge] WARNING: state_ring not initialised before session; late init.\n");
		if (StateRingInit(g_cfg.nStateSlots) != STATE_RING_OK) return false;
	}

	g_ggpoFrame = frame;   // libggpo saves at the START of `frame`, so this is where it is

	unsigned int chk = 0;
	int rc = StateRingSave(frame, (void**)buffer, len, &chk);
	if (rc != STATE_RING_OK) {
		g_lastError = rc;
		fprintf(stderr, "[ggpo_bridge] StateRingSave(frame=%d) failed: %d\n", frame, rc);
		return false;
	}
	if (checksum) *checksum = (int)chk;
	return true;
}

static bool __cdecl cb_load_game_state(unsigned char* buffer, int len)
{
	int rc = StateRingLoad(0 /* ignored: buffer != NULL */, buffer, len);
	if (rc != STATE_RING_OK) {
		g_lastError = rc;
		fprintf(stderr, "[ggpo_bridge] StateRingLoad failed: %d\n", rc);
		return false;
	}
	return true;
}

static bool __cdecl cb_log_game_state(char* filename, unsigned char* buffer, int len)
{
	if (g_host.log_state) g_host.log_state(filename, buffer, len, g_host.user);
	return true;
}

static void __cdecl cb_free_buffer(void* buffer)
{
	StateRingFree(buffer);   // no-op: pooled memory
}

static bool __cdecl cb_advance_frame(int /*flags*/)
{
	// Rollback re-simulation step. Silent (no draw / no audio) - enforced by
	// the host honouring bRollback == 1.
	stepOnce(/*bRollback=*/1);
	return true;
}

static bool __cdecl cb_on_event(GGPOEvent* info)
{
	switch (info->code) {
	case GGPO_EVENTCODE_CONNECTED_TO_PEER:
		emitEvent(GGPO_BRIDGE_EV_CONNECTED, info->u.connected.player, 0, 0);
		break;
	case GGPO_EVENTCODE_SYNCHRONIZING_WITH_PEER:
		emitEvent(GGPO_BRIDGE_EV_SYNCHRONIZING, info->u.synchronizing.player,
		          info->u.synchronizing.count, info->u.synchronizing.total);
		break;
	case GGPO_EVENTCODE_SYNCHRONIZED_WITH_PEER:
		emitEvent(GGPO_BRIDGE_EV_SYNCHRONIZED, info->u.synchronized.player, 0, 0);
		break;
	case GGPO_EVENTCODE_RUNNING:
		emitEvent(GGPO_BRIDGE_EV_RUNNING, -1, 0, 0);
		break;
	case GGPO_EVENTCODE_DISCONNECTED_FROM_PEER:
		emitEvent(GGPO_BRIDGE_EV_DISCONNECTED, info->u.disconnected.player, 0, 0);
		break;
	case GGPO_EVENTCODE_TIMESYNC:
		emitEvent(GGPO_BRIDGE_EV_TIMESYNC, -1, info->u.timesync.frames_ahead, 0);
		break;
	case GGPO_EVENTCODE_CONNECTION_INTERRUPTED:
		emitEvent(GGPO_BRIDGE_EV_CONNECTION_INTERRUPTED, info->u.connection_interrupted.player,
		          info->u.connection_interrupted.disconnect_timeout, 0);
		break;
	case GGPO_EVENTCODE_CONNECTION_RESUMED:
		emitEvent(GGPO_BRIDGE_EV_CONNECTION_RESUMED, info->u.connection_resumed.player, 0, 0);
		break;
	default:
		break;
	}
	return true;
}

static void fillCallbacks(void)
{
	memset(&g_cb, 0, sizeof(g_cb));
	g_cb.begin_game      = cb_begin_game;
	g_cb.save_game_state = cb_save_game_state;
	g_cb.load_game_state = cb_load_game_state;
	g_cb.log_game_state  = cb_log_game_state;
	g_cb.free_buffer     = cb_free_buffer;
	g_cb.advance_frame   = cb_advance_frame;
	g_cb.on_event        = cb_on_event;
}

// ---- config validation -----------------------------------------------------
static int validateConfig(const GgpoBridgeConfig* cfg, const GgpoBridgeHost* host)
{
	if (!cfg || !host)                                       return GGPO_BRIDGE_ERR_ARG;
	if (!host->poll_local_input || !host->step_frame)        return GGPO_BRIDGE_ERR_ARG;
	if (cfg->nPlayers < 2 || cfg->nPlayers > GGPO_BRIDGE_MAX_PLAYERS) return GGPO_BRIDGE_ERR_ARG;
	if (cfg->nInputBytes < 1 || cfg->nInputBytes > GGPO_BRIDGE_MAX_INPUT_BYTES) return GGPO_BRIDGE_ERR_ARG;
	if (cfg->nLocalPlayer < 1 || cfg->nLocalPlayer > cfg->nPlayers) return GGPO_BRIDGE_ERR_ARG;
	return GGPO_BRIDGE_OK;
}

// ---- common post-start wiring (players, timeouts) -------------------------
static int wirePlayersAndTimeouts(void)
{
	for (int i = 0; i < g_cfg.nPlayers; i++) {
		GGPOPlayer p;
		memset(&p, 0, sizeof(p));
		p.size = sizeof(GGPOPlayer);
		p.player_num = i + 1;

		if ((i + 1) == g_cfg.nLocalPlayer) {
			p.type = GGPO_PLAYERTYPE_LOCAL;
		} else {
			p.type = GGPO_PLAYERTYPE_REMOTE;
			strncpy(p.u.remote.ip_address, g_cfg.szRemoteIp, sizeof(p.u.remote.ip_address) - 1);
			p.u.remote.port = g_cfg.nRemotePort;
		}

		GGPOErrorCode r = ggpo_add_player(g_session, &p, &g_handle[i]);
		if (!GGPO_SUCCEEDED(r)) {
			g_lastError = r;
			return GGPO_BRIDGE_ERR_GGPO;
		}

		if (p.type == GGPO_PLAYERTYPE_LOCAL) {
			g_localHandle = g_handle[i];
			if (g_cfg.nFrameDelay > 0) {
				ggpo_set_frame_delay(g_session, g_localHandle, g_cfg.nFrameDelay);
			}
		}
	}

	if (!g_syncTest) {
		if (g_cfg.nDisconnectTimeoutMs > 0)
			ggpo_set_disconnect_timeout(g_session, g_cfg.nDisconnectTimeoutMs);
		if (g_cfg.nDisconnectNotifyMs > 0)
			ggpo_set_disconnect_notify_start(g_session, g_cfg.nDisconnectNotifyMs);
	}
	return GGPO_BRIDGE_OK;
}

static void resetModuleState(const GgpoBridgeConfig* cfg, const GgpoBridgeHost* host)
{
	g_cfg  = *cfg;
	g_host = *host;
	for (int i = 0; i < GGPO_BRIDGE_MAX_PLAYERS; i++) g_handle[i] = GGPO_INVALID_HANDLE;
	g_localHandle = GGPO_INVALID_HANDLE;
	g_lastError = GGPO_OK;
	g_frameCount = 0;
	g_ticksSinceStart = 0;
	g_everAdvanced = 0;

	g_ggpoFrame     = 0;
	g_publishedUpTo = -1;
	for (int i = 0; i < GGPO_BRIDGE_CONFIRM_RING; i++) g_confFrame[i] = -1;
}

// ---- lifecycle -------------------------------------------------------------
int GgpoBridgeStart(const GgpoBridgeConfig* cfg, const GgpoBridgeHost* host)
{
	if (g_running) GgpoBridgeClose();

	int v = validateConfig(cfg, host);
	if (v != GGPO_BRIDGE_OK) return v;

	resetModuleState(cfg, host);
	g_syncTest = 0;
	fillCallbacks();
	RbfWsaStartup();   // libggpo's udp.cpp assumes Winsock is already up

	GGPOErrorCode r = ggpo_start_session(&g_session, &g_cb, g_cfg.szGameId,
	                                     g_cfg.nPlayers, g_cfg.nInputBytes, g_cfg.nLocalPort);
	if (!GGPO_SUCCEEDED(r)) {
		g_lastError = r;
		g_session = NULL;
		return GGPO_BRIDGE_ERR_GGPO;
	}

	int w = wirePlayersAndTimeouts();
	if (w != GGPO_BRIDGE_OK) {
		ggpo_close_session(g_session);
		g_session = NULL;
		return w;
	}

	g_running = 1;
	return GGPO_BRIDGE_OK;
}

int GgpoBridgeStartSyncTest(const GgpoBridgeConfig* cfg, const GgpoBridgeHost* host, int nCheckDistance)
{
	if (g_running) GgpoBridgeClose();

	int v = validateConfig(cfg, host);
	if (v != GGPO_BRIDGE_OK) return v;
	if (nCheckDistance < 1) nCheckDistance = 1;

	resetModuleState(cfg, host);
	g_syncTest = 1;
	fillCallbacks();
	RbfWsaStartup();

	GGPOErrorCode r = ggpo_start_synctest(&g_session, &g_cb, (char*)g_cfg.szGameId,
	                                      g_cfg.nPlayers, g_cfg.nInputBytes, nCheckDistance);
	if (!GGPO_SUCCEEDED(r)) {
		g_lastError = r;
		g_session = NULL;
		return GGPO_BRIDGE_ERR_GGPO;
	}

	int w = wirePlayersAndTimeouts();
	if (w != GGPO_BRIDGE_OK) {
		ggpo_close_session(g_session);
		g_session = NULL;
		return w;
	}

	g_running = 1;
	return GGPO_BRIDGE_OK;
}

void GgpoBridgeClose(void)
{
	if (g_session) {
		ggpo_close_session(g_session);
		g_session = NULL;
	}
	g_running  = 0;
	g_syncTest = 0;
	g_localHandle = GGPO_INVALID_HANDLE;
	RbfWsaCleanup();
}

int GgpoBridgeIsRunning(void)
{
	return g_running;
}

// ---- per-rendered-frame driver -------------------------------------------
int GgpoBridgeTick(void)
{
	if (!g_running || !g_session) return GGPO_BRIDGE_ERR_STATE;

	// sync watchdog - see note at g_ticksSinceStart
	if (!g_everAdvanced && ++g_ticksSinceStart > GGPO_BRIDGE_SYNC_WATCHDOG_TICKS) {
		g_lastError = GGPO_ERRORCODE_NOT_SYNCHRONIZED;
		return GGPO_BRIDGE_ERR_GGPO;   // caller: FbnHostRunFrame -> -1 -> FbnHostStop (game runs offline)
	}

	// 1. Read our own controls FIRST.
	//
	// This must happen before ggpo_idle(): a rollback runs inside idle, and each
	// re-simulated frame overwrites the driver's input bytes with that frame's
	// synchronized values. Polling afterwards would read the PEER's input back
	// out of the shared bytes and send it as our own - one player ends up
	// driving both characters, and the two machines diverge.
	unsigned char local[GGPO_BRIDGE_MAX_INPUT_BYTES];
	memset(local, 0, sizeof(local));
	g_host.poll_local_input(local, g_cfg.nInputBytes, g_host.user);

	// 2. let libggpo work the network (this is where rollbacks happen)
	ggpo_idle(g_session, g_cfg.nIdleTimeoutMs);

	// 3. hand it to libggpo (may reject while the peers are still catching up)
	GGPOErrorCode r = ggpo_add_local_input(g_session, g_localHandle, local, g_cfg.nInputBytes);
	if (!GGPO_SUCCEEDED(r)) {
		g_lastError = r;
		return GGPO_BRIDGE_SKIPPED;   // e.g. GGPO_ERRORCODE_PREDICTION_THRESHOLD
	}

	// 4. one live step (rollbacks, if any, run synchronously via cb_advance_frame)
	int rc = stepOnce(/*bRollback=*/0);
	if (rc == GGPO_BRIDGE_ERR_GGPO) return GGPO_BRIDGE_SKIPPED; // sync not ready yet
	return rc;
}

// ---- introspection -------------------------------------------------------
int GgpoBridgeGetNetworkStats(int nPlayerHandle, GgpoBridgeNetStats* out)
{
	if (!out) return GGPO_BRIDGE_ERR_ARG;
	memset(out, 0, sizeof(*out));
	if (!g_running || !g_session) return GGPO_BRIDGE_ERR_STATE;

	GGPONetworkStats s;
	memset(&s, 0, sizeof(s));
	GGPOErrorCode r = ggpo_get_network_stats(g_session, nPlayerHandle, &s);
	if (!GGPO_SUCCEEDED(r)) {
		g_lastError = r;
		return GGPO_BRIDGE_ERR_GGPO;
	}

	out->valid                = 1;
	out->ping_ms              = s.network.ping;
	out->send_queue_len       = s.network.send_queue_len;
	out->recv_queue_len       = s.network.recv_queue_len;
	out->kbps_sent            = s.network.kbps_sent;
	out->local_frames_behind  = s.timesync.local_frames_behind;
	out->remote_frames_behind = s.timesync.remote_frames_behind;
	return GGPO_BRIDGE_OK;
}

int       GgpoBridgeLastError(void)  { return g_lastError; }
long long GgpoBridgeFrameCount(void) { return g_frameCount; }
