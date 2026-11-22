/*
 * str-amp -- minimal Windows API shim so FxSound's DfxDsp sources compile on Linux.
 *
 * Only what DfxDsp actually touches: the basic types, a handful of string helpers, and the
 * registry, which DfxDsp uses as a settings store. The registry here is an in-memory map
 * (see shim_registry.cpp): str-amp sets every value it cares about each time a track opens, so
 * nothing needs to persist, and Linux has no shared registry tree to collide with.
 *
 * Copyright (C) 2025 strong-ery, Aiden A -- AGPL-3.0-or-later, like the rest of str-amp.
 */
#ifndef STRAMP_LINUX_WINDOWS_H
#define STRAMP_LINUX_WINDOWS_H

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <wchar.h>
#include <stdlib.h>
#include <stdio.h>
#include <ctype.h>
#include <math.h>
#include <stdarg.h>
#include <strings.h>
#include <wctype.h>

#ifdef __cplusplus
/* <cwchar> #undefs swprintf (and friends), so everything that drags it in has to be seen before
   the swprintf compatibility macro below is defined. */
#include <cwchar>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include <new>
#endif

typedef int BOOL;
typedef unsigned char BYTE;
typedef unsigned char UCHAR;
typedef unsigned short WORD;
typedef unsigned short USHORT;
typedef uint32_t DWORD;
typedef uint32_t UINT;
typedef uint32_t ULONG;
typedef int32_t LONG;
typedef int64_t LONGLONG;
typedef uint64_t ULONGLONG;
#define __int64 long long
typedef uint64_t UINT64;
typedef int64_t INT64;
typedef int INT;
typedef short SHORT;
typedef float FLOAT;
typedef void VOID;
typedef void *LPVOID;
typedef const void *LPCVOID;
typedef void *PVOID;
typedef void *HANDLE;
typedef void *HWND;
typedef void *HINSTANCE;
typedef void *HMODULE;
typedef BYTE *LPBYTE;
typedef DWORD *LPDWORD;
typedef DWORD *PDWORD;
typedef LONG *PLONG;
typedef intptr_t INT_PTR;
typedef uintptr_t UINT_PTR;
typedef uintptr_t DWORD_PTR;
typedef intptr_t LONG_PTR;
typedef uintptr_t ULONG_PTR;
typedef size_t SIZE_T;
typedef UINT_PTR WPARAM;
typedef LONG_PTR LPARAM;
typedef LONG_PTR LRESULT;
typedef struct _FILETIME { DWORD dwLowDateTime; DWORD dwHighDateTime; } FILETIME, *LPFILETIME;
typedef struct _SYSTEMTIME { WORD wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; } SYSTEMTIME, *LPSYSTEMTIME;
struct IMMDevice;   /* Core Audio devices are not used by the DSP itself. */

typedef char CHAR;
typedef wchar_t WCHAR;
typedef char *LPSTR;
typedef const char *LPCSTR;
typedef wchar_t *LPWSTR;
typedef const wchar_t *LPCWSTR;
typedef char TCHAR;
typedef char *LPTSTR;
typedef const char *LPCTSTR;

#ifndef TRUE
#define TRUE 1
#endif
#ifndef FALSE
#define FALSE 0
#endif
#ifndef MAX_PATH
#define MAX_PATH 260
#endif
#ifndef NULL
#define NULL 0
#endif

#define WINAPI
#define CALLBACK
#define APIENTRY
#define __cdecl
#define __stdcall
#define __fastcall
#define __declspec(x)
#define _MAX_PATH MAX_PATH
#define _T(x) x
#define TEXT(x) x
#define UNREFERENCED_PARAMETER(p) (void)(p)

#define MB_OK 0
#define MB_ICONERROR 0x10
#define MB_ICONWARNING 0x30
#define MB_ICONEXCLAMATION 0x30
#define MB_ICONINFORMATION 0x40
#define MB_TOPMOST 0
#define MB_SETFOREGROUND 0
#define MB_SYSTEMMODAL 0

/* Dialogs have nowhere to show on a headless audio thread; they were debug aids. */
static inline int MessageBoxA(HWND, const char *, const char *, UINT) { return 0; }
static inline int MessageBoxW(HWND, const wchar_t *, const wchar_t *, UINT) { return 0; }
#define MessageBox MessageBoxA

/* ---- registry (in-memory; see shim_registry.cpp) ---- */
typedef void *HKEY;
typedef DWORD REGSAM;
typedef LONG LSTATUS;
#define HKEY_CURRENT_USER   ((HKEY)(intptr_t)0x80000001)
#define HKEY_LOCAL_MACHINE  ((HKEY)(intptr_t)0x80000002)
#define HKEY_CLASSES_ROOT   ((HKEY)(intptr_t)0x80000000)
#define KEY_READ 0x20019
#define KEY_WRITE 0x20006
#define KEY_ALL_ACCESS 0xF003F
#define KEY_QUERY_VALUE 1
#define KEY_SET_VALUE 2
#define REG_SZ 1
#define REG_BINARY 3
#define REG_DWORD 4
#define REG_OPTION_NON_VOLATILE 0
#define ERROR_SUCCESS 0L
#define ERROR_FILE_NOT_FOUND 2L
#define ERROR_MORE_DATA 234L

#ifdef __cplusplus
extern "C" {
#endif
LONG RegCreateKeyExA(HKEY, const char *, DWORD, char *, DWORD, REGSAM, void *, HKEY *, DWORD *);
LONG RegCreateKeyExW(HKEY, const wchar_t *, DWORD, wchar_t *, DWORD, REGSAM, void *, HKEY *, DWORD *);
LONG RegOpenKeyExA(HKEY, const char *, DWORD, REGSAM, HKEY *);
LONG RegOpenKeyExW(HKEY, const wchar_t *, DWORD, REGSAM, HKEY *);
LONG RegQueryValueExA(HKEY, const char *, DWORD *, DWORD *, BYTE *, DWORD *);
LONG RegQueryValueExW(HKEY, const wchar_t *, DWORD *, DWORD *, BYTE *, DWORD *);
LONG RegSetValueExA(HKEY, const char *, DWORD, DWORD, const BYTE *, DWORD);
LONG RegSetValueExW(HKEY, const wchar_t *, DWORD, DWORD, const BYTE *, DWORD);
LONG RegCloseKey(HKEY);
LONG RegDeleteKeyA(HKEY, const char *);
LONG RegDeleteKeyW(HKEY, const wchar_t *);
#ifdef __cplusplus
}
#endif
#define RegCreateKeyEx RegCreateKeyExA
#define RegOpenKeyEx RegOpenKeyExA
#define RegQueryValueEx RegQueryValueExA
#define RegSetValueEx RegSetValueExA
#define RegDeleteKey RegDeleteKeyA

/* MSVC's swprintf comes in two shapes -- the old (buffer, format, ...) and the standard
   (buffer, count, format, ...) -- and reads %s as a wide string where POSIX reads narrow. The
   sources use both shapes, so both are provided and told apart by their second argument. */
#ifdef __cplusplus
extern "C" {
#endif
int stramp_vswprintf_n(wchar_t *buffer, size_t count, const wchar_t *format, va_list args);
#ifdef __cplusplus
}
static inline int stramp_swprintf(wchar_t *buffer, const wchar_t *format, ...) {
    va_list args; va_start(args, format);
    int n = stramp_vswprintf_n(buffer, (size_t)1 << 16, format, args);
    va_end(args); return n;
}
static inline int stramp_swprintf(wchar_t *buffer, size_t count, const wchar_t *format, ...) {
    va_list args; va_start(args, format);
    int n = stramp_vswprintf_n(buffer, count, format, args);
    va_end(args); return n;
}
#endif
#define swprintf stramp_swprintf
static inline int _wtoi(const wchar_t *s) { return (int)wcstol(s, NULL, 10); }
static inline long _wtol(const wchar_t *s) { return wcstol(s, NULL, 10); }
static inline double _wtof(const wchar_t *s) { return wcstod(s, NULL); }
#define _wcsicmp wcscasecmp
#define _stricmp strcasecmp
#define _strnicmp strncasecmp
#define stricmp strcasecmp
#define _snprintf snprintf
#define _snwprintf swprintf_posix_unused

static inline HMODULE LoadLibraryA(const char *) { return NULL; }
static inline BOOL FreeLibrary(HMODULE) { return TRUE; }
static inline void *GetProcAddress(HMODULE, const char *) { return NULL; }
#define LoadLibrary LoadLibraryA
typedef int errno_t;
#define CP_ACP 0
#define CP_UTF8 65001
#define MB_PRECOMPOSED 1
#define S_OK 0
#ifdef __cplusplus
extern "C" {
#endif
int MultiByteToWideChar(UINT codePage, DWORD flags, const char *mb, int mbLen, wchar_t *wc, int wcLen);
int WideCharToMultiByte(UINT codePage, DWORD flags, const wchar_t *wc, int wcLen, char *mb, int mbLen,
                        const char *defaultChar, BOOL *usedDefault);
long CompareFileTime(const FILETIME *a, const FILETIME *b);
long CoCreateGuid(struct _GUID *guid);
#ifdef __cplusplus
int StringFromGUID2(const struct _GUID &guid, wchar_t *out, int max);
#endif
#ifdef __cplusplus
}
#endif
#include <errno.h>
static inline DWORD GetLastError(void) { return (DWORD)errno; }
static inline void SetLastError(DWORD e) { errno = (int)e; }
#define FORMAT_MESSAGE_FROM_SYSTEM 0x1000
static inline DWORD FormatMessageW(DWORD, const void *, DWORD, DWORD, wchar_t *, DWORD, void *) { return 0; }
#define FormatMessage FormatMessageW
#define wcstok_s(s, d, c) wcstok((s), (d), (c))
#define wsprintf stramp_swprintf
#define wsprintfW stramp_swprintf
#define lstrlenW wcslen
#define CONST const
typedef struct _GUID { uint32_t Data1; uint16_t Data2; uint16_t Data3; uint8_t Data4[8]; } GUID;
typedef GUID CLSID, IID;
typedef void *LPSECURITY_ATTRIBUTES;
typedef union _LARGE_INTEGER { struct { DWORD LowPart; LONG HighPart; }; LONGLONG QuadPart; } LARGE_INTEGER;
static inline BOOL QueryPerformanceCounter(LARGE_INTEGER *c) { c->QuadPart = 0; return TRUE; }
static inline BOOL QueryPerformanceFrequency(LARGE_INTEGER *c) { c->QuadPart = 1; return TRUE; }
static inline errno_t _wfopen_s(FILE **out, const wchar_t *path, const wchar_t *mode) {
    char p[1024], m[16]; size_t i = 0;
    for (; path[i] && i < sizeof p - 1; ++i) p[i] = (char)path[i];
    p[i] = 0;
    for (i = 0; mode[i] && i < sizeof m - 1; ++i) m[i] = (char)mode[i];
    m[i] = 0;
    *out = fopen(p, m);
    return *out ? 0 : 1;
}

static inline DWORD GetTickCount(void) { return 0; }
static inline void Sleep(DWORD) {}
static inline void OutputDebugStringA(const char *) {}
#define OutputDebugString OutputDebugStringA

#endif
