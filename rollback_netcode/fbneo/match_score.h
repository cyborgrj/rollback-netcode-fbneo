// ---------------------------------------------------------------------------
// match_score.h - Read the result of the match out of the game itself.
//
// One rule covers all four games, and it came out of eight recorded matches
// whose results were known in advance (see tools/README.md):
//
//   Life is a SIGNED 16-bit big-endian word. A side has lost the round when
//   its own word goes negative - not when it reaches zero. Zero is the struct
//   being cleared once the match is over, which is a different event and used
//   to be read as an extra round.
//
// So the score is an edge count: how many times the other side went negative.
// Nothing here asks the players to report anything, which is the only version
// of this that cannot be lied about.
//
// vsav is the exception and it is a real one, not a quirk of the reading: the
// game has no rounds. One gauge worth two 144-unit bars, no refill, no round
// break. There the score is how many bars each side lost - two if the gauge
// ran out, one if they finished under half, none otherwise.
//
// WHAT A SESSION LOOKS LIKE. A session is a series of games, each of which is
// a best-of-three on the machine. Totals alone throw away most of what
// happened: five games at 3x2 is one line, but what a scoreboard needs is
//
//   1  Ryu      2 x 1  Blanka
//   2  Ryu      2 x 1  Sagat
//   3  Dhalsim  0 x 2  Ryu
//
// - the characters CHANGE between games, so a single pair of character ids
// describes the last game and misdescribes every other one. That is why each
// finished game is appended to aGames with its own characters and its own
// round count, and why the character reading is forgotten when a game ends: a
// game nobody could read has to say so instead of inheriting the one before.
//
// Characters are read on every frame where both sides are alive, keeping the
// last such reading of the CURRENT game. Reading once at the start is wrong
// twice over: sf2ce fills the field a moment after the bars go full, and the
// arcade writes the NEXT opponent into it the instant the match ends.
//
// Rollback safety: a knockout has to hold for SCORE_HOLD_FRAMES consecutive
// live frames before it counts. libggpo never predicts more than 8 frames
// ahead, and a knockout lasts seconds, so a mispredicted frame can never
// invent a round.
//
// Threading: emulation thread only.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_MATCH_SCORE_H
#define ROLLBACK_MATCH_SCORE_H

#ifdef __cplusplus
extern "C" {
#endif

#define MATCH_SCORE_MAX_CHARS 3      // a KOF team

// Games kept in full detail. A "Livre" session has no limit, so this has a
// ceiling - past it the totals keep counting and bGamesTruncated says the
// detail is incomplete, which is better than either growing without bound or
// quietly losing rows.
#define MATCH_SCORE_MAX_GAMES 64

enum MatchScoreResult {
	MATCH_SCORE_OK           =  0,
	MATCH_SCORE_ERR_NO_RAM   = -1,   // could not attach to the work RAM
	MATCH_SCORE_ERR_NO_MAP   = -2    // this driver is not one we can read
};

// One finished game: who used what, and how many rounds each side took.
typedef struct MatchGameRow {
	int nP1Char[MATCH_SCORE_MAX_CHARS];
	int nP2Char[MATCH_SCORE_MAX_CHARS];
	int bHaveP1Char;
	int bHaveP2Char;
	int nP1Rounds;
	int nP2Rounds;
	int nWinner;      // 1 = P1, 2 = P2, 0 = draw (a double KO at match point)
	int nFrames;      // how long it took, in emulated frames - 3600 is a minute
} MatchGameRow;

typedef struct MatchScoreData {
	char      szGame[32];
	int       bStarted;       // a fight is running right now
	int       bEverStarted;   // at least one fight has happened this session
	// Games won - what a scoreboard should show. A game is best-of-three on
	// the machine, but counting to two is not the same thing: a double KO can
	// take a game past two rounds, and somebody who wins one round in each of
	// five games has won no games at all.
	int       nP1Games;
	int       nP2Games;
	int       nGames;         // finished games, including drawn ones
	// Rounds of the game being played right now; reset when it ends.
	int       nP1Rounds;
	int       nP2Rounds;
	int       nCharCount;    // 1, or 3 for a team game
	// Characters of the game being played right now. Cleared when it ends.
	int       nP1Char[MATCH_SCORE_MAX_CHARS];
	int       nP2Char[MATCH_SCORE_MAX_CHARS];
	int       bHaveP1Char;
	int       bHaveP2Char;   // vsav cannot read the opponent side yet
	// Every finished game, in the order they were played.
	int          nGameRows;
	int          bGamesTruncated;
	MatchGameRow aGames[MATCH_SCORE_MAX_GAMES];
	// Somebody reached the agreed number of games. The session should end.
	int       bLimitReached;
	long long nStartFrame;
	long long nEndFrame;
} MatchScoreData;

// Arm the reader for the running driver. Call after BurnDrvInit. Returns
// MATCH_SCORE_ERR_NO_MAP for a game we have no addresses for, which is not
// fatal - the match just goes unscored.
int  MatchScoreStart(int nFirstTo, void (*pfnLog)(const char*));

// One sample. Call once per LIVE frame (never from a rollback re-simulation).
// No-op unless MatchScoreStart succeeded.
void MatchScoreFrame(void);

// Copy out what has been read so far. Returns 1 when a fight was seen.
//
// This struct is several kilobytes - it carries every game of the session. Do
// NOT call this once per frame just to read one number: use the two accessors
// below. Copying it sixty times a second is waste, and a caller that gets the
// struct size wrong (a stale object file, say) corrupts its own stack doing it.
int  MatchScoreGet(MatchScoreData* out);

// The two things somebody needs every frame, without the copy.
int  MatchScoreLimitReached(void);            // the agreed number of games was reached
void MatchScoreGames(int* pP1, int* pP2);     // games won so far; either pointer may be NULL

// Log the result and disarm. Safe to call unconditionally.
void MatchScoreStop(void (*pfnLog)(const char*));

int  MatchScoreIsActive(void);

// Write the result where the launcher can find it: rbf-result-<matchId>.txt in
// the emulator folder, one key=value per line, plus one partidaN= line per
// finished game.
//
// A file, rather than talking to the launcher directly, because the two are
// separate processes and this has to survive the interesting cases - the
// emulator being closed by the window button, the match ending on a
// disconnect, the launcher being busy. Whoever reads it deletes it.
//
// szReason is why the session ended: "limit", "disconnect" or "closed".
// Returns 0 when something was written.
int  MatchScoreWriteResult(const char* szMatchId, int nFirstTo, const char* szReason,
                           void (*pfnLog)(const char*));

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_MATCH_SCORE_H
