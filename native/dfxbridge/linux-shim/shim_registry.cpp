// In-memory stand-in for the Windows registry; see windows.h.
#include "windows.h"
#undef swprintf
#define swprintf_posix swprintf
#include <map>
#include <string>
#include <vector>
#include <mutex>

namespace {
struct Value { DWORD type; std::vector<BYTE> data; };
struct Store { std::mutex lock; std::map<std::string, std::map<std::string, Value>> keys; };
Store &store() { static Store s; return s; }

std::string narrow(const wchar_t *w) {
    std::string r;
    for (; w && *w; ++w) r.push_back(*w < 128 ? (char)*w : '?');
    return r;
}
// A HKEY is a pointer to a heap string holding the key's full path.
HKEY makeHandle(const std::string &path) { return (HKEY) new std::string(path); }
std::string pathOf(HKEY k, const std::string &sub) {
    std::string base;
    uintptr_t v = (uintptr_t)k;
    if (v < 0x80000000u && k) base = *(std::string *)k; else base = "root" + std::to_string(v & 0xF);
    return sub.empty() ? base : base + "\\" + sub;
}
}

extern "C" {
LONG RegCreateKeyExA(HKEY k, const char *sub, DWORD, char *, DWORD, REGSAM, void *, HKEY *out, DWORD *disp) {
    std::lock_guard<std::mutex> g(store().lock);
    auto path = pathOf(k, sub ? sub : "");
    bool existed = store().keys.count(path) != 0;
    store().keys[path];
    if (out) *out = makeHandle(path);
    if (disp) *disp = existed ? 2 : 1;
    return ERROR_SUCCESS;
}
LONG RegCreateKeyExW(HKEY k, const wchar_t *sub, DWORD a, wchar_t *, DWORD b, REGSAM c, void *d, HKEY *out, DWORD *disp) {
    return RegCreateKeyExA(k, narrow(sub).c_str(), a, nullptr, b, c, d, out, disp);
}
LONG RegOpenKeyExA(HKEY k, const char *sub, DWORD, REGSAM, HKEY *out) {
    std::lock_guard<std::mutex> g(store().lock);
    auto path = pathOf(k, sub ? sub : "");
    if (!store().keys.count(path)) return ERROR_FILE_NOT_FOUND;
    if (out) *out = makeHandle(path);
    return ERROR_SUCCESS;
}
LONG RegOpenKeyExW(HKEY k, const wchar_t *sub, DWORD a, REGSAM b, HKEY *out) {
    return RegOpenKeyExA(k, narrow(sub).c_str(), a, b, out);
}
LONG RegQueryValueExA(HKEY k, const char *name, DWORD *, DWORD *type, BYTE *data, DWORD *size) {
    std::lock_guard<std::mutex> g(store().lock);
    auto it = store().keys.find(pathOf(k, ""));
    if (it == store().keys.end()) return ERROR_FILE_NOT_FOUND;
    auto v = it->second.find(name ? name : "");
    if (v == it->second.end()) return ERROR_FILE_NOT_FOUND;
    if (type) *type = v->second.type;
    if (size) {
        if (data && *size < v->second.data.size()) { *size = (DWORD)v->second.data.size(); return ERROR_MORE_DATA; }
        *size = (DWORD)v->second.data.size();
    }
    if (data) memcpy(data, v->second.data.data(), v->second.data.size());
    return ERROR_SUCCESS;
}
LONG RegQueryValueExW(HKEY k, const wchar_t *name, DWORD *r, DWORD *t, BYTE *d, DWORD *s) {
    return RegQueryValueExA(k, narrow(name).c_str(), r, t, d, s);
}
LONG RegSetValueExA(HKEY k, const char *name, DWORD, DWORD type, const BYTE *data, DWORD size) {
    std::lock_guard<std::mutex> g(store().lock);
    auto &vals = store().keys[pathOf(k, "")];
    Value v{type, std::vector<BYTE>(data, data + size)};
    vals[name ? name : ""] = std::move(v);
    return ERROR_SUCCESS;
}
LONG RegSetValueExW(HKEY k, const wchar_t *name, DWORD a, DWORD t, const BYTE *d, DWORD s) {
    return RegSetValueExA(k, narrow(name).c_str(), a, t, d, s);
}
LONG RegCloseKey(HKEY k) {
    if ((uintptr_t)k < 0x80000000u && k) delete (std::string *)k;
    return ERROR_SUCCESS;
}
LONG RegDeleteKeyA(HKEY k, const char *sub) {
    std::lock_guard<std::mutex> g(store().lock);
    store().keys.erase(pathOf(k, sub ? sub : ""));
    return ERROR_SUCCESS;
}
LONG RegDeleteKeyW(HKEY k, const wchar_t *sub) { return RegDeleteKeyA(k, narrow(sub).c_str()); }
}

// ---- swprintf with MSVC semantics -------------------------------------------------------------
#include <stdarg.h>
#undef swprintf
#define swprintf_posix swprintf
extern "C" {
int stramp_vswprintf_n(wchar_t *buffer, size_t count, const wchar_t *format, va_list args) {
    // MSVC: %s / %c are wide, %S / %C narrow. glibc: the opposite. Swap them in a copy of the format.
    std::wstring fmt;
    for (const wchar_t *p = format; *p; ++p) {
        fmt.push_back(*p);
        if (*p != L'%') continue;
        ++p;
        if (*p == L'%') { fmt.push_back(L'%'); continue; }
        while (*p && wcschr(L"-+ #0123456789.*", *p)) fmt.push_back(*p++);
        // length modifiers are left alone: an explicit l/h already says what it means
        if (*p == L's') { fmt.push_back(L'l'); fmt.push_back(L's'); }
        else if (*p == L'c') { fmt.push_back(L'l'); fmt.push_back(L'c'); }
        else if (*p == L'S') { fmt.push_back(L's'); }
        else if (*p == L'C') { fmt.push_back(L'c'); }
        else if (*p) fmt.push_back(*p);
        else break;
    }
    return vswprintf(buffer, count, fmt.c_str(), args);
}
}


// ---- string conversion, GUIDs, file times -----------------------------------------------------
#include <random>
extern "C" {
int MultiByteToWideChar(UINT, DWORD, const char *mb, int mbLen, wchar_t *wc, int wcLen) {
    // Linux's "ANSI" code page is UTF-8, and wchar_t is UTF-32 here.
    if (mbLen < 0) mbLen = (int)strlen(mb) + 1;
    int out = 0;
    for (int i = 0; i < mbLen;) {
        unsigned char c = (unsigned char)mb[i];
        unsigned cp; int extra;
        if (c < 0x80) { cp = c; extra = 0; }
        else if ((c >> 5) == 6) { cp = c & 0x1F; extra = 1; }
        else if ((c >> 4) == 14) { cp = c & 0x0F; extra = 2; }
        else if ((c >> 3) == 30) { cp = c & 0x07; extra = 3; }
        else { cp = 0xFFFD; extra = 0; }
        for (int k = 1; k <= extra && i + k < mbLen; ++k) cp = (cp << 6) | ((unsigned char)mb[i + k] & 0x3F);
        i += 1 + extra;
        if (wc) { if (out >= wcLen) return 0; wc[out] = (wchar_t)cp; }
        ++out;
    }
    return out;
}
int WideCharToMultiByte(UINT, DWORD, const wchar_t *wc, int wcLen, char *mb, int mbLen, const char *, BOOL *used) {
    if (used) *used = FALSE;
    if (wcLen < 0) wcLen = (int)wcslen(wc) + 1;
    int out = 0;
    auto put = [&](unsigned char b) { if (mb) { if (out >= mbLen) return false; mb[out] = (char)b; } ++out; return true; };
    for (int i = 0; i < wcLen; ++i) {
        unsigned cp = (unsigned)wc[i];
        bool ok = true;
        if (cp < 0x80) ok = put((unsigned char)cp);
        else if (cp < 0x800) ok = put(0xC0 | (cp >> 6)) && put(0x80 | (cp & 0x3F));
        else if (cp < 0x10000) ok = put(0xE0 | (cp >> 12)) && put(0x80 | ((cp >> 6) & 0x3F)) && put(0x80 | (cp & 0x3F));
        else ok = put(0xF0 | (cp >> 18)) && put(0x80 | ((cp >> 12) & 0x3F)) && put(0x80 | ((cp >> 6) & 0x3F)) && put(0x80 | (cp & 0x3F));
        if (!ok) return 0;
    }
    return out;
}
long CompareFileTime(const FILETIME *a, const FILETIME *b) {
    uint64_t x = ((uint64_t)a->dwHighDateTime << 32) | a->dwLowDateTime;
    uint64_t y = ((uint64_t)b->dwHighDateTime << 32) | b->dwLowDateTime;
    return x < y ? -1 : x > y ? 1 : 0;
}
long CoCreateGuid(struct _GUID *g) {
    std::random_device rd;
    uint32_t r[4] = {rd(), rd(), rd(), rd()};
    memcpy(g, r, sizeof *g);
    return 0;
}
int StringFromGUID2(const struct _GUID &g, wchar_t *out, int max) {
    wchar_t tmp[64];
    int n = swprintf_posix(tmp, 64, L"{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}", g.Data1, g.Data2, g.Data3,
        g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7]);
    if (n + 1 > max) return 0;
    wcscpy(out, tmp);
    return n + 1;
}
}
