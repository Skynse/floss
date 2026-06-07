#!/usr/bin/env bash
# Upload release installers to a Gumroad product (Aseprite-style buyer library updates).
#
# Required env:
#   GUMROAD_ACCESS_TOKEN, GUMROAD_PRODUCT_ID
# Optional:
#   FLOSS_VERSION  — default: read from csproj
#   GUMROAD_CLI_VERSION — pin gumroad-cli release (default: 0.20.0)
#
# Usage:
#   ./packaging/release/upload-gumroad.sh
#   FLOSS_VERSION=0.1.0-beta.2 ./packaging/release/upload-gumroad.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
VERSION="${FLOSS_VERSION:-$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo 2>/dev/null || echo unknown)}"
CLI_VERSION="${GUMROAD_CLI_VERSION:-0.20.0}"

if [[ -z "${GUMROAD_ACCESS_TOKEN:-}" || -z "${GUMROAD_PRODUCT_ID:-}" ]]; then
  echo "Missing Gumroad config. Set GUMROAD_ACCESS_TOKEN and GUMROAD_PRODUCT_ID." >&2
  exit 1
fi

ensure_gumroad_cli() {
  if command -v gumroad >/dev/null; then
    return
  fi
  local tmp archive url
  tmp="$(mktemp -d)"
  archive="$tmp/gumroad-cli.tar.gz"
  url="https://github.com/antiwork/gumroad-cli/releases/download/v${CLI_VERSION}/gumroad-cli_linux_amd64.tar.gz"
  echo "==> Installing gumroad-cli v${CLI_VERSION}"
  curl -fsSL "$url" -o "$archive"
  tar xzf "$archive" -C "$tmp"
  export PATH="$tmp:$PATH"
  if ! command -v gumroad >/dev/null; then
    echo "gumroad-cli install failed (no gumroad binary in archive)." >&2
    exit 1
  fi
}

find_one() {
  local pattern="$1"
  shopt -s nullglob
  local matches=($pattern)
  shopt -u nullglob
  if ((${#matches[@]} == 0)); then
    return 1
  fi
  printf '%s\n' "${matches[0]}"
}

# collect_file <glob> <gumroad display name>
FILES=()
NAMES=()
collect_file() {
  local pattern="$1"
  local name="$2"
  local found
  if ! found="$(find_one "$pattern")"; then
    echo "  (skip) $name"
    return 0
  fi
  FILES+=("$found")
  NAMES+=("$name")
  echo "  $name  <-  $found"
}

echo "==> Upload to Gumroad product $GUMROAD_PRODUCT_ID (v$VERSION)"

collect_file "$ROOT/artifacts/velopack/linux-x64-beta/*.AppImage" \
  "FlossPaint-${VERSION}-linux-x64.AppImage"
collect_file "$ROOT/artifacts/flatpak/com.flosspaint.Floss.flatpak" \
  "FlossPaint-${VERSION}.flatpak"
collect_file "$ROOT/artifacts/velopack/win-x64-beta/*Setup.exe" \
  "FlossPaint-${VERSION}-win-x64-Setup.exe"

win_portable=""
if win_portable="$(find_one "$ROOT/artifacts/velopack/win-x64-beta/*Portable.zip")"; then
  :
elif win_portable="$(find_one "$ROOT/artifacts/portable/FlossPaint-win-x64-beta-portable.zip")"; then
  :
fi
if [[ -n "$win_portable" ]]; then
  FILES+=("$win_portable")
  NAMES+=("FlossPaint-${VERSION}-win-x64-portable.zip")
  echo "  FlossPaint-${VERSION}-win-x64-portable.zip  <-  $win_portable"
else
  echo "  (skip) FlossPaint-${VERSION}-win-x64-portable.zip"
fi

collect_file "$ROOT/artifacts/velopack/osx-arm64-beta/*.dmg" \
  "FlossPaint-${VERSION}-macOS-arm64.dmg"
collect_file "$ROOT/artifacts/velopack/osx-x64-beta/*.dmg" \
  "FlossPaint-${VERSION}-macOS-x64.dmg"
collect_file "$ROOT/artifacts/portable/FlossPaint-osx-arm64-beta-portable.zip" \
  "FlossPaint-${VERSION}-macOS-arm64-portable.zip"
collect_file "$ROOT/artifacts/portable/FlossPaint-osx-x64-beta-portable.zip" \
  "FlossPaint-${VERSION}-macOS-x64-portable.zip"

if ((${#FILES[@]} == 0)); then
  echo "No release artifacts found under $ROOT/artifacts/." >&2
  exit 1
fi

ensure_gumroad_cli

cmd=(gumroad products update "$GUMROAD_PRODUCT_ID" --replace-files --non-interactive)
for i in "${!FILES[@]}"; do
  cmd+=(--file "${FILES[$i]}" --file-name "${NAMES[$i]}")
done

echo "==> Replacing ${#FILES[@]} file(s) on Gumroad"
"${cmd[@]}"

echo ""
echo "Done. Buyers re-download from their Gumroad library."
if [[ -n "${GUMROAD_PRODUCT_URL:-}" ]]; then
  echo "Product: $GUMROAD_PRODUCT_URL"
fi
