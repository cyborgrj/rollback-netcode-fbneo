// ---------------------------------------------------------------------------
// hud.cpp - see hud.h.
//
// Text drawing is overlay.cpp's, on purpose: same fonts, same compositing,
// same look. What is new here is only what to say and where to put it.
// ---------------------------------------------------------------------------
#include "burnint.h"       // bRunAhead
#include "hud.h"
#include "overlay.h"
#include "fbneo_host.h"
#include "../core/ggpo_bridge.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

// Frame Perfect palette, same as the bar.
#define HUD_TEXT   0xF8F8F2u
#define HUD_BAR    0x282A36u
#define HUD_BAR_A  170        // lighter than the name bar: this sits over play

#define HUD_PAD    3          // pixels around the text inside the panel

static int g_mode = 0;        // 0 hidden, 1 first line only, 2 both

// ---- fps -------------------------------------------------------------------
// Measured here rather than taken from FBNeo, which reports its own number
// through its own on-screen message. Half a second is long enough to be steady
// and short enough to react when a match starts dropping frames.
static double    g_fps = 0.0;
static long long g_fpsFreq = 0;
static long long g_fpsAt = 0;
static int       g_fpsCount = 0;

static void fpsSample(void)
{
	LARGE_INTEGER now;
	if (!g_fpsFreq) {
		LARGE_INTEGER f;
		if (!QueryPerformanceFrequency(&f) || f.QuadPart == 0) return;
		g_fpsFreq = f.QuadPart;
	}
	if (!QueryPerformanceCounter(&now)) return;

	if (!g_fpsAt) { g_fpsAt = now.QuadPart; g_fpsCount = 0; return; }

	g_fpsCount++;
	const long long elapsed = now.QuadPart - g_fpsAt;
	if (elapsed >= g_fpsFreq / 2) {
		g_fps = (double)g_fpsCount * (double)g_fpsFreq / (double)elapsed;
		g_fpsAt = now.QuadPart;
		g_fpsCount = 0;
	}
}

// ---- the numbers -----------------------------------------------------------
static int  g_ping = -1;       // -1 = no session / not measured yet
static int  g_delay = -1;
static int  g_rollback = -1;

static void sample(void)
{
	if (!FbnHostIsActive() || !GgpoBridgeIsRunning()) {
		g_ping = g_delay = g_rollback = -1;
		return;
	}

	g_delay    = GgpoBridgeFrameDelay();
	g_rollback = GgpoBridgeRollbackPeak();

	GgpoBridgeNetStats s;
	g_ping = (GgpoBridgeGetPeerStats(&s) == GGPO_BRIDGE_OK && s.valid) ? s.ping_ms : -1;
}

// "13ms" when we have it, "--" when we do not. A dash keeps the line the same
// shape as when the number is there, so nothing jumps when a session opens.
static void num(char* dst, size_t cap, const char* fmt, int v)
{
	if (v < 0) snprintf(dst, cap, "--");
	else       snprintf(dst, cap, fmt, v);
}

void HudFrame(void)
{
	fpsSample();
	sample();
}

void HudCycle(void)
{
	// Off is the starting point, so the first press shows everything.
	g_mode = (g_mode == 0) ? 2 : (g_mode == 2) ? 1 : 0;
}

int HudMode(void) { return g_mode; }

int HudGetLines(HudLines* out)
{
	if (!out || g_mode == 0) return 0;
	memset(out, 0, sizeof(*out));

	out->nMode     = g_mode;
	out->rgbText   = HUD_TEXT;
	out->rgbBar    = HUD_BAR;
	out->nBarAlpha = HUD_BAR_A;

	char szPing[16], szDelay[16], szRoll[16];
	num(szPing,  sizeof(szPing),  "%dms", g_ping);
	num(szDelay, sizeof(szDelay), "%df",  g_delay);
	num(szRoll,  sizeof(szRoll),  "%df",  g_rollback);

	snprintf(out->szLine1, sizeof(out->szLine1),
	         "ping %s | delay %s | rollback %s", szPing, szDelay, szRoll);

	if (g_mode >= 2) {
		// FBNeo's run ahead is one frame, on or off - there is no 1/2/3 step.
		// And during a match our frame path never reaches FBNeo's run-ahead
		// branch, so it is genuinely off however the menu is set. Saying "off"
		// there is the answer to the question, not a gap in the readout.
		const int bAhead = FbnHostIsActive() ? 0 : (bRunAhead ? 1 : 0);

		char szFps[24];
		snprintf(szFps, sizeof(szFps), "%.1f", g_fps);
		for (char* p = szFps; *p; p++) if (*p == '.') *p = ',';   // pt-BR

		snprintf(out->szLine2, sizeof(out->szLine2),
		         "run ahead %s | %s fps", bAhead ? "on" : "off", szFps);
	}

	return 1;
}

void HudDraw(unsigned char* img, int w, int h, int bpp, int pitch)
{
	if (OverlayClaimed()) return;      // a blitter is drawing it sharp instead
	if (!img || w <= 0 || h <= 0 || pitch <= 0) return;
	if (bpp != 2 && bpp != 3 && bpp != 4) return;

	HudLines L;
	if (!HudGetLines(&L)) return;

	OverlayEnsureGdi();

	int lh = 0;
	const int w1 = OverlayTextWidth(L.szLine1, 0, &lh);
	const int w2 = (L.nMode >= 2) ? OverlayTextWidth(L.szLine2, 0, NULL) : 0;
	if (lh <= 0 || w1 <= 0) return;

	const int lines = (L.nMode >= 2) ? 2 : 1;
	const int panelW = (w1 > w2 ? w1 : w2) + HUD_PAD * 2;
	const int panelH = lh * lines + HUD_PAD * 2;

	// Top right, tucked one pixel in from the corner. The match bar owns the
	// top strip across the middle, so this starts below it when both are up.
	const int barH = OverlayIsOn() && OverlayMode() ? lh + 2 : 0;
	const int x0 = w - panelW - 1;
	const int y0 = barH + 1;

	OverlayFillRect(img, w, h, bpp, pitch, x0, y0, x0 + panelW, y0 + panelH,
	                L.rgbBar, L.nBarAlpha);

	// Right-aligned, so the numbers keep their column as they change width.
	const int right = x0 + panelW - HUD_PAD;
	OverlayTextOut(img, w, h, bpp, pitch, right - w1, y0 + HUD_PAD,
	               L.szLine1, L.rgbText, 0);
	if (L.nMode >= 2)
		OverlayTextOut(img, w, h, bpp, pitch, right - w2, y0 + HUD_PAD + lh,
		               L.szLine2, L.rgbText, 0);
}
