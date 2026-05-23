#!/usr/bin/env bash
# Build a Velopack beta release for Floss.
#
# Usage:
#   ./packaging/velopack/pack.sh linux-x64
#   ./packaging/velopack/pack.sh win-x64     # pack on Windows (or CI win job)
#
# Requires: .NET SDK, dotnet tool restore (vpk).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="${1:-linux-x64}"
CHANNEL="${RID}-beta"
PACK_ID="FlossPaint"
PACK_TITLE="Floss"
ICON="$ROOT/packaging/icon.png"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"

case "$RID" in
  linux-x64) MAIN_EXE="Floss" ;;
  win-x64|win-arm64) MAIN_EXE="Floss.exe" ;;
  osx-x64|osx-arm64) MAIN_EXE="Floss" ;;
  *)
    echo "Unsupported RID: $RID" >&2
    echo "Use: linux-x64, win-x64, win-arm64, osx-x64, osx-arm64" >&2
    exit 1
    ;;
esac

VERSION="${FLOSS_VERSION:-$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo)}"
PUBLISH="$ROOT/artifacts/publish/$RID"
OUTPUT="$ROOT/artifacts/velopack/$CHANNEL"

echo "==> Floss Velopack pack"
echo "    version : $VERSION"
echo "    rid     : $RID"
echo "    channel : $CHANNEL"
echo "    output  : $OUTPUT"

rm -rf "$PUBLISH"
mkdir -p "$(dirname "$OUTPUT")"

echo "==> dotnet publish"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishTrimmed=false \
  -o "$PUBLISH"

echo "==> vpk pack"
cd "$ROOT"
dotnet tool restore
# vpk 0.0.1298 targets net9; roll forward when only net10 runtime is installed.
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-LatestMajor}"
dotnet tool run vpk pack \
  --packId "$PACK_ID" \
  --packVersion "$VERSION" \
  --packTitle "$PACK_TITLE" \
  --packDir "$PUBLISH" \
  --mainExe "$MAIN_EXE" \
  --icon "$ICON" \
  --channel "$CHANNEL" \
  --outputDir "$OUTPUT"

echo ""
echo "Done. Ship these to flosspaint.com (ignore .nupkg / releases.*.json):"
case "$RID" in
  linux-x64) echo "  $OUTPUT/FlossPaint-${CHANNEL}.AppImage" ;;
  win-*)     echo "  $OUTPUT/*Setup.exe and *Portable.zip" ;;
esac
echo ""
ls -la "$OUTPUT"
