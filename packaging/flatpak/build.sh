#!/usr/bin/env bash
# Build a Flatpak bundle for Floss (linux-x64 self-contained publish).
#
# Usage:
#   ./packaging/flatpak/build.sh
#
# Requires: flatpak, flatpak-builder, org.freedesktop.Platform//24.08,
#           org.freedesktop.Sdk//24.08
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
FLATPAK_DIR="$ROOT/packaging/flatpak"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
RID="linux-x64"
PUBLISH="$ROOT/artifacts/publish/$RID"
BUILD_CTX="$ROOT/artifacts/flatpak/build-context"
REPO="$ROOT/artifacts/flatpak/repo"
BUNDLE="$ROOT/artifacts/flatpak/com.flosspaint.Floss.flatpak"
APP_ID="com.flosspaint.Floss"
FLATPAK_SCOPE="${FLATPAK_SCOPE:-user}"

if ! command -v flatpak-builder >/dev/null 2>&1; then
  echo "flatpak-builder not found. Install flatpak-builder (e.g. dnf install flatpak-builder)." >&2
  exit 1
fi

echo "==> Ensuring Flatpak runtime (org.freedesktop.Platform 24.08, scope=$FLATPAK_SCOPE)"
flatpak remote-add --if-not-exists --"$FLATPAK_SCOPE" flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install -y --"$FLATPAK_SCOPE" flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08

echo "==> dotnet publish ($RID)"
rm -rf "$PUBLISH"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishTrimmed=false \
  -o "$PUBLISH"

echo "==> Staging flatpak build context"
rm -rf "$BUILD_CTX"
mkdir -p "$BUILD_CTX/publish"
cp -a "$PUBLISH/." "$BUILD_CTX/publish/"
cp "$FLATPAK_DIR/floss-launcher.sh" "$BUILD_CTX/"
cp "$FLATPAK_DIR/com.flosspaint.Floss.desktop" "$BUILD_CTX/"
cp "$FLATPAK_DIR/com.flosspaint.Floss.metainfo.xml" "$BUILD_CTX/"
cp "$FLATPAK_DIR/com.flosspaint.Floss.yml" "$BUILD_CTX/"
if command -v magick >/dev/null 2>&1; then
  magick "$ROOT/packaging/icon.png" -resize 256x256 "$BUILD_CTX/icon.png"
elif command -v convert >/dev/null 2>&1; then
  convert "$ROOT/packaging/icon.png" -resize 256x256 "$BUILD_CTX/icon.png"
else
  echo "ImageMagick (magick/convert) required to resize icon for Flatpak (max 512x512)." >&2
  exit 1
fi
cp "$ROOT/packaging/linux/application-x-floss.xml" "$BUILD_CTX/application-x-floss.xml"

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo)"
sed -i "s/<release version=\"[^\"]*\"/<release version=\"$VERSION\"/" "$BUILD_CTX/com.flosspaint.Floss.metainfo.xml"

echo "==> flatpak-builder"
rm -rf "$ROOT/artifacts/flatpak/appdir" "$REPO"
mkdir -p "$(dirname "$REPO")"
flatpak-builder --force-clean --repo="$REPO" \
  "$ROOT/artifacts/flatpak/appdir" \
  "$BUILD_CTX/com.flosspaint.Floss.yml"

echo "==> flatpak build-bundle"
rm -f "$BUNDLE"
flatpak build-bundle "$REPO" "$BUNDLE" "$APP_ID"

echo ""
echo "Done. Ship this to flosspaint.com:"
echo "  $BUNDLE"
echo ""
echo "Install locally:"
echo "  flatpak install --user $BUNDLE"
echo ""
ls -la "$BUNDLE"
