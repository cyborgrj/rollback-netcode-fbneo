// ---------------------------------------------------------------------------
// overlay.cpp - see overlay.h.
//
// A 5x7 bitmap font and a pixel plotter, which is all this needs. No font
// loading, no allocation, nothing that can fail while a match is running.
// ---------------------------------------------------------------------------
#include "overlay.h"
#include "match_score.h"

#include <string.h>
#include <stdio.h>

// Frame Perfect: cyan for player one, lilac for player two, white for the
// numbers so the score reads as one thing across the middle.
#define OV_CYAN   0x00E5FFu
#define OV_LILAC  0xB388FFu
#define OV_WHITE  0xFFFFFFu
#define OV_SHADOW 0x000000u

#define OV_MARGIN_X 6
#define OV_MARGIN_Y 5
#define OV_GLYPH_W  5
#define OV_GLYPH_H  7
#define OV_ADVANCE  6     // glyph plus one column of air

#define OV_MAX_NAME 24

static int  g_on = 0;
static char g_p1[OV_MAX_NAME + 1];
static char g_p2[OV_MAX_NAME + 1];

// ---- font ------------------------------------------------------------------
// One byte per column, bit 0 is the top row. Uppercase only: names are folded,
// which suits an arcade screen and halves the table.
typedef struct { char ch; unsigned char col[OV_GLYPH_W]; } OvGlyph;

static const OvGlyph kFont[] = {
	{ ' ', { 0x00,0x00,0x00,0x00,0x00 } },
	{ '0', { 0x3E,0x51,0x49,0x45,0x3E } },
	{ '1', { 0x00,0x42,0x7F,0x40,0x00 } },
	{ '2', { 0x42,0x61,0x51,0x49,0x46 } },
	{ '3', { 0x21,0x41,0x45,0x4B,0x31 } },
	{ '4', { 0x18,0x14,0x12,0x7F,0x10 } },
	{ '5', { 0x27,0x45,0x45,0x45,0x39 } },
	{ '6', { 0x3C,0x4A,0x49,0x49,0x30 } },
	{ '7', { 0x01,0x71,0x09,0x05,0x03 } },
	{ '8', { 0x36,0x49,0x49,0x49,0x36 } },
	{ '9', { 0x06,0x49,0x49,0x29,0x1E } },
	{ 'A', { 0x7E,0x11,0x11,0x11,0x7E } },
	{ 'B', { 0x7F,0x49,0x49,0x49,0x36 } },
	{ 'C', { 0x3E,0x41,0x41,0x41,0x22 } },
	{ 'D', { 0x7F,0x41,0x41,0x22,0x1C } },
	{ 'E', { 0x7F,0x49,0x49,0x49,0x41 } },
	{ 'F', { 0x7F,0x09,0x09,0x09,0x01 } },
	{ 'G', { 0x3E,0x41,0x49,0x49,0x7A } },
	{ 'H', { 0x7F,0x08,0x08,0x08,0x7F } },
	{ 'I', { 0x00,0x41,0x7F,0x41,0x00 } },
	{ 'J', { 0x20,0x40,0x41,0x3F,0x01 } },
	{ 'K', { 0x7F,0x08,0x14,0x22,0x41 } },
	{ 'L', { 0x7F,0x40,0x40,0x40,0x40 } },
	{ 'M', { 0x7F,0x02,0x0C,0x02,0x7F } },
	{ 'N', { 0x7F,0x04,0x08,0x10,0x7F } },
	{ 'O', { 0x3E,0x41,0x41,0x41,0x3E } },
	{ 'P', { 0x7F,0x09,0x09,0x09,0x06 } },
	{ 'Q', { 0x3E,0x41,0x51,0x21,0x5E } },
	{ 'R', { 0x7F,0x09,0x19,0x29,0x46 } },
	{ 'S', { 0x46,0x49,0x49,0x49,0x31 } },
	{ 'T', { 0x01,0x01,0x7F,0x01,0x01 } },
	{ 'U', { 0x3F,0x40,0x40,0x40,0x3F } },
	{ 'V', { 0x1F,0x20,0x40,0x20,0x1F } },
	{ 'W', { 0x3F,0x40,0x38,0x40,0x3F } },
	{ 'X', { 0x63,0x14,0x08,0x14,0x63 } },
	{ 'Y', { 0x07,0x08,0x70,0x08,0x07 } },
	{ 'Z', { 0x61,0x51,0x49,0x45,0x43 } },
	{ '-', { 0x08,0x08,0x08,0x08,0x08 } },
	{ '_', { 0x40,0x40,0x40,0x40,0x40 } },
	{ '.', { 0x00,0x60,0x60,0x00,0x00 } },
	{ ':', { 0x00,0x36,0x36,0x00,0x00 } },
	{ '!', { 0x00,0x00,0x5F,0x00,0x00 } },
	{ '?', { 0x02,0x01,0x51,0x09,0x06 } },
	{ '*', { 0x14,0x08,0x3E,0x08,0x14 } },
	{ '+', { 0x08,0x08,0x3E,0x08,0x08 } },
	{ '#', { 0x14,0x7F,0x14,0x7F,0x14 } },
	{ '@', { 0x32,0x49,0x79,0x41,0x3E } },
};

static const unsigned char* glyphFor(char c)
{
	if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
	for (unsigned i = 0; i < sizeof(kFont) / sizeof(kFont[0]); i++)
		if (kFont[i].ch == c) return kFont[i].col;
	return kFont[0].col;     // anything we cannot draw becomes a space
}

// ---- pixels ----------------------------------------------------------------
static void plot(unsigned char* p, int w, int h, int bpp, int pitch,
                 int x, int y, unsigned int rgb)
{
	if (x < 0 || y < 0 || x >= w || y >= h) return;

	const unsigned char r = (unsigned char)((rgb >> 16) & 0xFF);
	const unsigned char g = (unsigned char)((rgb >>  8) & 0xFF);
	const unsigned char b = (unsigned char)( rgb        & 0xFF);

	if (bpp == 2) {
		// RGB565, the same packing the Lua overlay reads back.
		unsigned short* row = (unsigned short*)(p + (size_t)y * pitch);
		row[x] = (unsigned short)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
	} else if (bpp == 3 || bpp == 4) {
		unsigned char* px = p + (size_t)y * pitch + (size_t)x * bpp;
		px[0] = b; px[1] = g; px[2] = r;
	}
}

static void drawGlyph(unsigned char* p, int w, int h, int bpp, int pitch,
                      int x, int y, char c, unsigned int rgb)
{
	const unsigned char* col = glyphFor(c);
	for (int cx = 0; cx < OV_GLYPH_W; cx++) {
		for (int cy = 0; cy < OV_GLYPH_H; cy++) {
			if (col[cx] & (1 << cy)) plot(p, w, h, bpp, pitch, x + cx, y + cy, rgb);
		}
	}
}

// Shadow first, then the glyph, so the text stays readable over a bright
// stage instead of dissolving into it.
static void drawText(unsigned char* p, int w, int h, int bpp, int pitch,
                     int x, int y, const char* s, unsigned int rgb)
{
	for (int i = 0; s[i]; i++) {
		drawGlyph(p, w, h, bpp, pitch, x + i * OV_ADVANCE + 1, y + 1, s[i], OV_SHADOW);
	}
	for (int i = 0; s[i]; i++) {
		drawGlyph(p, w, h, bpp, pitch, x + i * OV_ADVANCE, y, s[i], rgb);
	}
}

static int textWidth(const char* s)
{
	int n = (int)strlen(s);
	return n ? (n * OV_ADVANCE - 1) : 0;
}

// ---- public ---------------------------------------------------------------
void OverlayShow(const char* szP1, const char* szP2)
{
	memset(g_p1, 0, sizeof(g_p1));
	memset(g_p2, 0, sizeof(g_p2));
	if (szP1) strncpy(g_p1, szP1, OV_MAX_NAME);
	if (szP2) strncpy(g_p2, szP2, OV_MAX_NAME);
	g_on = (g_p1[0] || g_p2[0]) ? 1 : 0;
}

void OverlayHide(void)  { g_on = 0; g_p1[0] = g_p2[0] = '\0'; }
int  OverlayIsOn(void)  { return g_on; }

void OverlayDraw(unsigned char* p, int w, int h, int bpp, int pitch)
{
	if (!g_on || !p || w <= 0 || h <= 0 || pitch <= 0) return;
	if (bpp != 2 && bpp != 3 && bpp != 4) return;

	// The score only appears once a fight has actually started - before that
	// there is nothing to show and a "0 x 0" would just be wrong.
	MatchScoreData d;
	const int bScore = MatchScoreGet(&d);

	char szLeft[OV_MAX_NAME + 8];
	char szRight[OV_MAX_NAME + 8];

	if (bScore) {
		snprintf(szLeft,  sizeof(szLeft),  "%s %d", g_p1[0] ? g_p1 : "P1", d.nP1Rounds);
		snprintf(szRight, sizeof(szRight), "%d %s", d.nP2Rounds, g_p2[0] ? g_p2 : "P2");
	} else {
		snprintf(szLeft,  sizeof(szLeft),  "%s", g_p1[0] ? g_p1 : "P1");
		snprintf(szRight, sizeof(szRight), "%s", g_p2[0] ? g_p2 : "P2");
	}

	// The score digit is the last character on the left and the first on the
	// right, so each side is drawn in two pieces to get two colours.
	const int y = OV_MARGIN_Y;

	if (bScore) {
		char szNum[8];
		snprintf(szNum, sizeof(szNum), "%d", d.nP1Rounds);

		const char* szName = g_p1[0] ? g_p1 : "P1";
		drawText(p, w, h, bpp, pitch, OV_MARGIN_X, y, szName, OV_CYAN);
		drawText(p, w, h, bpp, pitch,
		         OV_MARGIN_X + textWidth(szName) + OV_ADVANCE, y, szNum, OV_WHITE);

		snprintf(szNum, sizeof(szNum), "%d", d.nP2Rounds);
		szName = g_p2[0] ? g_p2 : "P2";
		const int right = w - OV_MARGIN_X;
		drawText(p, w, h, bpp, pitch, right - textWidth(szName), y, szName, OV_LILAC);
		drawText(p, w, h, bpp, pitch,
		         right - textWidth(szName) - OV_ADVANCE - textWidth(szNum), y, szNum, OV_WHITE);
	} else {
		drawText(p, w, h, bpp, pitch, OV_MARGIN_X, y, szLeft, OV_CYAN);
		drawText(p, w, h, bpp, pitch, w - OV_MARGIN_X - textWidth(szRight), y,
		         szRight, OV_LILAC);
	}
}
