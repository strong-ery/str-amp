#!/usr/bin/env bash
# Builds libDfxBridge.so: FxSound's DfxDsp sources plus str-amp's flat C entry points, compiled
# against a small Windows-API shim (linux-shim/). See COPYRIGHT in the repository root.
#
#   FXSOUND_ROOT=/path/to/fxsound-app ./build-linux.sh
#
# The checkout must be at the commit named in COPYRIGHT. The registry-namespace patch is applied
# to a copy of the sources, never to the checkout itself.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FXSOUND_ROOT="${FXSOUND_ROOT:?set FXSOUND_ROOT to a checkout of fxsound-app}"
OUT="${OUT:-$HERE/build/linux-x64}"
WORK="$OUT/work"
CXX="${CXX:-g++}"
CC="${CC:-gcc}"

rm -rf "$WORK"
mkdir -p "$WORK/shim" "$OUT/obj"

# Work on a copy so the patch (and any case fix-ups) never touch the user's checkout.
cp -r "$FXSOUND_ROOT/dsp" "$WORK/dsp"
cp -r "$FXSOUND_ROOT/audiopassthru" "$WORK/audiopassthru"
( cd "$WORK" && patch -p1 --no-backup-if-mismatch < "$HERE/fxsound-registry-namespace.patch" >/dev/null )

# MSVC resolves #include names case-insensitively and Linux does not; the shim answers to both
# spellings of the one header, and the build adds whatever else turns out to be mis-cased.
cp "$HERE/linux-shim/"* "$WORK/shim/"
ln -sf windows.h "$WORK/shim/Windows.h"
ln -sf windows.h "$WORK/shim/tchar.h" 2>/dev/null || true

# A few FxSound sources are saved as UTF-16; gcc wants UTF-8.
while IFS= read -r -d '' f; do
    if head -c2 "$f" | od -An -tx1 | grep -qiE 'ff fe|fe ff'; then
        iconv -f UTF-16 -t UTF-8 "$f" > "$f.u8" && mv "$f.u8" "$f"
    fi
done < <(find "$WORK/dsp" "$WORK/audiopassthru" \( -name '*.c' -o -name '*.cpp' -o -name '*.h' \) -print0)

# MSVC accepts backslashes in #include paths; gcc does not.
find "$WORK/dsp" "$WORK/audiopassthru" \( -name '*.c' -o -name '*.cpp' -o -name '*.h' \) -print0 \
    | xargs -0 sed -i -E '/^[[:space:]]*#[[:space:]]*include/ s#\\#/#g'

# FxSound targets Windows, where C `long` is 32 bits; here it is 64, which silently corrupts the
# packed parameter blocks the DSP keeps (found with UBSan: 8-byte stores at 4-byte offsets). Rewrite
# it to `int` in the working copy so the layout matches what the code was written for.
find "$WORK/dsp" "$WORK/audiopassthru" \( -name '*.c' -o -name '*.cpp' -o -name '*.h' \) -print0 \
    | xargs -0 sed -i -E \
        -e 's/\blong long\b/__STRAMP_LL__/g' -e 's/\blong double\b/__STRAMP_LD__/g' \
        -e 's/\blong unsigned( int)?\b/unsigned int/g' -e 's/\blong int\b/int/g' \
        -e 's/\blong\b/int/g' \
        -e 's/__STRAMP_LL__/long long/g' -e 's/__STRAMP_LD__/long double/g'

# DfxDspPrivate's constructor never initialises preset_list_handle_ (the destructor frees it when
# non-NULL); Windows' heap tends to hand back zeroed memory, glibc's doesn't, so a stray pointer
# gets freed. Initialise it.
sed -i -E '0,/^[[:space:]]*rval_to_midi_qnt_handle_ = NULL;/s//    rval_to_midi_qnt_handle_ = NULL; preset_list_handle_ = NULL;/' "$WORK/dsp/DfxDspPrivate.cpp"
grep -q 'preset_list_handle_ = NULL' "$WORK/dsp/DfxDspPrivate.cpp" || { echo "preset_list_handle_ patch did not apply" >&2; exit 1; }

# Same trick for every other header the sources spell differently from the file on disk.
grep -rhoE '#include[[:space:]]*"[^"]+"' "$WORK/dsp" "$WORK/audiopassthru" 2>/dev/null \
    | sed -E 's/#include[[:space:]]*"([^"]+)"/\1/' | tr '\' '/' | sed 's|.*/||' | sort -u > "$WORK/includes.txt" || true
while read -r name; do
    [ -z "$name" ] && continue
    if [ -e "$WORK/shim/$name" ]; then continue; fi
    if find "$WORK/dsp" "$WORK/audiopassthru" -name "$name" 2>/dev/null | grep -q .; then continue; fi
    real="$(find "$WORK/dsp" "$WORK/audiopassthru" -iname "$name" 2>/dev/null | head -1 || true)"
    if [ -n "$real" ]; then ln -sf "$real" "$WORK/shim/$name"; fi
done < "$WORK/includes.txt"

DEFS="-DWIN32 -DPT_NON_MFC -DDSPSOFT_TARGET -DPT_DSP_BUILD=PT_DSP_DFX -DNDEBUG -DSTRAMP_LINUX"
INCS="-I$WORK/shim -I$WORK/dsp/ptutil/include -I$WORK/dsp/include -I$WORK/audiopassthru/include"
SAN=""
if [ -n "${SANITIZE:-}" ]; then SAN="-fsanitize=address,undefined -g -fno-omit-frame-pointer"; fi
COMMON="$SAN -O2 -fPIC -fpermissive -w -fno-strict-aliasing -include $WORK/shim/stramp_compat.h"

mapfile -t SOURCES < <(cd "$WORK/dsp" && grep -o 'ClCompile Include="[^"]*"' DfxDsp.vcxproj \
    | sed 's/ClCompile Include=//' | tr -d '"' | tr '\' '/')

# The bridge also links FxSound's passthru helper sources (math, files, strings, registry, logging...),
# the same set DfxBridge.vcxproj compiles on Windows.
# File access, wall-clock time and OS queries are replaced by POSIX equivalents in the shim
# (shim_passthru.cpp) rather than porting FxSound's Win32 implementations of them.
for dir in MTH MRY pstr SLOUT; do
    for f in "$WORK"/audiopassthru/src/$dir/*.cpp; do
        [ -e "$f" ] && SOURCES+=("../audiopassthru/src/$dir/$(basename "$f")")
    done
done

fail=0
for src in "${SOURCES[@]}"; do
    # Windows wave-device I/O (waveIn/waveOut); the DSP never plays or records on its own here.
    case "$src" in ptutil/PWAV/*) continue ;; esac
    obj="$OUT/obj/$(echo "${src#../}" | tr '/' '_').o"
    case "$src" in
        *.c)   cmd=("$CC"  -std=gnu11   $COMMON $DEFS $INCS -c "$WORK/dsp/$src" -o "$obj") ;;
        *)     cmd=("$CXX" -std=gnu++17 $COMMON $DEFS $INCS -c "$WORK/dsp/$src" -o "$obj") ;;
    esac
    "${cmd[@]}" || { echo "FAILED: $src" >&2; fail=1; }
done
[ "$fail" = 0 ] || exit 1

for shim in shim_registry shim_passthru; do
    "$CXX" -std=gnu++17 $COMMON $DEFS $INCS -c "$HERE/linux-shim/$shim.cpp" -o "$OUT/obj/$shim.o"
done
"$CXX" -std=gnu++17 $COMMON $DEFS $INCS -c "$HERE/dfxbridge.cpp" -o "$OUT/obj/dfxbridge.o"

"$CXX" -shared $SAN -o "$OUT/libDfxBridge.so" "$OUT"/obj/*.o -Wl,--exclude-libs,ALL -lm
echo "Built $OUT/libDfxBridge.so"
