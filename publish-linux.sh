#!/usr/bin/env bash
# Builds the Linux release artifact: a self-contained linux-x64 publish of str-amp, packed as
# publish/stramp-<version>-linux-x64.tar.gz. The name has to contain "linux" and end in .tar.gz,
# which is how the in-app updater picks the right asset off a GitHub release.
#
# Works on Linux (or WSL) and from Windows via Git Bash; -p:StrampLinux=true makes the build pick
# the Linux flavour of the app regardless of host OS.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="$(grep -oE 'Current = "[^"]+"' "$ROOT/src/Stramp.Core/Updates/AppVersion.cs" | head -1 | cut -d'"' -f2)"
[ -n "$VERSION" ] || { echo "Couldn't read AppVersion.Current" >&2; exit 1; }

OUT="$ROOT/publish/stramp-linux"
TARBALL="$ROOT/publish/stramp-$VERSION-linux-x64.tar.gz"
rm -rf "$OUT" "$TARBALL"

echo "Publishing str-amp $VERSION for linux-x64..."
dotnet publish "$ROOT/src/Stramp.App/Stramp.App.csproj" -c Release -r linux-x64 -o "$OUT" \
    -p:StrampLinux=true \
    -p:SelfContained=true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true

find "$OUT" -name '*.pdb' -delete
chmod +x "$OUT/Stramp.App"

echo "Packing $TARBALL..."
tar -C "$OUT" -czf "$TARBALL" .
echo "Done: $TARBALL"
echo "Attach this to the v$VERSION GitHub release. Needs ffmpeg and PulseAudio/PipeWire on the user's machine."
