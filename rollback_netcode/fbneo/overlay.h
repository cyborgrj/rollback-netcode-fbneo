// ---------------------------------------------------------------------------
// overlay.h - Who is playing, and what the score is, drawn over the game.
//
// Frame Perfect draws this into the game image itself rather than over the
// window, which is the only way it works the same in every blitter FBNeo has -
// and it means a screenshot or a recording carries it too. The hook is the one
// the Lua GUI already uses (VidFrameCallback in intf/video/vid_interface.cpp),
// so it runs after the frame is drawn and before anything is presented.
//
// Layout: a dark strip across the top, with everything grouped in the middle
// rather than pushed into the corners, where a name ends up fighting the
// game's own life bars for attention.
//
//     |            ALICE 2   FT5   1 BOB            |
//      cyan^ white^  label^  ^white ^lilac
//
// The two scores sit either side of the FT label, so the pair reads as a
// score. Room is left at both ends for a rank badge later. Colours are the
// Frame Perfect ones, which are the Dracula palette.
//
// The score comes from match_score.cpp, which reads it out of the game; the
// overlay never counts anything itself.
//
// Threading: none to speak of. FBNeo runs video from the emulation thread -
// VidFrame() calls the blitter, which calls back into VidFrameCallback - so
// OverlayDraw runs between frames on the same thread that set the names. A
// repaint outside a frame reads the same values, which are only written when a
// session opens or closes.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_OVERLAY_H
#define ROLLBACK_OVERLAY_H

#ifdef __cplusplus
extern "C" {
#endif

// Show the overlay for this match. Either name may be empty; nFirstTo is the
// match limit the players agreed on (0 hides the label). Passing NULL for both
// names is the same as OverlayHide().
void OverlayShow(const char* szP1, const char* szP2, int nFirstTo);

void OverlayHide(void);
int  OverlayIsOn(void);

// Tab walks through off / small / large, the way Fightcade does it - somebody
// who wants the screen clean for a screenshot should not have to restart.
void OverlayCycle(void);
int  OverlayMode(void);   // 0 off, 1 small, 2 large

// Called from VidFrameCallback with the finished game image. nBpp is BYTES per
// pixel: 2 = RGB565, 3 and 4 = B,G,R in ascending bytes. Does nothing when no
// session is showing, or when a blitter has claimed the job.
void OverlayDraw(unsigned char* pImage, int nWidth, int nHeight, int nBpp, int nPitch);

// ---- for a blitter that can do better -------------------------------------
//
// Drawing into the game image means drawing at about 384x224 and letting the
// blitter magnify the result, which is legible but soft - eight source pixels
// of letter cannot be made sharp by stretching them. A blitter that renders
// text at WINDOW resolution should ask for the line and draw it itself.
//
// Layout is the caller's to do, but the rule is: the FT label is centred on
// the screen, the player one group is right-aligned to its left and the player
// two group left-aligned to its right. Anchoring on the middle rather than on
// the edges is what keeps the two scores together when one player has a very
// long name and the other a very short one.
typedef struct OverlayLine {
	int  nMode;                 // 1 small, 2 large
	char szP1[40], szS1[8], szFt[12], szS2[8], szP2[40];
	unsigned int rgbP1, rgbP2, rgbScore, rgbLabel, rgbBar;
	int  nBarAlpha;             // 0..255
} OverlayLine;

// 1 when there is something to draw.
int OverlayGetLine(OverlayLine* out);

// The typeface that loaded: "Johnny Fever", or "Arial" when the file was not
// found next to the emulator.
const char* OverlayFaceName(void);

// A blitter that draws the line itself says so here, and the in-image
// fallback stands down.
void OverlayClaim(int bClaimed);
int  OverlayClaimed(void);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_OVERLAY_H
