// ---------------------------------------------------------------------------
// score_selftest - drives match_score.cpp with a scripted fight, no game and
// no emulator involved.
//
// The reader inside the emulator is the one piece of this that used to be
// checkable only by playing: start a match, win some games, close it, read the
// log. That is a slow way to find out that the characters of game 1 were
// written on top of game 3.
//
// So this feeds it life values by hand. RamProbeRead8 is replaced here, which
// is the whole trick - everything above it is the shipping code, unmodified.
//
// Build (MSYS2 mingw64 shell, from this folder):
//
//   g++ -std=c++11 -I. -I../../fbneo -o score_selftest.exe \
//       score_selftest.cpp ../../fbneo/match_score.cpp
//   ./score_selftest.exe
//
// The -I. is for the burnint.h stub next to this file.
// ---------------------------------------------------------------------------
#include "match_score.h"
#include "ram_probe.h"

#include <stdio.h>
#include <string.h>

// ---- the fake game --------------------------------------------------------
// sfa2 addresses, from match_score.cpp. sfa2 and not sf2ce because sf2ce has
// its character reading switched off until the right address is found - and a
// test of the per-game character logic needs a game that reads characters.
// Life is a signed 16-bit big-endian word; the character is one byte.
static int g_life1 = 0, g_life2 = 0;
static int g_char1 = 0, g_char2 = 0;

extern "C" {

unsigned char RamProbeRead8(unsigned int nAddr)
{
	switch (nAddr) {
		case 0xFF8450: return (unsigned char)((g_life1 >> 8) & 0xFF);
		case 0xFF8451: return (unsigned char)(g_life1 & 0xFF);
		case 0xFF8850: return (unsigned char)((g_life2 >> 8) & 0xFF);
		case 0xFF8851: return (unsigned char)(g_life2 & 0xFF);
		case 0xFF8482: return (unsigned char)g_char1;
		case 0xFF8882: return (unsigned char)g_char2;
	}
	return 0;
}

int RamProbeAttach(void (*pfnLog)(const char*)) { (void)pfnLog; return RAM_PROBE_OK; }

// match_score.cpp asks the driver for its name.
char* BurnDrvGetTextA(unsigned int i) { (void)i; return (char*)"sfa2"; }

} // extern "C"

// ---- the script -----------------------------------------------------------
#define FULL 0x90

static void run(int nFrames) { for (int i = 0; i < nFrames; i++) MatchScoreFrame(); }

static void live(int l1, int l2, int nFrames)
{
	g_life1 = l1; g_life2 = l2;
	run(nFrames);
}

// One round: both at full for a while, then the loser goes negative and stays
// there. 30 frames is well past the 12 the reader holds a knockout for.
static void round_(int nLoser)
{
	live(FULL, FULL, 30);
	if (nLoser == 1) live(-1, 40, 30);
	else             live(40, -1, 30);
}

// The end of a game: the driver clears both life words. That, and not a round
// ending, is what separates one game from the next.
static void endOfGame(void) { live(0, 0, 30); }

static void pick(int p1, int p2) { g_char1 = p1; g_char2 = p2; }

// ---- checks ---------------------------------------------------------------
static int g_pass = 0, g_fail = 0;

static void check(int ok, const char* what)
{
	if (ok) { g_pass++; printf("  ok    %s\n", what); }
	else    { g_fail++; printf("  FALHA %s\n", what); }
}

static void checkRow(const MatchScoreData* d, int i, int c1, int c2, int r1, int r2, int winner)
{
	char what[128];
	snprintf(what, sizeof(what), "partida %d: P1[%d] %d x %d P2[%d], vencedor %d",
	         i + 1, c1, r1, r2, c2, winner);
	if (i >= d->nGameRows) { check(0, what); return; }

	const MatchGameRow* g = &d->aGames[i];
	check(g->bHaveP1Char && g->nP1Char[0] == c1 &&
	      g->bHaveP2Char && g->nP2Char[0] == c2 &&
	      g->nP1Rounds == r1 && g->nP2Rounds == r2 &&
	      g->nWinner == winner, what);
}

int main(void)
{
	printf("score_selftest\n\n");
	printf("-- uma sessao de tres partidas, personagens diferentes em cada\n");

	// FT3, so the third win should also trip the session limit.
	MatchScoreStart(3, NULL);

	// 1. Ryu vs E.Honda, 2 x 1.
	pick(4, 5);
	round_(2);        // P2 loses the round
	round_(1);
	round_(2);
	endOfGame();

	// Somebody quits out to the select screen: the fight starts, the life
	// words clear a few seconds later, and nobody won a round. This is not a
	// game. It happened for real on 12/09 and reached the database as a drawn
	// match, shifting the numbering of every game after it.
	pick(9, 9);
	live(FULL, FULL, 30);
	endOfGame();

	// 2. Ken vs E.Honda, 2 x 0.
	pick(6, 5);
	round_(2);
	round_(2);
	endOfGame();

	// 3. Ryu vs Ken, 0 x 2.
	pick(4, 6);
	round_(1);
	round_(1);
	endOfGame();

	MatchScoreData d;
	check(MatchScoreGet(&d) == 1, "houve luta nesta sessao");

	check(d.nGames == 3, "tres partidas terminadas");
	// A quarta "partida" foi a desistencia, e ela nao entra em nada.
	check(d.nGameRows == 3 && d.aGames[1].nP1Char[0] == 6,
	      "a desistencia 0 x 0 nao virou partida nem empurrou a numeracao");
	check(d.nP1Games == 2 && d.nP2Games == 1, "placar da sessao 2 x 1");
	check(d.nGameRows == 3, "tres partidas detalhadas");

	checkRow(&d, 0, 4, 5, 2, 1, 1);
	checkRow(&d, 1, 6, 5, 2, 0, 1);
	checkRow(&d, 2, 4, 6, 0, 2, 2);

	// The bug this exists to prevent: one pair of character ids for the whole
	// session would give every game the last one read.
	check(d.nGameRows == 3 &&
	      d.aGames[0].nP1Char[0] != d.aGames[1].nP1Char[0],
	      "o personagem da partida 1 nao virou o da partida 2");

	// Nobody reached 3 games, so the session should not have been cut short.
	check(d.bLimitReached == 0, "FT3 nao foi atingido com 2 x 1");

	check(d.bHaveP1Char == 0 && d.bHaveP2Char == 0,
	      "entre partidas o leitor esquece os personagens");

	// ---- the file the launcher reads --------------------------------------
	printf("\n-- o arquivo que o launcher le\n");
	MatchScoreWriteResult("selftest", 3, "closed", NULL);

	FILE* f = fopen("rbf-result-selftest.txt", "rb");
	check(f != NULL, "rbf-result-selftest.txt foi escrito");
	if (f) {
		char buf[4096];
		size_t n = fread(buf, 1, sizeof(buf) - 1, f);
		buf[n] = '\0';
		fclose(f);
		remove("rbf-result-selftest.txt");

		check(strstr(buf, "partida1=p1chars=4 p2chars=5 rounds=2-1 vencedor=p1") != NULL,
		      "a partida 1 saiu no formato combinado");
		check(strstr(buf, "partida2=p1chars=6 p2chars=5 rounds=2-0 vencedor=p1") != NULL,
		      "a partida 2 saiu no formato combinado");
		check(strstr(buf, "partida3=p1chars=4 p2chars=6 rounds=0-2 vencedor=p2") != NULL,
		      "a partida 3 saiu no formato combinado");
		check(strstr(buf, "p1games=2") != NULL && strstr(buf, "p2games=1") != NULL,
		      "o total da sessao saiu junto");

		printf("\n%s\n", buf);
	}

	printf("%s\n", g_fail == 0 ? "tudo certo" : "FALHOU");
	printf("%d checagens, %d falhas\n", g_pass + g_fail, g_fail);
	return g_fail == 0 ? 0 : 1;
}
