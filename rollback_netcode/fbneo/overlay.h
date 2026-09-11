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
// session is showing.
void OverlayDraw(unsigned char* pImage, int nWidth, int nHeight, int nBpp, int nPitch);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_OVERLAY_H
