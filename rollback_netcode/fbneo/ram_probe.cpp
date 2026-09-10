// ---------------------------------------------------------------------------
// ram_probe.cpp - see ram_probe.h for the contract.
//
// The recording format, little-endian throughout, read by tools/ProbeAnalyze:
//
//   "RBFPROBE"            8 bytes
//   u32 version           1
//   u32 ramLen            bytes of work RAM
//   u32 cpuBase           address the CPU sees ram[0] at
//   u32 chunkSize         64
//   u32 interval          frames between samples
//   char game[32]         driver short name, NUL padded
//   char reserved[32]
//   then records, each a tag byte:
//     1 = full   u32 frame, ramLen bytes
//     2 = delta  u32 frame, u32 nChunks, nChunks * (u32 chunkIndex, chunkSize bytes)
//     0 = end
//
// A delta is written only while it is smaller than a full image, so the file
// degrades gracefully on a frame where everything moved (a round transition,
// typically) instead of getting bigger than the thing it compresses.
// ---------------------------------------------------------------------------
#include "burnint.h"     // BurnArea, BurnAcb, BurnAreaScan, ACB_*, BurnDrvGetTextA
#include "ram_probe.h"

#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <stdlib.h>
#include <time.h>

#define PROBE_CHUNK       64
#define PROBE_VERSION      1
// A calibration run is one match. Left going by accident it would quietly fill
// the disk, so it stops itself and says so.
#define PROBE_MAX_BYTES   (128u * 1024u * 1024u)

// ---- what "work RAM" is called, per driver family --------------------------
// The name comes from the area scan the driver itself publishes; the base is
// where the 68000 sees that block, which is the address a cheat table or a
// disassembly would quote.
static const struct { const char* szName; unsigned int nCpuBase; } kWorkRam[] = {
	{ "68K RAM",  0x100000 },   // Neo Geo cartridge work RAM (neo_run.cpp)
	{ "CpsRamFF", 0xFF0000 },   // CPS1 / CPS2 68000 work RAM (cps_mem.cpp)
};

// ---- attached RAM ----------------------------------------------------------
static const UINT8* g_pRam        = NULL;
static UINT32       g_nRamLen     = 0;
static UINT32       g_nCpuBase    = 0;
static int          g_attachTried = 0;

// ---- recorder --------------------------------------------------------------
static FILE*  g_fp        = NULL;
static UINT8* g_prev      = NULL;   // previous sample, for the delta
static int    g_interval  = 30;
static int    g_countdown = 0;
static UINT32 g_frame     = 0;
static UINT32 g_samples   = 0;
static UINT32 g_written   = 0;
static void  (*g_log)(const char*) = NULL;

static void probe_log(void (*pfn)(const char*), const char* fmt, ...)
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

// ===========================================================================
//  Attach
// ===========================================================================
static char         s_seen[512];      // every area name we walked past
static const UINT8* s_found;
static UINT32       s_foundLen;
static UINT32       s_foundBase;

static INT32 __cdecl ProbeAcb(struct BurnArea* pba)
{
	if (!pba || !pba->szName || !pba->Data || pba->nLen == 0) return 0;

	// Note every area we are offered. When a driver family we have never met
	// turns up, extending kWorkRam is then a matter of reading one log line
	// rather than of going back into the FBNeo sources to guess.
	size_t used = strlen(s_seen);
	if (used + strlen(pba->szName) + 3 < sizeof(s_seen)) {
		if (used) strcat(s_seen, ", ");
		strcat(s_seen, pba->szName);
	}

	if (s_found) return 0;
	for (unsigned i = 0; i < sizeof(kWorkRam) / sizeof(kWorkRam[0]); i++) {
		if (strcmp(pba->szName, kWorkRam[i].szName) == 0) {
			s_found     = (const UINT8*)pba->Data;
			s_foundLen  = pba->nLen;
			s_foundBase = kWorkRam[i].nCpuBase;
			break;
		}
	}
	return 0;
}

int RamProbeAttach(void (*pfnLog)(const char*))
{
	if (g_pRam) return RAM_PROBE_OK;
	if (g_attachTried) return RAM_PROBE_ERR_NO_RAM;
	g_attachTried = 1;

	s_found = NULL; s_foundLen = 0; s_foundBase = 0; s_seen[0] = '\0';

	// ACB_READ, so nothing anywhere writes to driver state: every driver gates
	// its post-load fixups on ACB_WRITE.
	INT32 (__cdecl *pPrev)(struct BurnArea*) = BurnAcb;
	BurnAcb = ProbeAcb;
	BurnAreaScan(ACB_MEMORY_RAM | ACB_READ, NULL);
	BurnAcb = pPrev;

	if (!s_found) {
		probe_log(pfnLog, "ram probe: no work RAM I recognise. areas seen: %s",
		          s_seen[0] ? s_seen : "(none)");
		return RAM_PROBE_ERR_NO_RAM;
	}

	g_pRam     = s_found;
	g_nRamLen  = s_foundLen;
	g_nCpuBase = s_foundBase;
	probe_log(pfnLog, "ram probe: work RAM %u bytes at cpu 0x%06X", g_nRamLen, g_nCpuBase);
	return RAM_PROBE_OK;
}

const unsigned char* RamProbeRam(unsigned int* pnLen, unsigned int* pnCpuBase)
{
	if (pnLen)     *pnLen     = g_nRamLen;
	if (pnCpuBase) *pnCpuBase = g_nCpuBase;
	return g_pRam;
}

unsigned char RamProbeRead8(unsigned int nCpuAddr)
{
	if (!g_pRam || g_nRamLen == 0) return 0;
	// Work RAM mirrors across the 68000 map (the Neo Geo answers at 0x10xxxx
	// and at 0x1Fxxxx, CPS from 0xFF0000 up), so wrap rather than reject: an
	// address copied from a mirror still lands on the right byte. Every size in
	// kWorkRam is a power of two, which is what makes the mask legitimate.
	return g_pRam[(nCpuAddr - g_nCpuBase) & (g_nRamLen - 1)];
}

// ===========================================================================
//  Recorder
// ===========================================================================
static void wr8(FILE* f, UINT8 v) { fputc(v, f); }

static void wr32(FILE* f, UINT32 v)
{
	// Explicit little-endian: the file outlives the machine that wrote it.
	fputc((int)( v        & 0xFF), f);
	fputc((int)((v >>  8) & 0xFF), f);
	fputc((int)((v >> 16) & 0xFF), f);
	fputc((int)((v >> 24) & 0xFF), f);
}

int RamProbeRecordStart(int nInterval, void (*pfnLog)(const char*))
{
	if (g_fp) return RAM_PROBE_OK;

	int rc = RamProbeAttach(pfnLog);
	if (rc != RAM_PROBE_OK) return rc;

	g_prev = (UINT8*)malloc(g_nRamLen);
	if (!g_prev) return RAM_PROBE_ERR_MEM;

	const char* szGame = BurnDrvGetTextA(DRV_NAME);
	if (!szGame) szGame = "game";

	char szPath[256];
	time_t t = time(NULL);
	struct tm* lt = localtime(&t);
	if (lt) {
		snprintf(szPath, sizeof(szPath), "rbf-probe-%s-%04d%02d%02d-%02d%02d%02d.rbfp",
		         szGame, lt->tm_year + 1900, lt->tm_mon + 1, lt->tm_mday,
		         lt->tm_hour, lt->tm_min, lt->tm_sec);
	} else {
		snprintf(szPath, sizeof(szPath), "rbf-probe-%s.rbfp", szGame);
	}

	g_fp = fopen(szPath, "wb");
	if (!g_fp) {
		free(g_prev); g_prev = NULL;
		probe_log(pfnLog, "ram probe: could not open %s for writing", szPath);
		return RAM_PROBE_ERR_FILE;
	}

	g_interval  = (nInterval > 0) ? nInterval : 30;
	g_countdown = 0;      // the first frame we see is sample 0
	g_frame     = 0;
	g_samples   = 0;
	g_written   = 0;
	g_log       = pfnLog;

	char szHdrGame[32]; memset(szHdrGame, 0, sizeof(szHdrGame));
	strncpy(szHdrGame, szGame, sizeof(szHdrGame) - 1);
	char szPad[32];     memset(szPad, 0, sizeof(szPad));

	fwrite("RBFPROBE", 1, 8, g_fp);
	wr32(g_fp, PROBE_VERSION);
	wr32(g_fp, g_nRamLen);
	wr32(g_fp, g_nCpuBase);
	wr32(g_fp, PROBE_CHUNK);
	wr32(g_fp, (UINT32)g_interval);
	fwrite(szHdrGame, 1, sizeof(szHdrGame), g_fp);
	fwrite(szPad,     1, sizeof(szPad),     g_fp);

	probe_log(pfnLog, "ram probe: recording %s every %d frames", szPath, g_interval);
	return RAM_PROBE_OK;
}

void RamProbeFrame(void)
{
	if (!g_fp) return;

	g_frame++;
	if (g_countdown > 0) { g_countdown--; return; }
	g_countdown = g_interval - 1;

	const UINT32 nChunks = (g_nRamLen + PROBE_CHUNK - 1) / PROBE_CHUNK;

	if (g_samples == 0) {
		wr8(g_fp, 1);
		wr32(g_fp, g_frame);
		fwrite(g_pRam, 1, g_nRamLen, g_fp);
		g_written += 5 + g_nRamLen;
	} else {
		UINT32 nDirty = 0;
		for (UINT32 c = 0; c < nChunks; c++) {
			UINT32 off = c * PROBE_CHUNK;
			UINT32 len = (off + PROBE_CHUNK <= g_nRamLen) ? PROBE_CHUNK : (g_nRamLen - off);
			if (memcmp(g_pRam + off, g_prev + off, len) != 0) nDirty++;
		}

		if (nDirty * (4 + PROBE_CHUNK) >= g_nRamLen) {
			// Everything moved. Cheaper, and simpler to read back, as an image.
			wr8(g_fp, 1);
			wr32(g_fp, g_frame);
			fwrite(g_pRam, 1, g_nRamLen, g_fp);
			g_written += 5 + g_nRamLen;
		} else {
			wr8(g_fp, 2);
			wr32(g_fp, g_frame);
			wr32(g_fp, nDirty);
			for (UINT32 c = 0; c < nChunks; c++) {
				UINT32 off = c * PROBE_CHUNK;
				UINT32 len = (off + PROBE_CHUNK <= g_nRamLen) ? PROBE_CHUNK : (g_nRamLen - off);
				if (memcmp(g_pRam + off, g_prev + off, len) == 0) continue;
				wr32(g_fp, c);
				fwrite(g_pRam + off, 1, len, g_fp);
				for (UINT32 k = len; k < PROBE_CHUNK; k++) fputc(0, g_fp);  // ragged last chunk
			}
			g_written += 9 + nDirty * (4 + PROBE_CHUNK);
		}
	}

	memcpy(g_prev, g_pRam, g_nRamLen);
	g_samples++;

	if (g_written >= PROBE_MAX_BYTES) {
		probe_log(g_log, "ram probe: %u MB recorded - stopping before this fills "
		                 "the disk. Far more than one match needs.",
		          g_written / (1024u * 1024u));
		RamProbeRecordStop();
	}
}

void RamProbeRecordStop(void)
{
	if (!g_fp) return;

	wr8(g_fp, 0);
	fclose(g_fp);
	g_fp = NULL;

	free(g_prev);
	g_prev = NULL;

	probe_log(g_log, "ram probe: %u samples over %u frames, %u KB.",
	          g_samples, g_frame, (g_written + 1023) / 1024);
	g_log = NULL;
}

int RamProbeIsRecording(void) { return g_fp != NULL; }
