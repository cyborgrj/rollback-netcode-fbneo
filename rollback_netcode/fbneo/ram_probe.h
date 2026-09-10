// ---------------------------------------------------------------------------
// ram_probe.h - Find the game's work RAM, and record how it changes.
//
// Everything we want for the scoreboard (rounds won, character picked, who
// finally won) is already in the game's memory; nothing needs to be reported by
// the players, which is the only version of this that cannot be lied about.
// What we do NOT have is a map of where those bytes are - each driver keeps its
// own, and no such table ships with FBNeo.
//
// So this module has two halves:
//
//   1. Attach. Ask BurnAreaScan for the 68000 work RAM of the running driver
//      and keep the pointer. Driver-agnostic: the area has a name, the name
//      tells us the CPU base, and everything downstream can then speak in the
//      addresses a cheat file or a disassembly would use (0xFF8xxx on CPS,
//      0x10Axxx on Neo Geo).
//
//   2. Record. Once per frame, snapshot that RAM into a file - full image
//      first, then only the 64-byte chunks that changed. A three minute match
//      costs a couple of megabytes, and out of that time series the addresses
//      fall out by shape alone: a round counter is a byte that sits at 0, steps
//      to 1, to 2, and stops; a character id is a byte that is set once at the
//      select screen and never moves again.
//
// Recording is a calibration tool, not something a real match runs. Play one
// offline match per game with -rbfprobe, hand the file to the analyser in
// tools/ProbeAnalyze, and the result is the per-game table the live reader
// needs. The attach half is cheap and is what the live reader will use.
//
// Threading: emulation thread only, like everything else that touches driver
// memory.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_RAM_PROBE_H
#define ROLLBACK_RAM_PROBE_H

#ifdef __cplusplus
extern "C" {
#endif

enum RamProbeResult {
	RAM_PROBE_OK          =  0,
	RAM_PROBE_ERR_ARG     = -1,
	RAM_PROBE_ERR_NO_RAM  = -2,   // no work RAM area we recognise - see the log
	RAM_PROBE_ERR_MEM     = -3,
	RAM_PROBE_ERR_FILE    = -4
};

// Locate the running driver's work RAM. Call once, after BurnDrvInit. Cheap and
// idempotent; every other entry point calls it for you.
int RamProbeAttach(void (*pfnLog)(const char*));

// The work RAM, or NULL when we could not identify it. *pnLen is its size and
// *pnCpuBase the address the CPU sees it at, so a documented address maps as
// base[addr - cpuBase]. Both out params are optional.
const unsigned char* RamProbeRam(unsigned int* pnLen, unsigned int* pnCpuBase);

// One byte of work RAM by CPU address, or 0 when the address is outside it (or
// nothing is attached). Meant for the per-game scoreboard table.
unsigned char RamProbeRead8(unsigned int nCpuAddr);

// ---- recording (calibration) ----------------------------------------------

// Start writing rbf-probe-<game>-<date>.rbfp next to the emulator. nInterval is
// the gap between samples in frames (<=0 picks 30, i.e. two per second).
int  RamProbeRecordStart(int nInterval, void (*pfnLog)(const char*));

// One sample, if this frame is due. No-op when no recording is running, so the
// per-frame call site does not have to know.
void RamProbeFrame(void);

// Close the file. Safe to call unconditionally.
void RamProbeRecordStop(void);

int  RamProbeIsRecording(void);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_RAM_PROBE_H
