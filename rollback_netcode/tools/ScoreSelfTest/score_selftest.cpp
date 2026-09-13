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
		// vsav shares the life words with sfa2; its pick bytes are elsewhere
		case 0xFF841D: return (unsigned char)g_char1;
		case 0xFF881D: return (unsigned char)g_char2;
		// kof98: life words and the three team slots per side
		case 0x108238: return (unsigned char)((g_life1 >> 8) & 0xFF);
		case 0x108239: return (unsigned char)(g_life1 & 0xFF);
		case 0x108438: return (unsigned char)((g_life2 >> 8) & 0xFF);
		case 0x108439: return (unsigned char)(g_life2 & 0xFF);
		case 0x10A84E: case 0x10A84F: case 0x10A850:
			return (unsigned char)(g_char1 + (nAddr - 0x10A84E));
		case 0x10A85F: case 0x10A860: case 0x10A861:
			return (unsigned char)(g_char2 + (nAddr - 0x10A85F));
	}
	return 0;
}

int RamProbeAttach(void (*pfnLog)(const char*)) { (void)pfnLog; return RAM_PROBE_OK; }

// match_score.cpp asks the driver for its name. sfa2 unless a test says so.
static const char* g_driver = "sfa2";
char* BurnDrvGetTextA(unsigned int i) { (void)i; return (char*)g_driver; }

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

	// ---- a pick byte that flickers ----------------------------------------
	// vsav, 13/09: J. Talbain's byte read 19/20/19... through the fight, and
	// the last reading before his KO was 20. Keeping the last reading named
	// the wrong character; the value seen most is the one that was picked.
	printf("\n-- byte do personagem que pisca no meio da luta\n");
	MatchScoreStart(0, NULL);

	pick(19, 17);
	live(FULL, FULL, 60);
	g_char1 = 20;              // a move flips it...
	live(40, 40, 20);          // ...right up to the knockout
	live(-1, 40, 30);
	endOfGame();

	MatchScoreData v;
	MatchScoreGet(&v);
	check(v.nGameRows == 1 && v.aGames[0].nP1Char[0] == 19,
	      "vale o valor mais lido (19), nao o ultimo antes do KO (20)");
	check(v.nGameRows == 1 && v.aGames[0].nP2Char[0] == 17,
	      "o lado que nao pisca continua certo");

	// ---- vsav: the flicker goes to id+1, and can be the majority ----------
	// Lilith (34) read 35 for 42% of a real fight. Here it is 2/3 of the
	// fight: a plain majority would say 35, which is nobody. And J. Talbain
	// (19) holding still must not be handed to Demitri (18) just because a
	// reading of 19 also counts as "18 flickering".
	printf("\n-- vsav: o pisca vai para id+1 e pode ser maioria\n");
	g_driver = "vsav";
	MatchScoreStart(0, NULL);

	pick(19, 34);
	live(0x120, 0x120, 20);
	g_char2 = 35;
	live(0x120, 100, 40);
	live(-1, 100, 30);         // P1 loses the gauge
	endOfGame();

	MatchScoreData w;
	MatchScoreGet(&w);
	check(w.nGameRows == 1 && w.aGames[0].nP2Char[0] == 34,
	      "Lilith que leu 35 na maior parte da luta continua 34");
	check(w.nGameRows == 1 && w.aGames[0].nP1Char[0] == 19,
	      "J. Talbain parado em 19 nao vira Demitri (18)");
	check(w.nGameRows == 1 && w.aGames[0].nWinner == 2, "P1 perdeu a barra toda: P2 venceu");

	// ---- kof98: two games in a row with no clearing between them ----------
	// 13/09, two humans: the loser's life word stayed negative until the next
	// fight and the winner kept its life, so "both words at zero" never came
	// and two games were read as one 5 x 5. A team game ends on the count.
	printf("\n-- kof98: duas partidas seguidas sem zerar a vida entre elas\n");
	g_driver = "kof98";
	MatchScoreStart(3, NULL);
	const int K = 0x67;

	// 1. Iori/Mature/Vice (27) x Terry/Andy/Joe (3): P1 3 x 2
	pick(27, 3);
	live(K, K, 30);
	live(40, -1, 30);  live(40, K, 30);    // Terry down
	live(-1, 50, 30);  live(K, 50, 30);    // Iori down
	live(60, -1, 30);  live(60, K, 30);    // Andy down
	live(-1, 20, 30);  live(K, 20, 30);    // Mature down
	live(30, -1, 30);                      // Joe down: whole team
	live(30, -1, 200);                     // loser stays negative, nothing clears

	// 2. the same P1 team x Kyo/Benimaru/Daimon (0): P2 3 x 2
	pick(27, 0);
	live(K, K, 30);
	live(50, -1, 30);  live(50, K, 30);    // Daimon down
	live(50, -1, 30);  live(50, K, 30);    // Kyo down
	live(-1, 40, 30);  live(K, 40, 30);    // Vice down
	live(-1, 40, 30);  live(K, 40, 30);    // Iori down
	live(-1, 10, 30);                      // Mature down: whole team
	live(-1, 10, 100);

	MatchScoreData k;
	MatchScoreGet(&k);
	check(k.nGames == 2 && k.nGameRows == 2, "duas partidas, nao uma 5 x 5");
	check(k.nGameRows == 2 && k.aGames[0].nP1Rounds == 3 && k.aGames[0].nP2Rounds == 2 &&
	      k.aGames[0].nWinner == 1, "partida 1: P1 3 x 2");
	check(k.nGameRows == 2 && k.aGames[1].nP1Rounds == 2 && k.aGames[1].nP2Rounds == 3 &&
	      k.aGames[1].nWinner == 2, "partida 2: P2 3 x 2");
	check(k.nGameRows == 2 && k.aGames[0].nP2Char[0] == 3 && k.aGames[1].nP2Char[0] == 0 &&
	      k.aGames[1].nP2Char[2] == 2, "o time do P2 muda de uma partida para a outra");
	check(k.nP1Rounds == 0 && k.nP2Rounds == 0,
	      "a vida negativa do perdedor depois do fim nao virou KO da partida seguinte");
	g_driver = "sfa2";

	printf("\n%s\n", g_fail == 0 ? "tudo certo" : "FALHOU");
	printf("%d checagens, %d falhas\n", g_pass + g_fail, g_fail);
	return g_fail == 0 ? 0 : 1;
}
