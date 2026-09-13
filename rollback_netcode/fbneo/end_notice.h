// ---------------------------------------------------------------------------
// end_notice.h - the "FT acabou" notice at the end of a netplay session.
//
// A message box that closes itself. The end of a first-to is not an error, so
// it must not look like the "conexao caiu" warning, and it must not sit there
// waiting for a click either: the session is already over and the emulator is
// about to close. The player can dismiss it early; if they do nothing, it goes
// away on its own after nSeconds, counting down in its own text.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_END_NOTICE_H
#define ROLLBACK_END_NOTICE_H

#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

// Blocks until the box is closed (click, Enter, Esc or the countdown). szBody
// is shown as-is, followed by a blank line and "Fechando em N s...".
void EndNoticeShow(const wchar_t* szTitle, const wchar_t* szBody, int nSeconds);

// ANSI code page -> wide, for names that came through TCHARToANSI. An empty or
// unconvertible name becomes szFallback. Always terminates out.
void EndNoticeWiden(const char* sz, const wchar_t* szFallback, wchar_t* out, int n);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_END_NOTICE_H
