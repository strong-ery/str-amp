// POSIX stand-ins for the handful of FxSound "passthru" helpers DfxDsp links against that are
// Win32 all the way down: the registry-backed settings store, file access, and the hardware
// (sound-card EEPROM) hooks that only existed for FxSound's old DSP boards.
//
// Settings are kept in memory for the life of the process. str-amp sets every effect value each
// time a track opens, so nothing needs to survive a restart.
#include "windows.h"
#undef swprintf
#include <unistd.h>
#include <map>
#include <mutex>
#include <string>
#include <fcntl.h>
#include <sys/stat.h>

class CSlout;

namespace {
std::mutex g_lock;
std::map<std::wstring, std::wstring> g_values;

std::wstring keyOf(int keyClass, const wchar_t *path) {
    std::wstring k = std::to_wstring(keyClass) + L"\\";
    for (const wchar_t *p = path; p && *p; ++p) k.push_back((wchar_t)towlower(*p));  // registry keys ignore case
    return k;
}

std::string narrow(const wchar_t *w) {
    char buf[4096];
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, sizeof buf, nullptr, nullptr);
    return n > 0 ? std::string(buf) : std::string();
}
}

int regCreateKey_Wide(int keyClass, wchar_t *path, wchar_t *value) {
    std::lock_guard<std::mutex> g(g_lock);
    g_values[keyOf(keyClass, path)] = value ? value : L"";
    return 0;
}

int regReadKey_Wide(int keyClass, wchar_t *path, int *exists, wchar_t *out, unsigned int length) {
    std::lock_guard<std::mutex> g(g_lock);
    auto it = g_values.find(keyOf(keyClass, path));
    if (exists) *exists = it != g_values.end() ? 1 : 0;
    if (out && length) {
        if (it == g_values.end()) { out[0] = 0; }
        else {
            wcsncpy(out, it->second.c_str(), length - 1);
            out[length - 1] = 0;
        }
    }
    return 0;
}

FILE *fileOpen_Wide(wchar_t *path, wchar_t *mode, CSlout *) {
    return fopen(narrow(path).c_str(), narrow(mode).c_str());
}

int fileExist_Wide(wchar_t *path, int *exists) {
    if (exists) *exists = access(narrow(path).c_str(), F_OK) == 0 ? 1 : 0;
    return 0;
}

int fileSplitFullpath_Wide(wchar_t *full, wchar_t *dir, wchar_t *name, CSlout *) {
    const wchar_t *slash = wcsrchr(full, L'/');
    const wchar_t *bslash = wcsrchr(full, (wchar_t)92);  // backslash
    if (!slash || (bslash && bslash > slash)) slash = bslash;
    if (slash) {
        if (dir) { size_t n = (size_t)(slash - full); wcsncpy(dir, full, n); dir[n] = 0; }
        if (name) wcscpy(name, slash + 1);
    } else {
        if (dir) dir[0] = 0;
        if (name) wcscpy(name, full);
    }
    return 0;
}

int fileGetModifiedDate_Wide(wchar_t *path, _FILETIME *out, CSlout *) {
    // statx rather than stat: plain stat is a newer glibc symbol, and statx exists from 2.28.
    struct statx st;
    if (statx(AT_FDCWD, narrow(path).c_str(), 0, STATX_MTIME, &st) != 0) return 1;
    // FILETIME counts 100 ns ticks since 1601; statx gives seconds since 1970.
    unsigned long long ticks = ((unsigned long long)st.stx_mtime.tv_sec + 11644473600ULL) * 10000000ULL;
    out->dwLowDateTime = (DWORD)(ticks & 0xFFFFFFFFu);
    out->dwHighDateTime = (DWORD)(ticks >> 32);
    return 0;
}

// DSP-board hardware: absent here, so every access reports failure.
extern "C" {
int comHrdwrGetChar(int, int *) { return 1; }
int comHrdEepromReadUlong(short unsigned, short unsigned, unsigned int *) { return 1; }
int comHrdEepromWriteUlong(short unsigned, short unsigned, unsigned int) { return 1; }
}

// 24-bit PCM <-> float, little-endian, interleaved. Only the sample count matters here.
int pwav24BitToFloat(char *in, float *out, int length, int) {
    for (int i = 0; i < length; ++i) {
        int v = ((unsigned char)in[3 * i]) | ((unsigned char)in[3 * i + 1] << 8) | ((signed char)in[3 * i + 2] << 16);
        out[i] = (float)v / 8388608.0f;
    }
    return 0;
}
int pwavFloatTo24Bit(float *in, char *out, int length, int) {
    for (int i = 0; i < length; ++i) {
        float f = in[i] > 1.0f ? 1.0f : in[i] < -1.0f ? -1.0f : in[i];
        int v = (int)(f * 8388607.0f);
        out[3 * i] = (char)(v & 0xFF);
        out[3 * i + 1] = (char)((v >> 8) & 0xFF);
        out[3 * i + 2] = (char)((v >> 16) & 0xFF);
    }
    return 0;
}
