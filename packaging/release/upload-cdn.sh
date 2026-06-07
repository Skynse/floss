#!/usr/bin/env bash
# Upload beta installers to Cloudflare R2 (S3-compatible) + version manifest.
#
# Required env:
#   R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_BUCKET
# Optional:
#   R2_ACCOUNT_ID     — defaults endpoint to https://<id>.r2.cloudflarestorage.com
#   R2_ENDPOINT       — override full S3 endpoint URL
#   R2_PREFIX         — key prefix (default: downloads)
#   FLOSS_VERSION     — manifest version (default: read from csproj)
#   CDN_PUBLIC_URL    — e.g. https://cdn.flosspaint.com (stored in version.json)
#
# Usage:
#   ./packaging/release/upload-cdn.sh
#   FLOSS_VERSION=0.1.0-beta.2 ./packaging/release/upload-cdn.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
PREFIX="${R2_PREFIX:-downloads}"
VERSION="${FLOSS_VERSION:-$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo 2>/dev/null || echo unknown)}"

if [[ -z "${R2_ACCESS_KEY_ID:-}" || -z "${R2_SECRET_ACCESS_KEY:-}" || -z "${R2_BUCKET:-}" ]]; then
  echo "Missing R2 credentials. Set R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_BUCKET." >&2
  exit 1
fi

if [[ -n "${R2_ENDPOINT:-}" ]]; then
  ENDPOINT="$R2_ENDPOINT"
elif [[ -n "${R2_ACCOUNT_ID:-}" ]]; then
  ENDPOINT="https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com"
else
  echo "Set R2_ACCOUNT_ID or R2_ENDPOINT." >&2
  exit 1
fi

export AWS_ACCESS_KEY_ID="$R2_ACCESS_KEY_ID"
export AWS_SECRET_ACCESS_KEY="$R2_SECRET_ACCESS_KEY"
export AWS_DEFAULT_REGION="${R2_REGION:-auto}"

aws --endpoint-url "$ENDPOINT" s3api head-bucket --bucket "$R2_BUCKET" >/dev/null

upload_one() {
  local src_pattern="$1"
  local dest_name="$2"
  local found=""
  shopt -s nullglob
  for f in $src_pattern; do
    found="$f"
    break
  done
  shopt -u nullglob
  if [[ -z "$found" || ! -f "$found" ]]; then
    echo "  (skip) $dest_name"
    return 0
  fi
  local key="$PREFIX/$dest_name"
  aws --endpoint-url "$ENDPOINT" s3 cp "$found" "s3://$R2_BUCKET/$key" \
    --content-type "$(mime_for "$dest_name")" \
    --cache-control "public, max-age=3600"
  echo "  $dest_name  <-  $found"
}

mime_for() {
  case "$1" in
    *.AppImage) echo "application/vnd.appimage" ;;
    *.flatpak) echo "application/vnd.flatpak" ;;
    *.zip) echo "application/zip" ;;
    *.dmg) echo "application/x-apple-diskimage" ;;
    *.exe) echo "application/vnd.microsoft.portable-executable" ;;
    *.json) echo "application/json" ;;
    *) echo "application/octet-stream" ;;
  esac
}

echo "==> Upload to s3://$R2_BUCKET/$PREFIX/ (v$VERSION)"

upload_one "$ROOT/artifacts/velopack/linux-x64-beta/*.AppImage" \
  "FlossPaint-linux-x64-beta.AppImage"
upload_one "$ROOT/artifacts/flatpak/com.flosspaint.Floss.flatpak" \
  "com.flosspaint.Floss.flatpak"
upload_one "$ROOT/artifacts/portable/FlossPaint-win-x64-beta-portable.zip" \
  "FlossPaint-win-x64-beta-portable.zip"
upload_one "$ROOT/artifacts/portable/FlossPaint-osx-arm64-beta-portable.zip" \
  "FlossPaint-osx-arm64-beta-portable.zip"
upload_one "$ROOT/artifacts/portable/FlossPaint-osx-x64-beta-portable.zip" \
  "FlossPaint-osx-x64-beta-portable.zip"
upload_one "$ROOT/artifacts/velopack/win-x64-beta/*Portable.zip" \
  "FlossPaint-win-x64-beta-portable.zip"
upload_one "$ROOT/artifacts/velopack/win-x64-beta/*Setup.exe" \
  "FlossPaint-win-x64-beta-Setup.exe"
upload_one "$ROOT/artifacts/velopack/osx-arm64-beta/*.dmg" \
  "FlossPaint-osx-arm64-beta.dmg"
upload_one "$ROOT/artifacts/velopack/osx-x64-beta/*.dmg" \
  "FlossPaint-osx-x64-beta.dmg"

MANIFEST="$(mktemp)"
CDN_URL="${CDN_PUBLIC_URL:-https://cdn.flosspaint.com}"
cat > "$MANIFEST" <<EOF
{
  "version": "$VERSION",
  "releasedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "cdnBase": "$CDN_URL",
  "downloads": {
    "linuxAppImage": "$CDN_URL/$PREFIX/FlossPaint-linux-x64-beta.AppImage",
    "linuxFlatpak": "$CDN_URL/$PREFIX/com.flosspaint.Floss.flatpak",
    "windowsPortable": "$CDN_URL/$PREFIX/FlossPaint-win-x64-beta-portable.zip",
    "windowsSetup": "$CDN_URL/$PREFIX/FlossPaint-win-x64-beta-Setup.exe",
    "macOsArm64Portable": "$CDN_URL/$PREFIX/FlossPaint-osx-arm64-beta-portable.zip",
    "macOsX64Portable": "$CDN_URL/$PREFIX/FlossPaint-osx-x64-beta-portable.zip",
    "macOsArm64Dmg": "$CDN_URL/$PREFIX/FlossPaint-osx-arm64-beta.dmg",
    "macOsX64Dmg": "$CDN_URL/$PREFIX/FlossPaint-osx-x64-beta.dmg"
  }
}
EOF

aws --endpoint-url "$ENDPOINT" s3 cp "$MANIFEST" "s3://$R2_BUCKET/$PREFIX/version.json" \
  --content-type "application/json" \
  --cache-control "public, max-age=300"
rm -f "$MANIFEST"
echo "  version.json"

echo ""
echo "Done. Public manifest: $CDN_URL/$PREFIX/version.json"
