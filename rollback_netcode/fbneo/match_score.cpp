// ---------------------------------------------------------------------------
// match_score.cpp - see match_score.h for the contract and where the rule
// came from.
//
// This is a straight port of the reference implementation in
// tools/ProbeAnalyze (the --score mode), which is run against eight recorded
// matches whose results were known in advance. The two must agree; when this
// file changes, re-run:
//
//   ProbeAnalyze --score D:\RBF\rbf-probe-*.rbfp
// ---------------------------------------------------------------------------
#include "burnint.h"     // BurnDrvGetTextA, DRV_NAME
#include "match_score.h"
#include "ram_probe.h"

#include <stdio.h>
#include <stdarg.h>
#include <string.h>

// A knockout has to hold this many live frames before it counts. libggpo
// predicts at most 8 frames ahead, so nothing shorter than this can survive a
// rollback - and a real knockout lasts seconds.
#define SCORE_HOLD_FRAMES 12

// ---- the table ------------------------------------------------------------
// CPU addresses. nFull is the value both sides show at the start of a fight,
// which is also how we detect that a fight started at all.
typedef struct {
	const char*  szGame;
	unsigned int nLifeP1, nLifeP2;
	unsigned int nCharP1, nCharP2;   // 0 = we cannot read that side yet
	int          nCharCount;
	int          nFull;
	int          bBars;              // vsav: no rounds, score is bars lost
} ScoreMap;

static const ScoreMap kMaps[] = {
	{ "sf2ce", 0xFF83E8, 0xFF86E8, 0xFF83D9, 0xFF86D9, 1, 0x0090, 0 },
	{ "sfa2",  0xFF8450, 0xFF8850, 0xFF8482, 0xFF8882, 1, 0x0090, 0 },
	{ "vsav",  0xFF8450, 0xFF8850, 0xFF841D, 0,        1, 0x0120, 1 },
	{ "kof98", 0x108238, 0x108438, 0x10A84E, 0x10A85F, 3, 0x0067, 0 },
};

// ---- state ----------------------------------------------------------------
static const ScoreMap* g_map = NULL;
static MatchScoreData  g_d;
static int  g_p1Hold = 0, g_p2Hold = 0;   // consecutive live frames seen dead
static int  g_p1Down = 0, g_p2Down = 0;   // already counted this knockout
static int  g_p1Low  = 0x7FFFFFFF;
static int  g_p2Low  = 0x7FFFFFFF;
static long long g_frame = 0;
static int  g_matchHold = 0;   // consecutive live frames with both words cleared
static int  g_matchOver = 0;   // this clearing has already been counted
static int  g_firstTo   = 0;   // games that end the session; 0 = free play

// Hand the finished game to whoever won more rounds of it, then start the
// round count over. A drawn game - a double KO at match point, most often -
// counts for neither side, which is what happens on the machine too.
static void awardGame(void)
{
	if (g_map && g_map->bBars) {
		const int p1Bars = g_p2Down ? 2 : ((g_p2Low <= g_map->nFull / 2) ? 1 : 0);
		const int p2Bars = g_p1Down ? 2 : ((g_p1Low <= g_map->nFull / 2) ? 1 : 0);
		if (p1Bars > p2Bars)      g_d.nP1Games++;
		else if (p2Bars > p1Bars) g_d.nP2Games++;
	} else {
		if (g_d.nP1Rounds > g_d.nP2Rounds)      g_d.nP1Games++;
		else if (g_d.nP2Rounds > g_d.nP1Rounds) g_d.nP2Games++;
	}

	g_d.nGames++;
	if (g_firstTo > 0 && (g_d.nP1Games >= g_firstTo || g_d.nP2Games >= g_firstTo))
		g_d.bLimitReached = 1;

	g_d.nP1Rounds = 0;
	g_d.nP2Rounds = 0;
	g_p1Low = g_p2Low = 0x7FFFFFFF;
	g_p1Down = g_p2Down = 0;
	g_p1Hold = g_p2Hold = 0;
	g_d.bStarted = 0;      // the next fight has to announce itself the same way
}

static void score_log(void (*pfn)(const char*), const char* fmt, ...)
{
	if (!pfn) return;
	char buf[512];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(buf, sizeof(buf), fmt, ap);
	va_end(ap);
	buf[sizeof(buf) - 1] = '\0';
	pfn(buf);
}

// Signed 16-bit big-endian, through RamProbeRead8 so the 68000 byte order is
// already undone for us.
static int lifeAt(unsigned int nAddr)
{
	int hi = RamProbeRead8(nAddr);
	int lo = RamProbeRead8(nAddr + 1);
	return (short)((hi << 8) | lo);
}

static void readChars(unsigned int nAddr, int* pOut, int nCount, int* pbHave)
{
	if (!nAddr) return;
	for (int i = 0; i < nCount; i++) pOut[i] = RamProbeRead8(nAddr + i);
	*pbHave = 1;
}

int MatchScoreStart(int nFirstTo, void (*pfnLog)(const char*))
{
	g_map = NULL;
	g_firstTo = (nFirstTo > 0 && nFirstTo < 100) ? nFirstTo : 0;
	memset(&g_d, 0, sizeof(g_d));
	g_p1Hold = g_p2Hold = 0;
	g_p1Down = g_p2Down = 0;
	g_p1Low = g_p2Low = 0x7FFFFFFF;
	g_frame = 0;

	if (RamProbeAttach(pfnLog) != RAM_PROBE_OK)
		return MATCH_SCORE_ERR_NO_RAM;

	const char* szGame = BurnDrvGetTextA(DRV_NAME);
	if (!szGame) szGame = "";
	strncpy(g_d.szGame, szGame, sizeof(g_d.szGame) - 1);

	for (unsigned i = 0; i < sizeof(kMaps) / sizeof(kMaps[0]); i++) {
		if (strcmp(szGame, kMaps[i].szGame) == 0) { g_map = &kMaps[i]; break; }
	}

	if (!g_map) {
		score_log(pfnLog, "score: %s is not a game I know how to read - "
		                  "this match will not be scored", szGame);
		return MATCH_SCORE_ERR_NO_MAP;
	}

	g_d.nCharCount = g_map->nCharCount;
	score_log(pfnLog, "score: reading %s", szGame);
	return MATCH_SCORE_OK;
}

void MatchScoreFrame(void)
{
	if (!g_map) return;

	g_frame++;

	const int l1 = lifeAt(g_map->nLifeP1);
	const int l2 = lifeAt(g_map->nLifeP2);

	// Two full bars at once happens nowhere but the start of a fight.
	if (!g_d.bStarted) {
		if (l1 != g_map->nFull || l2 != g_map->nFull) return;
		g_d.bStarted     = 1;
		g_d.bEverStarted = 1;
		if (!g_d.nStartFrame) g_d.nStartFrame = g_frame;
	}

	// Both alive: the character fields are filled in and not yet overwritten.
	if (l1 > 0 && l2 > 0) {
		readChars(g_map->nCharP1, g_d.nP1Char, g_map->nCharCount, &g_d.bHaveP1Char);
		readChars(g_map->nCharP2, g_d.nP2Char, g_map->nCharCount, &g_d.bHaveP2Char);
	}

	// Zero is the cleared struct, not a life total.
	if (l1 > 0 && l1 < g_p1Low) g_p1Low = l1;
	if (l2 > 0 && l2 < g_p2Low) g_p2Low = l2;

	if (l1 < 0) {
		if (g_p1Hold < SCORE_HOLD_FRAMES) g_p1Hold++;
		if (g_p1Hold >= SCORE_HOLD_FRAMES && !g_p1Down) {
			g_p1Down = 1;
			g_d.nP2Rounds++;
			g_d.nEndFrame = g_frame;
		}
	} else {
		g_p1Hold = 0;
		if (l1 > 0) g_p1Down = 0;
	}

	if (l2 < 0) {
		if (g_p2Hold < SCORE_HOLD_FRAMES) g_p2Hold++;
		if (g_p2Hold >= SCORE_HOLD_FRAMES && !g_p2Down) {
			g_p2Down = 1;
			g_d.nP1Rounds++;
			g_d.nEndFrame = g_frame;
		}
	} else {
		g_p2Hold = 0;
		if (l2 > 0) g_p2Down = 0;
	}

	// ---- rounds are not games -------------------------------------------
	// Every one of these games clears both life words to zero when the match
	// itself is over - which is a different event from a round ending, where
	// the loser goes negative and then refills. So that is where a game is
	// awarded, and it is why this does not simply wait for somebody to reach
	// two: a double KO can take a match past two rounds, and five matches
	// each won by a single round would otherwise read as two games won.
	if (l1 == 0 && l2 == 0) {
		if (g_matchHold < SCORE_HOLD_FRAMES) g_matchHold++;
		if (g_matchHold >= SCORE_HOLD_FRAMES && !g_matchOver) {
			g_matchOver = 1;
			awardGame();
		}
	} else {
		g_matchHold = 0;
		g_matchOver = 0;
	}
}

int MatchScoreGet(MatchScoreData* out)
{
	if (!out) return 0;
	*out = g_d;

	if (g_map && g_map->bBars) {
		// Losing the gauge is losing both bars at once; getting through it
		// with less than half left is one bar gone.
		out->nP1Rounds = g_p2Down ? 2 : ((g_p2Low <= g_map->nFull / 2) ? 1 : 0);
		out->nP2Rounds = g_p1Down ? 2 : ((g_p1Low <= g_map->nFull / 2) ? 1 : 0);
	}

	return g_d.bEverStarted ? 1 : 0;
}

void MatchScoreStop(void (*pfnLog)(const char*))
{
	if (!g_map) return;

	MatchScoreData d;
	if (MatchScoreGet(&d)) {
		char szP1[64] = "?", szP2[64] = "?";
		if (d.bHaveP1Char) {
			int n = snprintf(szP1, sizeof(szP1), "%d", d.nP1Char[0]);
			for (int i = 1; i < d.nCharCount && n > 0 && n < (int)sizeof(szP1); i++)
				n += snprintf(szP1 + n, sizeof(szP1) - n, "/%d", d.nP1Char[i]);
		}
		if (d.bHaveP2Char) {
			int n = snprintf(szP2, sizeof(szP2), "%d", d.nP2Char[0]);
			for (int i = 1; i < d.nCharCount && n > 0 && n < (int)sizeof(szP2); i++)
				n += snprintf(szP2 + n, sizeof(szP2) - n, "/%d", d.nP2Char[i]);
		}
		score_log(pfnLog, "score: %s  P1[%s] %d x %d P2[%s]  (%d partidas, %s)"
		                  "  rounds em andamento: %d x %d",
		          d.szGame, szP1, d.nP1Games, d.nP2Games, szP2, d.nGames,
		          d.nP1Games > d.nP2Games ? "P1 venceu"
		          : d.nP2Games > d.nP1Games ? "P2 venceu" : "empate",
		          d.nP1Rounds, d.nP2Rounds);
	} else {
		score_log(pfnLog, "score: no fight started, nothing to report");
	}

	g_map = NULL;
}

int MatchScoreIsActive(void) { return g_map != NULL; }
