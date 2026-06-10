#!/usr/bin/env bash
# Build Floss.app + .dmg for macOS from Linux (unsigned — Gatekeeper may require right-click Open).
# For signed + notarized builds, run on a Mac: packaging/macos/sign-and-notarize.sh (see notes/macos-signing.md).
#
# Usage:
#   ./packaging/macos/pack-dmg.sh osx-arm64
#   ./packaging/macos/pack-dmg.sh osx-x64
#   ./packaging/macos/pack-dmg.sh all
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
VERSION="${FLOSS_VERSION:-$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo)}"
OUT_ROOT="$ROOT/artifacts/macos"
PLIST_SRC="$ROOT/packaging/macos/Info.plist"

require_dmg_tool() {
  if command -v genisoimage >/dev/null; then
    echo genisoimage
    return
  fi
  if command -v mkisofs >/dev/null; then
    echo mkisofs
    return
  fi
  echo "Install genisoimage (e.g. apt install genisoimage / dnf install genisoimage)." >&2
  exit 1
}

pack_one() {
  local rid="$1"
  local publish="$ROOT/artifacts/publish/$rid"
  local staging="$ROOT/artifacts/macos/staging-$rid"
  local app="$staging/Floss.app"
  local dmg_name="FlossPaint-${rid}-beta.dmg"
  local dmg_path="$OUT_ROOT/$dmg_name"
  local iso_tool
  iso_tool="$(require_dmg_tool)"

  echo "==> macOS DMG pack: $rid (v$VERSION)"
  rm -rf "$publish" "$staging"
  mkdir -p "$OUT_ROOT" "$app/Contents/MacOS" "$app/Contents/Resources"

  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishTrimmed=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$publish"

  find "$publish" -name '*.pdb' -delete
  cp -a "$publish/." "$app/Contents/MacOS/"
  chmod +x "$app/Contents/MacOS/Floss"

  cat > "$staging/READ ME FIRST.txt" <<'EOF'
Floss (unsigned beta) — Apple Silicon (arm64)

1. Drag Floss.app to Applications (or Desktop).
2. Eject this disk image.
3. First launch ONLY: right-click Floss.app → Open → Open again.
   (Double-click often does nothing on unsigned apps — macOS Gatekeeper.)

If it still won't start, open Terminal and run:
  xattr -cr /Applications/Floss.app
  open /Applications/Floss.app

To see errors:
  /Applications/Floss.app/Contents/MacOS/Floss

Requires macOS 11 or later. This build is not notarized.
EOF

  sed "s/VERSION_PLACEHOLDER/$VERSION/g" "$PLIST_SRC" > "$app/Contents/Info.plist"
  printf 'APPL????' > "$app/Contents/PkgInfo"
  if [[ -f "$ROOT/packaging/icon.png" ]]; then
    cp "$ROOT/packaging/icon.png" "$app/Contents/Resources/icon.png"
  fi

  rm -f "$dmg_path"
  "$iso_tool" -V "Floss $VERSION" -D -R -apple -no-pad -o "$dmg_path" "$staging"

  rm -rf "$staging"
  echo "    $dmg_path"
  ls -lh "$dmg_path"
}

RID="${1:-}"
case "$RID" in
  osx-arm64|osx-x64) pack_one "$RID" ;;
  all)
    pack_one osx-arm64
    pack_one osx-x64
    ;;
  *)
    echo "Usage: $0 {osx-arm64|osx-x64|all}" >&2
    exit 1
    ;;
esac

echo ""
echo "Done. Unsigned DMG — users may need right-click → Open the first time."
