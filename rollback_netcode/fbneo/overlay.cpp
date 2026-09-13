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
#include <stdlib.h>

// fbneo_host owns rbf-netplay.log; this is the only thing overlay needs from it.
extern "C" void FbnHostLogLine(const char* s);

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
// The readout is telemetry, not the scoreboard - it should be readable and
// then get out of the way.
#define OV_HUD_PX   6

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
static HFONT    g_font[3] = { NULL, NULL, NULL };   // 0 small, 1 large, 2 hud
static int      g_fontTried = 0;
static int      g_haveFace  = 0;         // 1 when the real typeface loaded
static int      g_claimed   = 0;         // a blitter draws the line itself
static int      g_softLogged = 0;        // said once that we are on the soft path

// The file ships under a generic name so anybody can swap it: drop your own
// font in beside the emulator as fonte_placar.otf (or .ttf) and it is used.
// Delete it and Arial takes over, which is also what happens if the file turns
// out to be something Windows will not render.
static const char* kFontFiles[] = {
	"fonte_placar.otf",
	"fonte_placar.ttf",
	"support\\fonte_placar.otf",
	"support\\fonte_placar.ttf",
};

// The netcode readout gets its own file. A display face chosen for names on a
// scoreboard is the wrong tool for "ping 13ms | delay 3f" - digits there want
// to be plain and even-width, and whoever picks one should not have to give up
// the other. Same rule: drop it in beside the emulator, or get Arial.
static const char* kMetricFiles[] = {
	"fonte_metricas.ttf",
	"fonte_metricas.otf",
	"support\\fonte_metricas.ttf",
	"support\\fonte_metricas.otf",
};

static char g_face[64] = "Arial";
static char g_metricFace[64] = "Arial";
static int  g_haveMetricFace = 0;

// ---- reading the family name out of the font file -------------------------
//
// AddFontResourceEx loads a file but tells us nothing about it, and CreateFont
// wants a family NAME. With a fixed filename and an arbitrary font inside it,
// the name has to come from the file itself - so this reads the sfnt "name"
// table. Big-endian throughout, which is why every read goes through be16/be32.
static unsigned be16(const unsigned char* p) { return (unsigned)((p[0] << 8) | p[1]); }
static unsigned be32(const unsigned char* p)
{
	return ((unsigned)p[0] << 24) | ((unsigned)p[1] << 16) | ((unsigned)p[2] << 8) | p[3];
}

static int readFaceName(const char* szPath, char* out, size_t cap)
{
	FILE* f = fopen(szPath, "rb");
	if (!f) return 0;

	fseek(f, 0, SEEK_END);
	long len = ftell(f);
	fseek(f, 0, SEEK_SET);
	if (len < 12 || len > 8 * 1024 * 1024) { fclose(f); return 0; }

	unsigned char* buf = (unsigned char*)malloc((size_t)len);
	if (!buf) { fclose(f); return 0; }
	if (fread(buf, 1, (size_t)len, f) != (size_t)len) { free(buf); fclose(f); return 0; }
	fclose(f);

	int ok = 0;
	const unsigned numTables = be16(buf + 4);
	unsigned nameOff = 0, nameLen = 0;

	for (unsigned i = 0; i < numTables; i++) {
		const unsigned rec = 12 + i * 16;
		if (rec + 16 > (unsigned)len) break;
		if (memcmp(buf + rec, "name", 4) == 0) {
			nameOff = be32(buf + rec + 8);
			nameLen = be32(buf + rec + 12);
			break;
		}
	}

	if (nameOff && nameLen && nameOff + 6 <= (unsigned)len) {
		const unsigned count   = be16(buf + nameOff + 2);
		const unsigned strBase = nameOff + be16(buf + nameOff + 4);
		int best = -1;                 // prefer the Windows/Unicode record

		for (unsigned i = 0; i < count; i++) {
			const unsigned r = nameOff + 6 + i * 12;
			if (r + 12 > (unsigned)len) break;
			if (be16(buf + r + 6) != 1) continue;          // nameID 1 = family

			const unsigned plat = be16(buf + r);
			const unsigned sl   = be16(buf + r + 8);
			const unsigned so   = strBase + be16(buf + r + 10);
			if (so + sl > (unsigned)len || sl == 0) continue;

			// Windows records are UTF-16BE; Mac ones are single-byte. Take the
			// low byte either way - these names are ASCII in practice, and a
			// name that is not will simply fail to match later and fall back.
			size_t o = 0;
			if (plat == 3) {
				for (unsigned k = 1; k < sl && o + 1 < cap; k += 2) out[o++] = (char)buf[so + k];
			} else {
				for (unsigned k = 0; k < sl && o + 1 < cap; k++)    out[o++] = (char)buf[so + k];
			}
			out[o] = '\0';

			if (o) { ok = 1; if (plat == 3) { best = 1; break; } }
		}
		(void)best;
	}

	free(buf);
	return ok;
}

// Try each candidate filename and keep the first that both parses and loads.
static int ovLoadFamily(const char* const* files, int n, char* faceOut, size_t cap,
                        const char* szWhat)
{
	char szFound[64] = "";
	for (int i = 0; i < n; i++) {
		if (!readFaceName(files[i], szFound, sizeof(szFound))) continue;
		if (AddFontResourceExA(files[i], FR_PRIVATE, NULL) <= 0) continue;
		strncpy(faceOut, szFound, cap - 1);
		faceOut[cap - 1] = '\0';
		char szMsg[128];
		snprintf(szMsg, sizeof(szMsg), "overlay: usando a fonte de %s", szWhat);
		FbnHostLogLine(szMsg);
		return 1;
	}
	return 0;
}

static HFONT ovMakeFont(int px, const char* szFace)
{
	return CreateFontA(-px, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
	                   DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
	                   ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_DONTCARE, szFace);
}

// GDI never refuses a face - it quietly substitutes one. So ask what we were
// actually given: if it is not what we asked for, the drop-in file was no good
// and Arial is the honest answer.
static int ovFaceAccepted(HFONT f, const char* szWant)
{
	if (!f || !g_dc) return 0;
	HFONT old = (HFONT)SelectObject(g_dc, f);
	char szActual[64] = "";
	GetTextFaceA(g_dc, sizeof(szActual), szActual);
	SelectObject(g_dc, old);
	return _stricmp(szActual, szWant) == 0;
}

static void ovInitGdi(void)
{
	if (g_fontTried) return;
	g_fontTried = 1;

	g_haveFace = ovLoadFamily(kFontFiles, (int)(sizeof(kFontFiles) / sizeof(kFontFiles[0])),
	                          g_face, sizeof(g_face), "fonte_placar");
	g_haveMetricFace = ovLoadFamily(kMetricFiles, (int)(sizeof(kMetricFiles) / sizeof(kMetricFiles[0])),
	                                g_metricFace, sizeof(g_metricFace), "fonte_metricas");

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

	g_font[0] = ovMakeFont(OV_SMALL_PX, g_face);
	g_font[1] = ovMakeFont(OV_LARGE_PX, g_face);
	g_font[2] = ovMakeFont(OV_HUD_PX,   g_metricFace);

	// A font that does not work costs nothing but itself: the score falls back
	// without taking the readout with it, and the other way round.
	if (g_haveFace && !ovFaceAccepted(g_font[0], g_face)) {
		FbnHostLogLine("overlay: o Windows nao aceitou a fonte de fonte_placar - usando Arial");
		g_haveFace = 0;
		strcpy(g_face, "Arial");
		DeleteObject(g_font[0]); g_font[0] = ovMakeFont(OV_SMALL_PX, g_face);
		DeleteObject(g_font[1]); g_font[1] = ovMakeFont(OV_LARGE_PX, g_face);
	}

	if (g_haveMetricFace && !ovFaceAccepted(g_font[2], g_metricFace)) {
		FbnHostLogLine("overlay: o Windows nao aceitou a fonte de fonte_metricas - usando Arial");
		g_haveMetricFace = 0;
		strcpy(g_metricFace, "Arial");
		DeleteObject(g_font[2]); g_font[2] = ovMakeFont(OV_HUD_PX, g_metricFace);
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

	int nP1 = 0, nP2 = 0;
	MatchScoreGames(&nP1, &nP2);
	snprintf(out->szS1, sizeof(out->szS1), "%d", nP1);
	snprintf(out->szS2, sizeof(out->szS2), "%d", nP2);
	if (g_ft) snprintf(out->szFt, sizeof(out->szFt), "FT%d", g_ft);

	strncpy(out->szP1, g_p1[0] ? g_p1 : "P1", sizeof(out->szP1) - 1);
	strncpy(out->szP2, g_p2[0] ? g_p2 : "P2", sizeof(out->szP2) - 1);
	return 1;
}

const char* OverlayFaceName(void)
{
	ovInitGdi();
	return g_face;
}

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

// ---- shared drawing, for hud.cpp ------------------------------------------
void OverlayEnsureGdi(void) { ovInitGdi(); }

static int ovClampSize(int n) { return (n < 0) ? 0 : (n > 2) ? 2 : n; }

int OverlayTextWidth(const char* s, int nSize, int* pHeight)
{
	ovInitGdi();
	return ovMeasure(s, ovClampSize(nSize), pHeight);
}

int OverlayTextOut(unsigned char* img, int w, int h, int bpp, int pitch,
                   int x, int y, const char* s, unsigned int rgb, int nSize)
{
	return ovText(img, w, h, bpp, pitch, x, y, s, rgb, ovClampSize(nSize));
}

const char* OverlayMetricFaceName(void)
{
	ovInitGdi();
	return g_metricFace;
}

void OverlayFillRect(unsigned char* img, int w, int h, int bpp, int pitch,
                     int x0, int y0, int x1, int y1, unsigned int rgb, int alpha)
{
	if (x0 < 0) x0 = 0;
	if (y0 < 0) y0 = 0;
	if (x1 > w) x1 = w;
	if (y1 > h) y1 = h;
	for (int y = y0; y < y1; y++)
		for (int x = x0; x < x1; x++)
			ovBlend(img, w, h, bpp, pitch, x, y, rgb, alpha);
}

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
