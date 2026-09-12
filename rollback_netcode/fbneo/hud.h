// ---------------------------------------------------------------------------
// hud.h - the netcode readout, top right.
//
//     ping 13ms   delay 3f   rollback 2f
//              run ahead off   59,9 fps
//
// Backspace walks it: two lines, then the first line alone, then nothing. Off
// is where it starts, because most of the time nobody wants numbers on top of
// the game - they want them the moment something feels wrong.
//
// Everything here is measured, never assumed:
//
//   ping        libggpo's own round trip to the peer, not the lobby's.
//   delay       the input delay the SESSION runs with, which the server set
//               from both players' preferences - not what this side asked for.
//   rollback    frames libggpo re-simulated, worst burst in the last second.
//               Per-frame it is 0 most of the time and 4 for one frame at
//               60Hz, which is unreadable; the peak holds and decays.
//   run ahead   FBNeo's own lag-reduction option. During a match it always
//               reads OFF, and that is the truth rather than a missing
//               feature: rollback replaces it, and our frame path does not
//               go through FBNeo's run-ahead branch at all. It is not a
//               1/2/3 setting in FBNeo either - it is one frame, on or off.
//   fps         frames actually presented, measured here over half a second.
//
// Offline - testing a game with no session - the three network fields read
// "--" rather than disappearing, so the layout does not jump around.
//
// Threading: emulation thread only, same as overlay.cpp.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_HUD_H
#define ROLLBACK_HUD_H

#ifdef __cplusplus
extern "C" {
#endif

// Backspace: off -> two lines -> first line only -> off.
void HudCycle(void);
int  HudMode(void);      // 0 hidden, 1 first line only, 2 both lines

// Once per presented frame, before drawing. Samples the counters and the fps.
void HudFrame(void);

// Drawn into the game image, like the match bar - the fallback for blitters
// that cannot do better. Does nothing when hidden or when a blitter claimed
// the job (OverlayClaim).
void HudDraw(unsigned char* pImage, int nWidth, int nHeight, int nBpp, int nPitch);

// ---- for a blitter that draws at window resolution ------------------------
typedef struct HudLines {
	int  nMode;              // 1 = first line only, 2 = both
	char szLine1[96];
	char szLine2[96];
	unsigned int rgbText;
	unsigned int rgbBar;
	int  nBarAlpha;          // 0..255
} HudLines;

// 1 when there is something to draw. Both lines are already formatted; the
// caller right-aligns them at the top right corner.
int HudGetLines(HudLines* out);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_HUD_H
