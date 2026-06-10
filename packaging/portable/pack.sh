#!/usr/bin/env bash
# Self-contained publish + zip (no Velopack). Works cross-platform from Linux.
#
# Usage:
#   ./packaging/portable/pack.sh linux-x64
#   ./packaging/portable/pack.sh win-x64
#   ./packaging/portable/pack.sh osx-arm64
#   ./packaging/portable/pack.sh osx-x64
#   ./packaging/portable/pack.sh all
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
VERSION="${FLOSS_VERSION:-$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo)}"
OUT_ROOT="$ROOT/artifacts/portable"

pack_one() {
  local rid="$1"
  local publish="$ROOT/artifacts/publish/$rid"
  local zip_name="FlossPaint-${rid}-beta-portable.zip"
  local zip_path="$OUT_ROOT/$zip_name"

  echo "==> Portable pack: $rid (v$VERSION)"
  rm -rf "$publish"
  mkdir -p "$OUT_ROOT"

  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$publish"

  find "$publish" -name '*.pdb' -delete

  rm -f "$zip_path"
  (
    cd "$publish"
    if command -v zip >/dev/null 2>&1; then
      zip -9 -r -q "$zip_path" . -x '*.pdb'
    else
      echo "zip not found; install zip (e.g. dnf install zip)." >&2
      exit 1
    fi
  )

  echo "    $zip_path"
  ls -lh "$zip_path"
}

RID="${1:-}"
case "$RID" in
  linux-x64|win-x64|osx-arm64|osx-x64) pack_one "$RID" ;;
  all)
    pack_one linux-x64
    pack_one win-x64
    ./packaging/macos/pack-dmg.sh all
    ;;
  *)
    echo "Usage: $0 {linux-x64|win-x64|osx-arm64|osx-x64|all}" >&2
    exit 1
    ;;
esac

echo ""
echo "Done. Sync to site: ./packaging/release/sync-to-site.sh"
