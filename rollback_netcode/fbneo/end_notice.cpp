// ---------------------------------------------------------------------------
// end_notice.cpp - see end_notice.h.
//
// Plain MessageBox plus a thread timer, instead of a custom dialog: it looks
// like every other warning the emulator shows, and needs no resource script.
//
// How the countdown reaches the box: MessageBox runs its own modal loop on
// this thread, and that loop dispatches thread messages too - so a timer
// created with a NULL window keeps firing while the box is up. Each tick finds
// the box (the only "#32770" dialog this thread owns at that moment), rewrites
// its text, and on the last one ends the dialog with IDOK, as if Enter was
// pressed.
//
// MessageBoxTimeout would do the closing in one call, but it is undocumented.
// ---------------------------------------------------------------------------
#include <windows.h>
#include <wchar.h>

#include "end_notice.h"

// MessageBox gives its text control this id.
#define EN_TEXT_ID 0xFFFF

static const wchar_t* s_body;
static int            s_left;

static BOOL CALLBACK findBox(HWND hWnd, LPARAM lParam)
{
	wchar_t cls[16];
	if (GetClassNameW(hWnd, cls, 16) && wcscmp(cls, L"#32770") == 0) {
		*(HWND*)lParam = hWnd;
		return FALSE;
	}
	return TRUE;
}

static void compose(wchar_t* out, size_t n, int nLeft)
{
	_snwprintf(out, n, L"%ls\n\nFechando em %d s...", s_body ? s_body : L"", nLeft);
	out[n - 1] = 0;
}

static void CALLBACK tick(HWND, UINT, UINT_PTR, DWORD)
{
	HWND box = NULL;
	EnumThreadWindows(GetCurrentThreadId(), findBox, (LPARAM)&box);
	if (!box) return;   // not shown yet - count this second on the next tick

	if (s_left <= 0) return;   // already closing
	if (--s_left == 0) {
		// Not a posted WM_COMMAND/IDOK: the box ignores that (measured on
		// 13/09 - it kept ticking at 0, -1, -2...). EndDialog is allowed here
		// because this runs on the thread that owns the box.
		EndDialog(box, IDOK);
		return;
	}

	// Same length as the text the box was sized for ("3" -> "2"), so the
	// label never needs to grow.
	wchar_t text[1024];
	compose(text, sizeof(text) / sizeof(text[0]), s_left);
	SetDlgItemTextW(box, EN_TEXT_ID, text);
}

void EndNoticeWiden(const char* sz, const wchar_t* szFallback, wchar_t* out, int n)
{
	if (!out || n < 1) return;
	if (!sz || !sz[0] || MultiByteToWideChar(CP_ACP, 0, sz, -1, out, n) <= 0)
		wcsncpy(out, szFallback ? szFallback : L"", n);
	out[n - 1] = 0;
}

void EndNoticeShow(const wchar_t* szTitle, const wchar_t* szBody, int nSeconds)
{
	if (nSeconds < 1) nSeconds = 1;
	if (nSeconds > 9) nSeconds = 9;   // one digit, see tick()

	s_body = szBody;
	s_left = nSeconds;

	wchar_t text[1024];
	compose(text, sizeof(text) / sizeof(text[0]), s_left);

	UINT_PTR id = SetTimer(NULL, 0, 1000, tick);
	// TOPMOST: the game window may be fullscreen, and a box behind it would
	// leave the player looking at a frozen game for three seconds.
	MessageBoxW(NULL, text, szTitle, MB_OK | MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND);
	if (id) KillTimer(NULL, id);

	s_body = NULL;
}
