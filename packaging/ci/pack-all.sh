#!/usr/bin/env bash
# Full release pack pipeline — same steps as GitLab CI pack:all.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"

cd "$ROOT"
bash "$ROOT/packaging/ci/check-deps.sh"

chmod +x \
  packaging/velopack/pack.sh \
  packaging/flatpak/build.sh \
  packaging/portable/pack.sh \
  packaging/macos/pack-dmg.sh

echo "==> dotnet build (Release)"
dotnet build "$PROJECT" -c Release

echo "==> Velopack AppImage"
./packaging/velopack/pack.sh linux-x64

echo "==> Flatpak bundle"
FLATPAK_SCOPE="${FLATPAK_SCOPE:-system}" ./packaging/flatpak/build.sh

echo "==> Windows portable + macOS DMGs"
./packaging/portable/pack.sh all

echo "==> Verifying artifacts"
require_artifact() {
  local pattern="$1"
  shopt -s nullglob
  local matches=($pattern)
  shopt -u nullglob
  if ((${#matches[@]} == 0)); then
    echo "Missing artifact: $pattern" >&2
    exit 1
  fi
  echo "  ok  ${matches[0]}"
}

require_artifact "$ROOT/artifacts/velopack/linux-x64-beta/*.AppImage"
require_artifact "$ROOT/artifacts/flatpak/com.flosspaint.Floss.flatpak"
require_artifact "$ROOT/artifacts/portable/FlossPaint-win-x64-beta-portable.zip"
require_artifact "$ROOT/artifacts/macos/FlossPaint-osx-arm64-beta.dmg"
require_artifact "$ROOT/artifacts/macos/FlossPaint-osx-x64-beta.dmg"

echo ""
echo "Pack complete. Artifacts under $ROOT/artifacts/"
