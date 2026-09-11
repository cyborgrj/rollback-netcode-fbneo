// ---------------------------------------------------------------------------
// overlay.h - Who is playing, and what the score is, drawn over the game.
//
// Frame Perfect draws this into the game image itself rather than over the
// window, which is the only way it works the same in every blitter FBNeo has -
// and it means a screenshot or a recording carries it too. The hook is the one
// the Lua GUI already uses (VidFrameCallback in intf/video/vid_interface.cpp),
// so it runs after the frame is drawn and before anything is presented.
//
// Layout, at the top of the screen:
//
//     ALICE 2                              1 BOB
//     ^cyan ^white                   white^ ^lilac
//
// Player one on the left in cyan, player two on the right in lilac, each score
// against the middle - so the two numbers sit next to each other where the eye
// looks for them. Colours are the Frame Perfect ones.
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

// Show the overlay for this match. Either name may be empty. Passing NULL for
// both is the same as OverlayHide().
void OverlayShow(const char* szP1, const char* szP2);

void OverlayHide(void);
int  OverlayIsOn(void);

// Called from VidFrameCallback with the finished game image. nBpp is BYTES per
// pixel: 2 = RGB565, 3 and 4 = B,G,R in ascending bytes. Does nothing when no
// session is showing.
void OverlayDraw(unsigned char* pImage, int nWidth, int nHeight, int nBpp, int nPitch);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_OVERLAY_H
