// ---------------------------------------------------------------------------
// overlay.cpp - see overlay.h.
//
// Text is rendered with GDI into a scratch DIB and composited into the game
// image with the coverage as alpha. That buys a real typeface and real
// anti-aliasing, which matters at this size: the game image is about 384x224,
// so the whole bar is eight or twelve pixels tall before the blitter scales it
// up. A hand-made bitmap font at that size is legible but ugly; an
// anti-aliased one reads cleanly once it is stretched.
//
// GDI is asked for ANTIALIASED_QUALITY specifically, not the default. The
// grid-fitted rendering mangles this typeface below about ten pixels - letters
// lose strokes and words stop being words.
// ---------------------------------------------------------------------------
#include "overlay.h"
#include "match_score.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>
#include <stdio.h>

// ---- Frame Perfect palette (Dracula) --------------------------------------
#define OV_CYAN    0x8BE9FDu   // player one
#define OV_LILAC   0xBD93F9u   // player two
#define OV_WHITE   0xF8F8F2u   // the two scores
#define OV_LABEL   0x6272A4u   // "FT5" - a label, not a score
#define OV_BAR     0x282A36u   // the strip behind it all
#define OV_BAR_A   216         // out of 255: enough to read over any stage

#define OV_MAX_NAME 24
#define OV_SMALL_PX 8
#define OV_LARGE_PX 12

// The scratch DIB only ever holds one run of text at a time.
#define OV_DIB_W 640
#define OV_DIB_H 32

static int  g_on   = 0;          // a session is showing
static int  g_mode = 1;          // 0 off, 1 small, 2 large
static char g_p1[OV_MAX_NAME + 1];
static char g_p2[OV_MAX_NAME + 1];
static int  g_ft = 0;            // first to N; 0 hides the label

// ---- GDI plumbing ---------------------------------------------------------
static HDC      g_dc      = NULL;
static HBITMAP  g_dib     = NULL;
static HBITMAP  g_dibOld  = NULL;
static unsigned char* g_bits = NULL;     // BGRA, top-down
static HFONT    g_font[2] = { NULL, NULL };
static int      g_fontTried = 0;
static int      g_haveFace  = 0;         // 1 when the real typeface loaded
static int      g_claimed   = 0;         // a blitter draws the line itself
static int      g_softLogged = 0;        // said once that we are on the soft path

// Johnny Fever is public domain, so it ships with the emulator; Arial is the
// fallback for a folder where somebody deleted it.
static const char* kFontFiles[] = {
	"Johnny Fever.otf",
	"support\\Johnny Fever.otf",
	"fonts\\Johnny Fever.otf",
};

static void ovInitGdi(void)
{
	if (g_fontTried) return;
	g_fontTried = 1;

	const char* szFace = "Arial";
	for (unsigned i = 0; i < sizeof(kFontFiles) / sizeof(kFontFiles[0]); i++) {
		if (AddFontResourceExA(kFontFiles[i], FR_PRIVATE, NULL) > 0) {
			szFace = "Johnny Fever";
			g_haveFace = 1;
			break;
		}
	}

	HDC screen = GetDC(NULL);
	g_dc = CreateCompatibleDC(screen);
	ReleaseDC(NULL, screen);
	if (!g_dc) return;

	BITMAPINFO bi;
	memset(&bi, 0, sizeof(bi));
	bi.bmiHeader.biSize        = sizeof(bi.bmiHeader);
	bi.bmiHeader.biWidth       = OV_DIB_W;
	bi.bmiHeader.biHeight      = -OV_DIB_H;      // negative: top-down
	bi.bmiHeader.biPlanes      = 1;
	bi.bmiHeader.biBitCount    = 32;
	bi.bmiHeader.biCompression = BI_RGB;

	g_dib = CreateDIBSection(g_dc, &bi, DIB_RGB_COLORS, (void**)&g_bits, NULL, 0);
	if (!g_dib) { DeleteDC(g_dc); g_dc = NULL; return; }
	g_dibOld = (HBITMAP)SelectObject(g_dc, g_dib);

	const int px[2] = { OV_SMALL_PX, OV_LARGE_PX };
	for (int i = 0; i < 2; i++) {
		g_font[i] = CreateFontA(-px[i], 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
		                        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
		                        ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_DONTCARE, szFace);
	}

	SetBkMode(g_dc, OPAQUE);
	SetBkColor(g_dc, RGB(0, 0, 0));
	SetTextColor(g_dc, RGB(255, 255, 255));   // white on black: the pixel IS the coverage
}

// ---- pixels ----------------------------------------------------------------
static void ovUnpack(const unsigned char* px, int bpp, int* r, int* g, int* b)
{
	if (bpp == 2) {
		const unsigned short v = *(const unsigned short*)px;
		*r = ((v >> 11) & 31) << 3;
		*g = ((v >>  5) & 63) << 2;
		*b = ( v        & 31) << 3;
	} else {
		*b = px[0]; *g = px[1]; *r = px[2];
	}
}

static void ovPack(unsigned char* px, int bpp, int r, int g, int b)
{
	if (bpp == 2) {
		*(unsigned short*)px =
			(unsigned short)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
	} else {
		px[0] = (unsigned char)b; px[1] = (unsigned char)g; px[2] = (unsigned char)r;
	}
}

static void ovBlend(unsigned char* img, int w, int h, int bpp, int pitch,
                    int x, int y, unsigned int rgb, int alpha)
{
	if (x < 0 || y < 0 || x >= w || y >= h || alpha <= 0) return;
	unsigned char* px = img + (size_t)y * pitch + (size_t)x * bpp;

	const int sr = (rgb >> 16) & 0xFF, sg = (rgb >> 8) & 0xFF, sb = rgb & 0xFF;
	if (alpha >= 255) { ovPack(px, bpp, sr, sg, sb); return; }

	int dr, dg, db;
	ovUnpack(px, bpp, &dr, &dg, &db);
	ovPack(px, bpp,
	       dr + ((sr - dr) * alpha) / 255,
	       dg + ((sg - dg) * alpha) / 255,
	       db + ((sb - db) * alpha) / 255);
}

static void ovBar(unsigned char* img, int w, int h, int bpp, int pitch,
                  int y0, int y1)
{
	for (int y = y0; y < y1; y++)
		for (int x = 0; x < w; x++)
			ovBlend(img, w, h, bpp, pitch, x, y, OV_BAR, OV_BAR_A);
}

// ---- text ------------------------------------------------------------------
static int ovMeasure(const char* s, int idx, int* pH)
{
	if (!g_dc || !g_font[idx] || !s || !*s) { if (pH) *pH = 0; return 0; }
	HFONT old = (HFONT)SelectObject(g_dc, g_font[idx]);
	SIZE sz; sz.cx = sz.cy = 0;
	GetTextExtentPoint32A(g_dc, s, (int)strlen(s), &sz);
	SelectObject(g_dc, old);
	if (pH) *pH = sz.cy;
	return sz.cx;
}

// Render one run into the scratch DIB and composite it. Re-rendering every
// frame is a handful of microseconds and costs no cache to get wrong.
static int ovText(unsigned char* img, int w, int h, int bpp, int pitch,
                  int x, int y, const char* s, unsigned int rgb, int idx)
{
	if (!g_dc || !g_bits || !g_font[idx] || !s || !*s) return 0;

	int th = 0;
	const int tw = ovMeasure(s, idx, &th);
	if (tw <= 0 || tw > OV_DIB_W || th > OV_DIB_H) return tw;

	RECT rc; rc.left = 0; rc.top = 0; rc.right = tw; rc.bottom = th;
	HFONT old = (HFONT)SelectObject(g_dc, g_font[idx]);
	ExtTextOutA(g_dc, 0, 0, ETO_OPAQUE, &rc, s, (int)strlen(s), NULL);
	SelectObject(g_dc, old);
	GdiFlush();

	for (int ty = 0; ty < th; ty++) {
		const unsigned char* row = g_bits + (size_t)ty * OV_DIB_W * 4;
		for (int tx = 0; tx < tw; tx++) {
			const int a = row[tx * 4 + 1];        // green channel = coverage
			if (a) ovBlend(img, w, h, bpp, pitch, x + tx, y + ty, rgb, a);
		}
	}
	return tw;
}

// ---- public ---------------------------------------------------------------

// Names arrive percent-encoded, because -rbfnet is a comma list that ends at
// the first space and plenty of people have a space in their name. Undo it
// here so the screen shows what the player actually chose to be called.
static void unescape(char* dst, size_t cap, const char* src)
{
	size_t o = 0;
	for (size_t i = 0; src && src[i] && o + 1 < cap; i++) {
		if (src[i] == '%' && src[i + 1] && src[i + 2]) {
			int hi = src[i + 1], lo = src[i + 2];
			hi = (hi >= '0' && hi <= '9') ? hi - '0' : ((hi | 32) >= 'a' && (hi | 32) <= 'f') ? (hi | 32) - 'a' + 10 : -1;
			lo = (lo >= '0' && lo <= '9') ? lo - '0' : ((lo | 32) >= 'a' && (lo | 32) <= 'f') ? (lo | 32) - 'a' + 10 : -1;
			if (hi >= 0 && lo >= 0) { dst[o++] = (char)((hi << 4) | lo); i += 2; continue; }
		}
		dst[o++] = src[i];
	}
	dst[o] = '\0';
}

void OverlayShow(const char* szP1, const char* szP2, int nFirstTo)
{
	memset(g_p1, 0, sizeof(g_p1));
	memset(g_p2, 0, sizeof(g_p2));
	unescape(g_p1, sizeof(g_p1), szP1);
	unescape(g_p2, sizeof(g_p2), szP2);
	g_ft = (nFirstTo > 0 && nFirstTo < 100) ? nFirstTo : 0;
	g_on = (g_p1[0] || g_p2[0]) ? 1 : 0;
}

void OverlayHide(void) { g_on = 0; g_p1[0] = g_p2[0] = '\0'; g_ft = 0; }
int  OverlayIsOn(void) { return g_on; }
int  OverlayMode(void) { return g_mode; }

void OverlayCycle(void)
{
	g_mode = (g_mode + 1) % 3;
}

int OverlayGetLine(OverlayLine* out)
{
	if (!out) return 0;
	if (!g_on || g_mode == 0) return 0;

	memset(out, 0, sizeof(*out));
	out->nMode     = g_mode;
	out->rgbP1     = OV_CYAN;
	out->rgbP2     = OV_LILAC;
	out->rgbScore  = OV_WHITE;
	out->rgbLabel  = OV_LABEL;
	out->rgbBar    = OV_BAR;
	out->nBarAlpha = OV_BAR_A;

	MatchScoreData d;
	if (MatchScoreGet(&d)) {
		snprintf(out->szS1, sizeof(out->szS1), "%d", d.nP1Games);
		snprintf(out->szS2, sizeof(out->szS2), "%d", d.nP2Games);
	}
	if (g_ft) snprintf(out->szFt, sizeof(out->szFt), "FT%d", g_ft);

	strncpy(out->szP1, g_p1[0] ? g_p1 : "P1", sizeof(out->szP1) - 1);
	strncpy(out->szP2, g_p2[0] ? g_p2 : "P2", sizeof(out->szP2) - 1);
	return 1;
}

const char* OverlayFaceName(void)
{
	ovInitGdi();
	return g_haveFace ? "Johnny Fever" : "Arial";
}

// fbneo_host owns rbf-netplay.log; this is the only thing overlay needs from it.
extern "C" void FbnHostLogLine(const char* s);

void OverlayClaim(int bClaimed)
{
	const int now = bClaimed ? 1 : 0;
	if (now != g_claimed) {
		g_claimed = now;
		FbnHostLogLine(now
			? "overlay: o blitter desenha na resolucao da janela (nitido)"
			: "overlay: o blitter devolveu o desenho da barra");
	}
}

int  OverlayClaimed(void) { return g_claimed; }

void OverlayDraw(unsigned char* img, int w, int h, int bpp, int pitch)
{
	if (g_claimed) return;          // a blitter is drawing it properly instead
	if (!img || w <= 0 || h <= 0 || pitch <= 0) return;
	if (bpp != 2 && bpp != 3 && bpp != 4) return;

	OverlayLine L;
	if (!OverlayGetLine(&L)) return;

	ovInitGdi();
	if (!g_dc) return;

	// Say so, once. This path draws into a 384-pixel-wide image and lets the
	// blitter magnify it, which is soft no matter what typeface is used - and
	// soft here is indistinguishable from soft anywhere else when somebody is
	// looking at the screen wondering why the names are hard to read.
	if (!g_softLogged) {
		g_softLogged = 1;
		FbnHostLogLine("overlay: desenhando DENTRO da imagem do jogo "
		               "(este blitter nao desenha a barra) - vai ficar borrado. "
		               "Use o blitter DirectX 9 no menu Video.");
	}

	const int idx = (L.nMode == 2) ? 1 : 0;

	int th = 0;
	const int w1  = ovMeasure(L.szP1, idx, &th);
	const int ws1 = ovMeasure(L.szS1, idx, NULL);
	const int wft = ovMeasure(L.szFt, idx, NULL);
	const int ws2 = ovMeasure(L.szS2, idx, NULL);
	if (th <= 0) return;

	const int gap  = (idx ? OV_LARGE_PX : OV_SMALL_PX) / 2 + 2;
	const int gapL = gap * 2;

	const int barH = th + 2;
	ovBar(img, w, h, bpp, pitch, 0, barH);

	// The middle of the screen is the anchor, not the edges. Right-align the
	// player one group to the left of it and left-align player two to the
	// right, so a thirty-character name pushes its own name outwards and
	// leaves the two scores where they can be read together.
	const int mid   = w / 2;
	const int half  = wft ? (wft / 2 + gapL) : gapL / 2;
	const int y     = 1;

	int x = mid - half - ws1;
	ovText(img, w, h, bpp, pitch, x, y, L.szS1, L.rgbScore, idx);
	ovText(img, w, h, bpp, pitch, x - gap - w1, y, L.szP1, L.rgbP1, idx);

	if (wft) ovText(img, w, h, bpp, pitch, mid - wft / 2, y, L.szFt, L.rgbLabel, idx);

	x = mid + half;
	ovText(img, w, h, bpp, pitch, x, y, L.szS2, L.rgbScore, idx);
	ovText(img, w, h, bpp, pitch, x + ws2 + gap, y, L.szP2, L.rgbP2, idx);
}
