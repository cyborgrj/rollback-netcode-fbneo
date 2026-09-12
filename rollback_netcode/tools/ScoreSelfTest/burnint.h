// Stub of FBNeo's burnint.h, just enough for match_score.cpp to compile
// outside the emulator. The real one drags in half the burner and cannot be
// included from here - see fbneo/README.md.
#ifndef ROLLBACK_SELFTEST_BURNINT_H
#define ROLLBACK_SELFTEST_BURNINT_H

#define DRV_NAME 0

extern "C" char* BurnDrvGetTextA(unsigned int i);

#endif
