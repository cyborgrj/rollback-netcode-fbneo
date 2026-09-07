// ---------------------------------------------------------------------------
// state_ring.cpp - see state_ring.h for the design contract.
//
// This module is a hardened, N-slot generalisation of FBNeo's existing
// StateRunAheadSave/Load (src/burn/burn.cpp): same BurnAreaScan idiom, same
// volatile set, but a fixed circular pool instead of a single malloc'd buffer,
// plus a rolling content hash for desync detection.
// ---------------------------------------------------------------------------
#include "burnint.h"     // -> burn.h -> state.h : BurnArea, BurnAcb, BurnAreaScan, ACB_*
#include "state_ring.h"

#include <stdlib.h>
#include <string.h>

// nSlots default. libggpo's prediction window is 8 frames and it keeps
// ~MAX_PREDICTION+2 saved frames internally; 16 gives generous headroom so a
// slot libggpo still references is never overwritten before it frees it, and
// leaves room for the control agent to peek at recent frames.
#define STATE_RING_DEFAULT_SLOTS 16

// The volatile set we capture. Identical to FBNeo RunAhead.
#define STATE_RING_SCAN_FLAGS (ACB_FULLSCAN | ACB_RUNAHEAD)

// FNV-1a offset basis, used as the per-state hash seed.
#define STATE_RING_HASH_SEED 0x811c9dc5u

// ---- module state (emulation thread only) ---------------------------------
static UINT8*  g_pool         = NULL;   // g_slots * g_slotSize contiguous bytes
static INT32   g_slotSize     = 0;      // bytes per slot, fixed for the session
static INT32   g_slots        = 0;      // power of two
static INT32   g_slotMask     = 0;      // g_slots - 1
static INT32*  g_slotFrame    = NULL;   // frame resident in slot i, or -1
static UINT32* g_slotChecksum = NULL;   // hash of slot i payload
static bool    g_inited       = false;

// ---- transient cursors used by the BurnAcb callbacks ---------------------
static UINT8*  s_cur      = NULL;
static UINT8*  s_curEnd   = NULL;
static UINT32  s_hash     = STATE_RING_HASH_SEED;
static INT32   s_bytes    = 0;
static bool    s_overflow = false;
static bool    s_wantHash = false;

#define SLOT_EMPTY (-1)

// ---- rolling content hash ----------------------------------------------------
// Word-wise FNV-1a-ish mix. Endianness-consistent within one build/arch, which
// is the current same-binary peering assumption; a cross-arch canonical form is
// a later concern. Kept optional because it is a full pass over the state.
static void hash_add(const UINT8* p, INT32 n)
{
	UINT32 h = s_hash;
	INT32 i = 0;
	for (; i + 4 <= n; i += 4) {
		UINT32 w;
		memcpy(&w, p + i, 4);
		h = (h ^ w) * 0x01000193u;
		h = (h << 13) | (h >> 19);
	}
	for (; i < n; i++) {
		h = (h ^ p[i]) * 0x01000193u;
	}
	s_hash = h;
}

// ---- BurnAcb callbacks -----------------------------------------------------
static INT32 __cdecl RingLenAcb(struct BurnArea* pba)
{
	s_bytes += pba->nLen;
	return 0;
}

// emulator -> pool
static INT32 __cdecl RingReadAcb(struct BurnArea* pba)
{
	if (s_cur + pba->nLen > s_curEnd) { s_overflow = true; return 1; }
	memcpy(s_cur, pba->Data, pba->nLen);
	if (s_wantHash) hash_add(s_cur, pba->nLen);
	s_cur   += pba->nLen;
	s_bytes += pba->nLen;
	return 0;
}

// pool -> emulator
static INT32 __cdecl RingWriteAcb(struct BurnArea* pba)
{
	if (s_cur + pba->nLen > s_curEnd) { s_overflow = true; return 1; }
	memcpy(pba->Data, s_cur, pba->nLen);
	s_cur   += pba->nLen;
	s_bytes += pba->nLen;
	return 0;
}

// ---- size probe ----------------------------------------------------------
static INT32 StateRingProbeSize(void)
{
	s_bytes = 0;
	BurnAcb = RingLenAcb;
	BurnAreaScan(STATE_RING_SCAN_FLAGS | ACB_READ, NULL);
	return s_bytes;
}

// ---- lifecycle ---------------------------------------------------------------
int StateRingInit(int nMinSlots)
{
	if (g_inited) StateRingExit();

	const INT32 measured = StateRingProbeSize();
	if (measured <= 0) return STATE_RING_ERR_SIZE;

	INT32 want = (nMinSlots > 0) ? nMinSlots : STATE_RING_DEFAULT_SLOTS;
	INT32 pow2 = 1;
	while (pow2 < want) pow2 <<= 1;

	g_slotSize = measured;
	g_slots    = pow2;
	g_slotMask = pow2 - 1;

	g_pool         = (UINT8*)malloc((size_t)g_slotSize * (size_t)g_slots);
	g_slotFrame    = (INT32*)malloc(sizeof(INT32) * (size_t)g_slots);
	g_slotChecksum = (UINT32*)malloc(sizeof(UINT32) * (size_t)g_slots);
	if (!g_pool || !g_slotFrame || !g_slotChecksum) {
		StateRingExit();
		return STATE_RING_ERR_ALLOC;
	}

	StateRingReset();
	g_inited = true;

	bprintf(0, _T(" ** StateRing: %d slots x $%x bytes = %d KB pinned.\n"),
	        g_slots, g_slotSize, (INT32)(((long long)g_slotSize * g_slots) / 1024));
	return STATE_RING_OK;
}

void StateRingExit(void)
{
	if (g_pool)         free(g_pool);
	if (g_slotFrame)    free(g_slotFrame);
	if (g_slotChecksum) free(g_slotChecksum);
	g_pool = NULL;
	g_slotFrame = NULL;
	g_slotChecksum = NULL;
	g_slotSize = g_slots = g_slotMask = 0;
	g_inited = false;
}

void StateRingReset(void)
{
	for (INT32 i = 0; i < g_slots; i++) {
		g_slotFrame[i]    = SLOT_EMPTY;
		g_slotChecksum[i] = 0;
	}
}

// ---- save / load -------------------------------------------------------------
int StateRingSave(int nFrame, void** ppBuf, int* pnLen, unsigned int* pnChecksum)
{
	if (!g_inited)   return STATE_RING_ERR_STATE;
	if (nFrame < 0)  return STATE_RING_ERR_ARG;

	const INT32 slot = nFrame & g_slotMask;
	UINT8* dst = g_pool + (size_t)slot * (size_t)g_slotSize;

	s_cur      = dst;
	s_curEnd   = dst + g_slotSize;
	s_hash     = STATE_RING_HASH_SEED;
	s_bytes    = 0;
	s_overflow = false;
	s_wantHash = (pnChecksum != NULL);

	BurnAcb = RingReadAcb;
	BurnAreaScan(STATE_RING_SCAN_FLAGS | ACB_READ, NULL);

	if (s_overflow)              return STATE_RING_ERR_OVERFLOW;
	if (s_bytes != g_slotSize)   return STATE_RING_ERR_SIZE_DRIFT;

	g_slotFrame[slot]    = nFrame;
	g_slotChecksum[slot] = s_wantHash ? s_hash : 0;

	if (ppBuf)      *ppBuf      = dst;
	if (pnLen)      *pnLen      = g_slotSize;
	if (pnChecksum) *pnChecksum = s_hash;
	return STATE_RING_OK;
}

int StateRingLoad(int nFrame, const void* pBuf, int nLen)
{
	if (!g_inited) return STATE_RING_ERR_STATE;

	const UINT8* src;
	if (pBuf != NULL) {
		if (nLen != g_slotSize) return STATE_RING_ERR_SIZE_DRIFT;
		src = (const UINT8*)pBuf;
	} else {
		if (nFrame < 0) return STATE_RING_ERR_ARG;
		const INT32 slot = nFrame & g_slotMask;
		if (g_slotFrame[slot] != nFrame) return STATE_RING_ERR_MISS;
		src = g_pool + (size_t)slot * (size_t)g_slotSize;
	}

	s_cur      = (UINT8*)src;
	s_curEnd   = (UINT8*)src + g_slotSize;
	s_bytes    = 0;
	s_overflow = false;

	BurnAcb = RingWriteAcb;
	BurnAreaScan(STATE_RING_SCAN_FLAGS | ACB_WRITE, NULL);

	if (s_overflow)            return STATE_RING_ERR_OVERFLOW;
	if (s_bytes != g_slotSize) return STATE_RING_ERR_SIZE_DRIFT;
	return STATE_RING_OK;
}

void StateRingFree(void* pBuf)
{
	// Buffers are borrowed views into the pinned pool - nothing to release.
	(void)pBuf;
}

// ---- introspection ---------------------------------------------------------
int          StateRingIsInited(void)        { return g_inited ? 1 : 0; }
int          StateRingSlotSize(void)        { return g_inited ? g_slotSize : 0; }
int          StateRingSlotCount(void)       { return g_inited ? g_slots : 0; }
long long    StateRingPoolBytes(void)       { return g_inited ? (long long)g_slotSize * g_slots : 0; }

int StateRingFrameResident(int nFrame)
{
	if (!g_inited || nFrame < 0) return 0;
	return (g_slotFrame[nFrame & g_slotMask] == nFrame) ? 1 : 0;
}

unsigned int StateRingChecksumForFrame(int nFrame)
{
	if (!StateRingFrameResident(nFrame)) return 0;
	return g_slotChecksum[nFrame & g_slotMask];
}
