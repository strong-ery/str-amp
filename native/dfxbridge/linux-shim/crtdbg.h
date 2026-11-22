/* Debug-CRT shim: the asserts were debug-only and compile out. */
#pragma once
#include <assert.h>
#define _ASSERT(x) ((void)0)
#define _ASSERTE(x) ((void)0)
#define _CrtSetDbgFlag(x) 0
#define _CrtDumpMemoryLeaks() 0
