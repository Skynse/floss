#!/usr/bin/env bash
# Copy built installers into floss-site/public/downloads for static hosting.
#
# Usage:
#   ./packaging/release/sync-to-site.sh [path-to-floss-site]
#
# Build first:
#   ./packaging/velopack/pack.sh linux-x64
#   ./packaging/portable/pack.sh all
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SITE="${1:-$ROOT/../floss-site}"
DOWNLOADS="$SITE/public/downloads"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo 2>/dev/null || echo unknown)"

mkdir -p "$DOWNLOADS"

copy_one() {
  local pattern="$1"
  local dest_name="$2"
  local found=""
  shopt -s nullglob
  for f in $pattern; do
    found="$f"
    break
  done
  shopt -u nullglob
  if [[ -n "$found" && -f "$found" ]]; then
    cp -f "$found" "$DOWNLOADS/$dest_name"
    echo "  $dest_name  <-  $found"
  else
    echo "  (skip) $dest_name — not found ($pattern)"
  fi
}

echo "==> Sync installers to $DOWNLOADS (version $VERSION)"

copy_one "$ROOT/artifacts/velopack/linux-x64-beta/*.AppImage" \
  "FlossPaint-linux-x64-beta.AppImage"

copy_one "$ROOT/artifacts/portable/FlossPaint-linux-x64-beta-portable.zip" \
  "FlossPaint-linux-x64-beta-portable.zip"

copy_one "$ROOT/artifacts/portable/FlossPaint-win-x64-beta-portable.zip" \
  "FlossPaint-win-x64-beta-portable.zip"

copy_one "$ROOT/artifacts/portable/FlossPaint-osx-arm64-beta-portable.zip" \
  "FlossPaint-osx-arm64-beta-portable.zip"

copy_one "$ROOT/artifacts/portable/FlossPaint-osx-x64-beta-portable.zip" \
  "FlossPaint-osx-x64-beta-portable.zip"

cat > "$DOWNLOADS/README.txt" <<EOF
Floss beta installers (v$VERSION)

Local preview only — production releases go to Gumroad (see notes/release-automation.md).
Buyers download and update from their Gumroad library.

Linux AppImage:  FlossPaint-linux-x64-beta.AppImage
Linux portable:  FlossPaint-linux-x64-beta-portable.zip — unzip, run ./Floss
Windows:         FlossPaint-win-x64-beta-portable.zip — unzip, run Floss.exe
macOS:           FlossPaint-osx-arm64-beta-portable.zip (Apple Silicon) or osx-x64 (Intel)
EOF

echo ""
echo "Done. For releases, upload to Gumroad:"
echo "  ./packaging/release/upload-gumroad.sh"
ls -la "$DOWNLOADS"
