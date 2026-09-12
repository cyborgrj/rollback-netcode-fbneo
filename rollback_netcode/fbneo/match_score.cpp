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
static long long g_gameStart = 0;   // live frame the current game began on
static int  g_matchHold = 0;   // consecutive live frames with both words cleared
static int  g_matchOver = 0;   // this clearing has already been counted
static int  g_firstTo   = 0;   // games that end the session; 0 = free play

// vsav has no rounds: the score of a game is how many of the two bars each
// side lost. Two when the gauge went negative, one when they finished at half
// or less, none above that.
static void barsOf(int* pP1, int* pP2)
{
	*pP1 = g_p2Down ? 2 : ((g_p2Low <= g_map->nFull / 2) ? 1 : 0);
	*pP2 = g_p1Down ? 2 : ((g_p1Low <= g_map->nFull / 2) ? 1 : 0);
}

// Hand the finished game to whoever won more rounds of it, write down what it
// was, then start over. A drawn game - a double KO at match point, most often -
// counts for neither side, which is what happens on the machine too.
static void awardGame(void)
{
	int p1 = g_d.nP1Rounds, p2 = g_d.nP2Rounds;
	if (g_map && g_map->bBars) barsOf(&p1, &p2);

	const int nWinner = (p1 > p2) ? 1 : (p2 > p1) ? 2 : 0;
	if (nWinner == 1)      g_d.nP1Games++;
	else if (nWinner == 2) g_d.nP2Games++;

	// The row is the point of all this: totals cannot say who used what, and
	// the characters change between games.
	if (g_d.nGameRows < MATCH_SCORE_MAX_GAMES) {
		MatchGameRow* row = &g_d.aGames[g_d.nGameRows++];
		memset(row, 0, sizeof(*row));
		memcpy(row->nP1Char, g_d.nP1Char, sizeof(row->nP1Char));
		memcpy(row->nP2Char, g_d.nP2Char, sizeof(row->nP2Char));
		row->bHaveP1Char = g_d.bHaveP1Char;
		row->bHaveP2Char = g_d.bHaveP2Char;
		row->nP1Rounds   = p1;
		row->nP2Rounds   = p2;
		row->nWinner     = nWinner;
		row->nFrames     = g_gameStart ? (int)(g_frame - g_gameStart) : 0;
	} else {
		g_d.bGamesTruncated = 1;
	}

	g_d.nGames++;
	if (g_firstTo > 0 && (g_d.nP1Games >= g_firstTo || g_d.nP2Games >= g_firstTo))
		g_d.bLimitReached = 1;

	g_d.nP1Rounds = 0;
	g_d.nP2Rounds = 0;
	g_p1Low = g_p2Low = 0x7FFFFFFF;
	g_p1Down = g_p2Down = 0;
	g_p1Hold = g_p2Hold = 0;
	g_gameStart = 0;
	// Forget the characters. Both sides pick again between games, and a game
	// we failed to read has to report nothing rather than repeat the last one.
	memset(g_d.nP1Char, 0, sizeof(g_d.nP1Char));
	memset(g_d.nP2Char, 0, sizeof(g_d.nP2Char));
	g_d.bHaveP1Char = g_d.bHaveP2Char = 0;
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

// "4" for a single character, "0/1/2" for a KOF team, "?" when unread.
static void charsToText(char* szOut, size_t nOut, const int* pChars, int nCount, int bHave)
{
	if (!bHave || nCount <= 0) { snprintf(szOut, nOut, "?"); return; }
	int n = snprintf(szOut, nOut, "%d", pChars[0]);
	for (int i = 1; i < nCount && n > 0 && n < (int)nOut; i++)
		n += snprintf(szOut + n, nOut - n, "/%d", pChars[i]);
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
	g_gameStart = 0;

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
	if (g_firstTo > 0)
		score_log(pfnLog, "score: reading %s, limite FT%d", szGame, g_firstTo);
	else
		score_log(pfnLog, "score: reading %s, sem limite (livre) - "
		                  "se voce pediu FT, o servidor mandou 0: ele esta desatualizado?",
		          szGame);
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
		if (!g_gameStart)     g_gameStart     = g_frame;
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
		barsOf(&out->nP1Rounds, &out->nP2Rounds);
	}

	return g_d.bEverStarted ? 1 : 0;
}

int MatchScoreLimitReached(void) { return g_d.bLimitReached; }

void MatchScoreGames(int* pP1, int* pP2)
{
	if (pP1) *pP1 = g_d.nP1Games;
	if (pP2) *pP2 = g_d.nP2Games;
}

void MatchScoreStop(void (*pfnLog)(const char*))
{
	if (!g_map) return;

	MatchScoreData d;
	if (MatchScoreGet(&d)) {
		score_log(pfnLog, "score: %s  %d x %d  (%d partidas, %s)",
		          d.szGame, d.nP1Games, d.nP2Games, d.nGames,
		          d.nP1Games > d.nP2Games ? "P1 venceu"
		          : d.nP2Games > d.nP1Games ? "P2 venceu" : "empate");

		// One line per game, which is the whole reason the rows exist.
		for (int i = 0; i < d.nGameRows; i++) {
			const MatchGameRow* g = &d.aGames[i];
			char szP1[64], szP2[64];
			charsToText(szP1, sizeof(szP1), g->nP1Char, d.nCharCount, g->bHaveP1Char);
			charsToText(szP2, sizeof(szP2), g->nP2Char, d.nCharCount, g->bHaveP2Char);
			score_log(pfnLog, "score:   %d. P1[%s] %d x %d P2[%s]  %s",
			          i + 1, szP1, g->nP1Rounds, g->nP2Rounds, szP2,
			          g->nWinner == 1 ? "P1 venceu" :
			          g->nWinner == 2 ? "P2 venceu" : "empate");
		}
		if (d.bGamesTruncated)
			score_log(pfnLog, "score:   (sessao longa: so as primeiras %d partidas "
			                  "foram detalhadas)", MATCH_SCORE_MAX_GAMES);
		if (d.nP1Rounds || d.nP2Rounds)
			score_log(pfnLog, "score:   partida interrompida no meio: %d x %d rounds",
			          d.nP1Rounds, d.nP2Rounds);
	} else {
		score_log(pfnLog, "score: no fight started, nothing to report");
	}

	g_map = NULL;
}

int MatchScoreIsActive(void) { return g_map != NULL; }

static void writeChars(FILE* f, const char* szKey, const int* pChars, int nCount, int bHave)
{
	if (!bHave) return;
	fprintf(f, "%s=", szKey);
	for (int i = 0; i < nCount; i++) fprintf(f, i ? ",%d" : "%d", pChars[i]);
	fputc('\n', f);
}

// One finished game as a single value, readable by a person and trivial to
// parse:
//
//   partida3=p1chars=15 p2chars=0 rounds=0-2 vencedor=p2 frames=4210
//
// Fields are separated by spaces and never contain one; a team is a comma
// list, the same as the top-level p1chars=. A side we could not read leaves
// its field out entirely rather than inventing a number.
static void writeGameRow(FILE* f, int nIndex, const MatchGameRow* g, int nCharCount)
{
	fprintf(f, "partida%d=", nIndex);
	if (g->bHaveP1Char) {
		fprintf(f, "p1chars=");
		for (int i = 0; i < nCharCount; i++) fprintf(f, i ? ",%d" : "%d", g->nP1Char[i]);
		fputc(' ', f);
	}
	if (g->bHaveP2Char) {
		fprintf(f, "p2chars=");
		for (int i = 0; i < nCharCount; i++) fprintf(f, i ? ",%d" : "%d", g->nP2Char[i]);
		fputc(' ', f);
	}
	fprintf(f, "rounds=%d-%d vencedor=%s frames=%d\n",
	        g->nP1Rounds, g->nP2Rounds,
	        g->nWinner == 1 ? "p1" : g->nWinner == 2 ? "p2" : "empate",
	        g->nFrames);
}

int MatchScoreWriteResult(const char* szMatchId, int nFirstTo, const char* szReason,
                          void (*pfnLog)(const char*))
{
	MatchScoreData d;
	if (!MatchScoreGet(&d)) {
		score_log(pfnLog, "result: nenhuma luta comecou - nada a registrar");
		return -1;
	}
	if (!szMatchId || !szMatchId[0]) {
		score_log(pfnLog, "result: partida sem id - nada a registrar");
		return -1;
	}

	// Anything but [0-9a-zA-Z-] would let a match id reach outside the folder.
	// It comes from the server, but a filename built from a value we did not
	// generate is worth checking whatever its source.
	char szSafe[48];
	size_t n = 0;
	for (size_t i = 0; szMatchId[i] && n + 1 < sizeof(szSafe); i++) {
		const char c = szMatchId[i];
		if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') ||
		    (c >= 'A' && c <= 'Z') || c == '-') szSafe[n++] = c;
	}
	szSafe[n] = '\0';
	if (!n) return -1;

	char szPath[128];
	snprintf(szPath, sizeof(szPath), "rbf-result-%s.txt", szSafe);

	FILE* f = fopen(szPath, "wb");
	if (!f) {
		score_log(pfnLog, "result: nao consegui escrever %s", szPath);
		return -1;
	}

	fprintf(f, "match=%s\n",  szSafe);
	fprintf(f, "game=%s\n",   d.szGame);
	fprintf(f, "p1games=%d\n", d.nP1Games);
	fprintf(f, "p2games=%d\n", d.nP2Games);
	fprintf(f, "games=%d\n",   d.nGames);
	fprintf(f, "firstto=%d\n", nFirstTo);
	fprintf(f, "reason=%s\n",  szReason ? szReason : "closed");
	fprintf(f, "chars=%d\n",   d.nCharCount);
	// The characters of the game still in progress, when there is one. The
	// partidaN lines below are what a scoreboard should read - these are here
	// so a session cut short mid-game still says what was being played.
	writeChars(f, "p1chars", d.nP1Char, d.nCharCount, d.bHaveP1Char);
	writeChars(f, "p2chars", d.nP2Char, d.nCharCount, d.bHaveP2Char);

	for (int i = 0; i < d.nGameRows; i++)
		writeGameRow(f, i + 1, &d.aGames[i], d.nCharCount);
	if (d.bGamesTruncated)
		fprintf(f, "truncado=1\n");
	// A game that was under way when the session ended is not a result, but
	// saying how far it had got is cheap, and it explains a session whose
	// games do not account for the time it lasted.
	if (d.nP1Rounds || d.nP2Rounds)
		fprintf(f, "parcial=rounds=%d-%d\n", d.nP1Rounds, d.nP2Rounds);
	fclose(f);

	score_log(pfnLog, "result: %s escrito (%d x %d, %d partidas detalhadas, %s)",
	          szPath, d.nP1Games, d.nP2Games, d.nGameRows,
	          szReason ? szReason : "closed");
	return 0;
}
