// ---------------------------------------------------------------------------
// state_ring.h - Fixed, pre-allocated circular buffer of FBNeo save states
//                for rollback netcode (libggpo integration).
//
// Design contract:
//   * ONE malloc at StateRingInit(), ONE free at StateRingExit().
//   * Zero heap allocation during StateRingSave()/StateRingLoad().
//   * O(1) save/load; slot index = frame & (nSlots - 1)  (nSlots is pow2).
//   * Single-threaded: every call must run on the emulation thread, never
//     concurrently with BurnStateSave / VidFrame / RunAhead / Rewind, because
//     they all share the global BurnAcb function pointer.
//
// The captured state is exactly FBNeo's "RunAhead" volatile set
// (ACB_FULLSCAN | ACB_RUNAHEAD) - the same set FBNeo already re-simulates
// deterministically for its run-ahead feature.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_STATE_RING_H
#define ROLLBACK_STATE_RING_H

#ifdef __cplusplus
extern "C" {
#endif

// Return / status codes.
enum StateRingResult {
	STATE_RING_OK             =  0,
	STATE_RING_ERR_STATE     = -1,  // ring not initialised
	STATE_RING_ERR_ALLOC     = -2,  // out of memory at init
	STATE_RING_ERR_SIZE      = -3,  // size probe returned <= 0
	STATE_RING_ERR_OVERFLOW  = -4,  // driver wrote past the pinned slot size
	STATE_RING_ERR_SIZE_DRIFT= -5,  // driver state size changed mid-session (fatal for rollback)
	STATE_RING_ERR_MISS      = -6,  // requested frame is no longer resident in the ring
	STATE_RING_ERR_ARG       = -7   // bad argument (e.g. negative frame)
};

// Allocate the pool. Must be called AFTER the driver has executed at least one
// frame, so the volatile state size is stable. nMinSlots is rounded up to the
// next power of two; pass 0 for the built-in default (comfortably larger than
// libggpo's prediction window). Safe to call again - it re-inits.
int  StateRingInit(int nMinSlots);

// Release the pool. Idempotent.
void StateRingExit(void);

// Mark every slot empty. Call on game (re)load and whenever libggpo resyncs.
void StateRingReset(void);

// Save the current emulator state for logical frame nFrame into its ring slot.
// On success, optionally returns a borrowed pointer INTO the pool (valid until
// that slot is reused ~nSlots frames later), the fixed slot length, and a
// content hash. Shaped to feed libggpo's save_game_state callback directly.
int  StateRingSave(int nFrame, void** ppBuf, int* pnLen, unsigned int* pnChecksum);

// Restore emulator state for logical frame nFrame.
//   pBuf != NULL : restore from this buffer (a pointer libggpo previously got
//                  from StateRingSave); nLen must equal the slot size.
//   pBuf == NULL : restore from the ring by frame number (agent / debug path);
//                  fails with STATE_RING_ERR_MISS if that frame was evicted.
int  StateRingLoad(int nFrame, const void* pBuf, int nLen);

// libggpo free_buffer hook. No-op: buffers are owned by the pinned pool.
void StateRingFree(void* pBuf);

// --- introspection (for the gRPC control agent / telemetry) ---------------
int          StateRingIsInited(void);
int          StateRingSlotSize(void);           // bytes per slot, 0 if uninit
int          StateRingSlotCount(void);          // number of slots
long long    StateRingPoolBytes(void);          // total pinned bytes
int          StateRingFrameResident(int nFrame);// 1 if that frame is in the ring
unsigned int StateRingChecksumForFrame(int nFrame); // 0 if not resident

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_STATE_RING_H
