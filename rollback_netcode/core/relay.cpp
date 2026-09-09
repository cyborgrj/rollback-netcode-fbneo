// ---------------------------------------------------------------------------
// relay.cpp - see relay.h for the contract.
// ---------------------------------------------------------------------------
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <process.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>

#include "relay.h"

#define RELAY_MAGIC        "RBF1"
#define RELAY_PUB_RING     (256 * 1024)   // ~9 min of 2p input at 60fps
#define RELAY_IO_TIMEOUT   8000           // ms, per send/recv
#define RELAY_HDR_MAX      512
#define RELAY_MAX_REC      (4 + 64)       // frame number + one frame of inputs

// ---------------------------------------------------------------------------
// small helpers
// ---------------------------------------------------------------------------
static void logf_(void (*pfn)(const char*), const char* fmt, ...)
{
	if (!pfn) return;
	char buf[400];
	va_list ap;
	va_start(ap, fmt);
	_vsnprintf(buf, sizeof(buf) - 1, fmt, ap);
	va_end(ap);
	buf[sizeof(buf) - 1] = 0;
	pfn(buf);
}

// A name is one whitespace-free token on the wire; spaces would make the
// header ambiguous, and these are only ever shown, never matched.
static void sanitize(char* dst, int cap, const char* src)
{
	int i = 0;
	if (cap <= 0) return;
	for (; src && src[i] && i < cap - 1; i++)
		dst[i] = (src[i] <= ' ' || src[i] == 0x7f) ? '_' : src[i];
	if (i == 0 && cap > 1) dst[i++] = '?';
	dst[i] = 0;
}

static SOCKET dialOut(const char* szHost, unsigned short nPort, int* pErr)
{
	char szPort[16];
	sprintf(szPort, "%u", (unsigned)nPort);

	struct addrinfo hints, *res = NULL;
	memset(&hints, 0, sizeof(hints));
	hints.ai_family   = AF_INET;
	hints.ai_socktype = SOCK_STREAM;
	if (getaddrinfo(szHost, szPort, &hints, &res) != 0 || !res) {
		*pErr = RELAY_ERR_RESOLVE;
		return INVALID_SOCKET;
	}

	SOCKET s = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
	if (s == INVALID_SOCKET) {
		freeaddrinfo(res);
		*pErr = RELAY_ERR_SOCKET;
		return INVALID_SOCKET;
	}

	int rc = connect(s, res->ai_addr, (int)res->ai_addrlen);
	freeaddrinfo(res);
	if (rc != 0) {
		closesocket(s);
		*pErr = RELAY_ERR_CONNECT;
		return INVALID_SOCKET;
	}

	DWORD to = RELAY_IO_TIMEOUT;
	setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&to, sizeof(to));
	setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char*)&to, sizeof(to));
	int one = 1;
	setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char*)&one, sizeof(one));

	*pErr = RELAY_OK;
	return s;
}

static int sendAll(SOCKET s, const void* p, int n)
{
	const char* b = (const char*)p;
	while (n > 0) {
		int k = send(s, b, n, 0);
		if (k <= 0) return RELAY_ERR_CLOSED;
		b += k;
		n -= k;
	}
	return RELAY_OK;
}

static int recvAll(SOCKET s, void* p, int n)
{
	char* b = (char*)p;
	while (n > 0) {
		int k = recv(s, b, n, 0);
		if (k <= 0) return RELAY_ERR_CLOSED;
		b += k;
		n -= k;
	}
	return RELAY_OK;
}

// Read one newline-terminated line. Byte at a time: it runs twice per session.
static int recvLine(SOCKET s, char* out, int cap)
{
	int i = 0;
	while (i < cap - 1) {
		char c;
		int k = recv(s, &c, 1, 0);
		if (k <= 0) return RELAY_ERR_CLOSED;
		if (c == '\n') break;
		if (c != '\r') out[i++] = c;
	}
	out[i] = 0;
	return RELAY_OK;
}

// ===========================================================================
//  Publisher
// ===========================================================================
static struct {
	int              up;          // socket alive and header accepted
	volatile LONG    stop;
	volatile LONG    broken;      // ring overflowed or socket died
	HANDLE           thread;
	CRITICAL_SECTION cs;
	int              csReady;

	char             szHost[128];
	unsigned short   nPort;
	char             szMatchId[40];
	RelayStreamInfo  info;
	unsigned char*   pState;
	int              nStateLen;
	void           (*pfnLog)(const char*);

	unsigned char*   pRing;
	int              nHead;       // write cursor (emu thread)
	int              nTail;       // read cursor  (worker)
	int              nUsed;
} P;

static int pubRingWrite(const void* p, int n)
{
	if (P.nUsed + n > RELAY_PUB_RING) return 0;
	const unsigned char* b = (const unsigned char*)p;
	for (int i = 0; i < n; i++) {
		P.pRing[P.nHead] = b[i];
		P.nHead = (P.nHead + 1) % RELAY_PUB_RING;
	}
	P.nUsed += n;
	return 1;
}

// Worker: connect, announce, ship the opening state, then drain the ring.
static unsigned __stdcall pubThread(void*)
{
	int err = RELAY_OK;
	SOCKET s = dialOut(P.szHost, P.nPort, &err);
	if (s == INVALID_SOCKET) {
		logf_(P.pfnLog, "relay: publish connect failed (%d) - match will not be watchable", err);
		InterlockedExchange(&P.broken, 1);
		return 0;
	}

	char szP1[RELAY_MAX_NAME], szP2[RELAY_MAX_NAME], szGame[64];
	sanitize(szP1, sizeof(szP1), P.info.szP1);
	sanitize(szP2, sizeof(szP2), P.info.szP2);
	sanitize(szGame, sizeof(szGame), P.info.szGame);

	char hdr[RELAY_HDR_MAX];
	int n = _snprintf(hdr, sizeof(hdr) - 1, "%s PUB %s %s %d %d %d %s %s\n",
	                  RELAY_MAGIC, P.szMatchId, szGame,
	                  P.info.nPlayers, P.info.nInputBytes, P.nStateLen, szP1, szP2);
	if (n < 0) n = (int)strlen(hdr);

	if (sendAll(s, hdr, n) != RELAY_OK ||
	    (P.nStateLen > 0 && sendAll(s, P.pState, P.nStateLen) != RELAY_OK)) {
		logf_(P.pfnLog, "relay: publish header/state failed - match will not be watchable");
		closesocket(s);
		InterlockedExchange(&P.broken, 1);
		return 0;
	}

	logf_(P.pfnLog, "relay: publishing match %s (%d byte state)", P.szMatchId, P.nStateLen);
	P.up = 1;

	unsigned char chunk[4096];
	while (!P.stop) {
		int take = 0;
		EnterCriticalSection(&P.cs);
		while (take < (int)sizeof(chunk) && P.nUsed > 0) {
			chunk[take++] = P.pRing[P.nTail];
			P.nTail = (P.nTail + 1) % RELAY_PUB_RING;
			P.nUsed--;
		}
		LeaveCriticalSection(&P.cs);

		if (take == 0) { Sleep(4); continue; }
		if (sendAll(s, chunk, take) != RELAY_OK) {
			logf_(P.pfnLog, "relay: publish stream dropped");
			break;
		}
	}

	P.up = 0;
	closesocket(s);
	InterlockedExchange(&P.broken, 1);
	return 0;
}

int RelayPublishStart(const char* szHost, unsigned short nPort,
                      const char* szMatchId, const RelayStreamInfo* info,
                      const void* pState, int nStateLen,
                      void (*pfnLog)(const char*))
{
	if (!szHost || !*szHost || !nPort || !szMatchId || !*szMatchId || !info) return RELAY_ERR_ARG;
	if (info->nPlayers < 1 || info->nInputBytes < 1)                         return RELAY_ERR_ARG;
	if (nStateLen < 0 || (nStateLen > 0 && !pState))                         return RELAY_ERR_ARG;
	if (info->nPlayers * info->nInputBytes > RELAY_MAX_REC - 4)              return RELAY_ERR_ARG;

	RelayPublishStop();
	memset(&P, 0, sizeof(P));

	P.pRing  = (unsigned char*)malloc(RELAY_PUB_RING);
	P.pState = nStateLen > 0 ? (unsigned char*)malloc(nStateLen) : NULL;
	if (!P.pRing || (nStateLen > 0 && !P.pState)) {
		free(P.pRing);
		free(P.pState);
		memset(&P, 0, sizeof(P));
		return RELAY_ERR_MEMORY;
	}
	if (nStateLen > 0) memcpy(P.pState, pState, nStateLen);
	P.nStateLen = nStateLen;

	strncpy(P.szHost, szHost, sizeof(P.szHost) - 1);
	strncpy(P.szMatchId, szMatchId, sizeof(P.szMatchId) - 1);
	P.nPort  = nPort;
	P.info   = *info;
	P.pfnLog = pfnLog;

	InitializeCriticalSection(&P.cs);
	P.csReady = 1;

	P.thread = (HANDLE)_beginthreadex(NULL, 0, pubThread, NULL, 0, NULL);
	if (!P.thread) {
		DeleteCriticalSection(&P.cs);
		free(P.pRing);
		free(P.pState);
		memset(&P, 0, sizeof(P));
		return RELAY_ERR_SOCKET;
	}
	return RELAY_OK;
}

void RelayPublishInputs(int nFrame, const void* pInputs, int nBytes)
{
	if (!P.csReady || P.broken || nBytes <= 0 || nBytes > RELAY_MAX_REC - 4) return;

	unsigned char rec[RELAY_MAX_REC];
	rec[0] = (unsigned char)(nFrame & 0xff);
	rec[1] = (unsigned char)((nFrame >> 8) & 0xff);
	rec[2] = (unsigned char)((nFrame >> 16) & 0xff);
	rec[3] = (unsigned char)((nFrame >> 24) & 0xff);
	memcpy(rec + 4, pInputs, nBytes);

	EnterCriticalSection(&P.cs);
	int ok = pubRingWrite(rec, 4 + nBytes);
	LeaveCriticalSection(&P.cs);

	if (!ok) {
		// A gap would desync every viewer, so a full ring ends the broadcast
		// rather than quietly skipping frames. The ring holds ~9 minutes: if we
		// got here the socket has been stuck for a very long time.
		InterlockedExchange(&P.broken, 1);
		logf_(P.pfnLog, "relay: publish buffer full - stopping the broadcast");
	}
}

int RelayPublishIsUp(void) { return P.up && !P.broken; }

void RelayPublishStop(void)
{
	if (!P.csReady) return;
	InterlockedExchange(&P.stop, 1);
	if (P.thread) {
		WaitForSingleObject(P.thread, 3000);
		CloseHandle(P.thread);
	}
	DeleteCriticalSection(&P.cs);
	free(P.pRing);
	free(P.pState);
	memset(&P, 0, sizeof(P));
}

// ===========================================================================
//  Subscriber
// ===========================================================================
static struct {
	volatile LONG    stop;
	volatile LONG    ended;       // host stopped publishing / socket closed
	HANDLE           thread;
	CRITICAL_SECTION cs;
	int              csReady;

	SOCKET           sock;
	RelayStreamInfo  info;
	unsigned char*   pState;

	unsigned char*   pFrames;     // nCap * nRecBytes, grown by the worker only
	int              nRecBytes;   // players * inputBytes
	int              nCap;        // capacity in frames
	int              nRecv;       // frames received
	int              nRead;       // frames handed to the emu thread

	void           (*pfnLog)(const char*);
} S;

// Worker only. Doubling growth - the emu thread never allocates. A whole hour
// of a 2 player match is under 2MB, so this stays small.
static int subReserve(int nFrames)
{
	if (nFrames <= S.nCap) return 1;
	int cap = S.nCap ? S.nCap : 4096;
	while (cap < nFrames) cap *= 2;
	unsigned char* p = (unsigned char*)realloc(S.pFrames, (size_t)cap * S.nRecBytes);
	if (!p) return 0;
	S.pFrames = p;
	S.nCap = cap;
	return 1;
}

static unsigned __stdcall subThread(void*)
{
	unsigned char rec[RELAY_MAX_REC];
	const int nRec = 4 + S.nRecBytes;

	while (!S.stop) {
		if (recvAll(S.sock, rec, nRec) != RELAY_OK) break;

		int frame = (int)((unsigned)rec[0] | ((unsigned)rec[1] << 8) |
		                  ((unsigned)rec[2] << 16) | ((unsigned)rec[3] << 24));

		EnterCriticalSection(&S.cs);
		int expected = S.nRecv;
		int ok = (frame == expected) && subReserve(S.nRecv + 1);
		if (ok) {
			memcpy(S.pFrames + (size_t)S.nRecv * S.nRecBytes, rec + 4, S.nRecBytes);
			S.nRecv++;
		}
		LeaveCriticalSection(&S.cs);

		if (!ok) {
			// Out of order or out of memory: nothing past this point can be
			// trusted, so stop rather than show a fight that never happened.
			logf_(S.pfnLog, "relay: watch stream broke at frame %d (expected %d)", frame, expected);
			break;
		}
	}

	InterlockedExchange(&S.ended, 1);
	return 0;
}

int RelayWatchStart(const char* szHost, unsigned short nPort,
                    const char* szMatchId, int nTimeoutMs,
                    void (*pfnLog)(const char*))
{
	if (!szHost || !*szHost || !nPort || !szMatchId || !*szMatchId) return RELAY_ERR_ARG;

	RelayWatchStop();
	memset(&S, 0, sizeof(S));
	S.sock = INVALID_SOCKET;
	S.pfnLog = pfnLog;

	int err = RELAY_OK;
	SOCKET s = dialOut(szHost, nPort, &err);
	if (s == INVALID_SOCKET) return err;

	if (nTimeoutMs > 0) {
		DWORD to = (DWORD)nTimeoutMs;
		setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&to, sizeof(to));
	}

	char req[128];
	int n = _snprintf(req, sizeof(req) - 1, "%s SUB %s\n", RELAY_MAGIC, szMatchId);
	if (n < 0) n = (int)strlen(req);
	if (sendAll(s, req, n) != RELAY_OK) { closesocket(s); return RELAY_ERR_CLOSED; }

	char line[RELAY_HDR_MAX];
	if (recvLine(s, line, sizeof(line)) != RELAY_OK) { closesocket(s); return RELAY_ERR_CLOSED; }

	char magic[8] = "", verb[8] = "", game[64] = "";
	char p1[RELAY_MAX_NAME] = "", p2[RELAY_MAX_NAME] = "";
	int players = 0, inputBytes = 0, stateLen = 0;
	int got = sscanf(line, "%7s %7s %63s %d %d %d %31s %31s",
	                 magic, verb, game, &players, &inputBytes, &stateLen, p1, p2);

	if (got < 8 || strcmp(magic, RELAY_MAGIC) != 0 || strcmp(verb, "HDR") != 0 ||
	    players < 1 || players > 4 || inputBytes < 1 || inputBytes > 8 ||
	    players * inputBytes > RELAY_MAX_REC - 4 ||
	    stateLen < 0 || stateLen > 64 * 1024 * 1024) {
		logf_(pfnLog, "relay: watch refused: %s", line);
		closesocket(s);
		return RELAY_ERR_PROTOCOL;
	}

	strncpy(S.info.szGame, game, sizeof(S.info.szGame) - 1);
	strncpy(S.info.szP1, p1, sizeof(S.info.szP1) - 1);
	strncpy(S.info.szP2, p2, sizeof(S.info.szP2) - 1);
	S.info.nPlayers    = players;
	S.info.nInputBytes = inputBytes;
	S.info.nStateLen   = stateLen;
	S.nRecBytes        = players * inputBytes;

	if (stateLen > 0) {
		S.pState = (unsigned char*)malloc(stateLen);
		if (!S.pState) { closesocket(s); return RELAY_ERR_MEMORY; }
		if (recvAll(s, S.pState, stateLen) != RELAY_OK) {
			free(S.pState);
			S.pState = NULL;
			closesocket(s);
			return RELAY_ERR_CLOSED;
		}
	}

	// Back to the short timeout: from here a stall only means the host is idle.
	DWORD to = RELAY_IO_TIMEOUT;
	setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&to, sizeof(to));

	S.sock = s;
	InitializeCriticalSection(&S.cs);
	S.csReady = 1;

	S.thread = (HANDLE)_beginthreadex(NULL, 0, subThread, NULL, 0, NULL);
	if (!S.thread) {
		DeleteCriticalSection(&S.cs);
		S.csReady = 0;
		closesocket(s);
		free(S.pState);
		memset(&S, 0, sizeof(S));
		return RELAY_ERR_SOCKET;
	}

	logf_(pfnLog, "relay: watching %s (%s, %s vs %s, %d byte state)",
	      szMatchId, game, p1, p2, stateLen);
	return RELAY_OK;
}

int RelayWatchInfo(RelayStreamInfo* out)
{
	if (!out || !S.csReady) return RELAY_ERR_ARG;
	*out = S.info;
	return RELAY_OK;
}

const void* RelayWatchState(int* pnLen)
{
	if (pnLen) *pnLen = S.info.nStateLen;
	return S.pState;
}

int RelayWatchPending(void)
{
	if (!S.csReady) return 0;
	EnterCriticalSection(&S.cs);
	int n = S.nRecv - S.nRead;
	LeaveCriticalSection(&S.cs);
	return n;
}

int RelayWatchEnded(void) { return S.csReady ? (int)S.ended : 1; }

int RelayWatchNext(void* pOut, int nBytes)
{
	if (!S.csReady || !pOut || nBytes != S.nRecBytes) return RELAY_ERR_ARG;

	EnterCriticalSection(&S.cs);
	int have = S.nRead < S.nRecv;
	if (have) {
		memcpy(pOut, S.pFrames + (size_t)S.nRead * S.nRecBytes, S.nRecBytes);
		S.nRead++;
	}
	LeaveCriticalSection(&S.cs);

	if (have)    return 1;
	if (S.ended) return RELAY_ERR_CLOSED;
	return 0;
}

void RelayWatchStop(void)
{
	if (!S.csReady) return;
	InterlockedExchange(&S.stop, 1);
	if (S.sock != INVALID_SOCKET) {
		shutdown(S.sock, SD_BOTH);
		closesocket(S.sock);
		S.sock = INVALID_SOCKET;
	}
	if (S.thread) {
		WaitForSingleObject(S.thread, 3000);
		CloseHandle(S.thread);
	}
	DeleteCriticalSection(&S.cs);
	free(S.pState);
	free(S.pFrames);
	memset(&S, 0, sizeof(S));
}
