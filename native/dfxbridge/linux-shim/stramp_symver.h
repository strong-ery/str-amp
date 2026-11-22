/*
 * Pins the handful of libc/libm functions this library uses to their original symbol versions, so
 * the .so loads on any glibc from 2.31 up rather than only on the (very recent) one it was built
 * on. Newer glibc ships the same functions under newer versions; the old ones are kept as
 * compatibility symbols and behave identically for what DfxDsp does with them.
 */
#pragma once
#define STRAMP_SYMVER(sym, ver) __asm__(".symver " #sym "," #sym "@" #ver)
STRAMP_SYMVER(sqrtf, GLIBC_2.2.5);
STRAMP_SYMVER(asinf, GLIBC_2.2.5);
STRAMP_SYMVER(atan2f, GLIBC_2.2.5);
STRAMP_SYMVER(log10f, GLIBC_2.2.5);
STRAMP_SYMVER(fmodf, GLIBC_2.2.5);
STRAMP_SYMVER(pow, GLIBC_2.2.5);
STRAMP_SYMVER(exp, GLIBC_2.2.5);

/* glibc 2.38+ redirects the strtol/scanf families to C23 variants (__isoc23_*) that only differ in
   accepting 0b binary literals. Bind them back to the originals. */
__asm__(".symver __isoc23_wcstol,wcstol@GLIBC_2.2.5");
__asm__(".symver __isoc23_swscanf,swscanf@GLIBC_2.2.5");
__asm__(".symver __isoc23_strtol,strtol@GLIBC_2.2.5");
__asm__(".symver __isoc23_sscanf,sscanf@GLIBC_2.2.5");
