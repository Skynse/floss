#!/usr/bin/env bash
# Sign and notarize Floss.app / .dmg on macOS (Developer ID + notarytool).
#
# Cannot run on Linux — requires codesign, hdiutil, notarytool.
#
# Usage:
#   source packaging/macos/.env.signing   # or export vars manually
#   ./packaging/macos/sign-and-notarize.sh --app /path/to/Floss.app
#   ./packaging/macos/sign-and-notarize.sh --dmg artifacts/macos/FlossPaint-osx-arm64-beta.dmg
#   ./packaging/macos/sign-and-notarize.sh osx-arm64   # publish + sign + dmg + notarize
#
# Outputs signed (+ notarized when configured) DMG under artifacts/macos/.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
ENTITLEMENTS="$ROOT/packaging/macos/entitlements.plist"
PLIST_SRC="$ROOT/packaging/macos/Info.plist"
OUT_ROOT="$ROOT/artifacts/macos"
VERSION="${FLOSS_VERSION:-$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo)}"

require_macos() {
  if [[ "$(uname -s)" != Darwin ]]; then
    echo "macOS signing must run on a Mac (codesign / notarytool / hdiutil)." >&2
    echo "Build unsigned on Linux: ./packaging/macos/pack-dmg.sh osx-arm64" >&2
    echo "Then copy the DMG here and run: $0 --dmg <path>" >&2
    exit 1
  fi
}

require_cmd() {
  command -v "$1" >/dev/null || { echo "Missing: $1" >&2; exit 1; }
}

resolve_signing_identity() {
  if [[ -n "${APPLE_SIGNING_IDENTITY:-}" ]]; then
    echo "$APPLE_SIGNING_IDENTITY"
    return
  fi
  # First matching Developer ID Application identity in login keychain.
  local id
  id="$(security find-identity -v -p codesigning 2>/dev/null \
    | sed -n 's/.*"\(Developer ID Application:.*\)".*/\1/p' \
    | head -1 || true)"
  if [[ -z "$id" ]]; then
    echo "Set APPLE_SIGNING_IDENTITY or install a Developer ID Application certificate." >&2
    exit 1
  fi
  echo "$id"
}

is_macho() {
  file -b "$1" 2>/dev/null | grep -q 'Mach-O'
}

sign_native_binaries() {
  local app="$1"
  local identity="$2"
  local macos_dir="$app/Contents/MacOS"
  local apphost="$macos_dir/Floss"

  echo "==> Signing Mach-O binaries in $macos_dir (excluding apphost)"
  while IFS= read -r -d '' f; do
    [[ "$f" == "$apphost" ]] && continue
    is_macho "$f" || continue
    codesign --force --options runtime --timestamp --sign "$identity" "$f"
  done < <(find "$macos_dir" -type f -print0)

  echo "==> Signing apphost with entitlements"
  codesign --force --options runtime --timestamp \
    --entitlements "$ENTITLEMENTS" \
    --sign "$identity" \
    "$apphost"

  echo "==> Signing app bundle"
  codesign --force --options runtime --timestamp \
    --entitlements "$ENTITLEMENTS" \
    --sign "$identity" \
    "$app"

  codesign --verify --deep --strict --verbose=2 "$app"
}

assemble_app_from_publish() {
  local rid="$1"
  local publish="$ROOT/artifacts/publish/$rid"
  local staging="$ROOT/artifacts/macos/staging-$rid"
  local app="$staging/Floss.app"

  rm -rf "$staging"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

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

  sed "s/VERSION_PLACEHOLDER/$VERSION/g" "$PLIST_SRC" > "$app/Contents/Info.plist"
  printf 'APPL????' > "$app/Contents/PkgInfo"
  if [[ -f "$ROOT/packaging/icon.png" ]]; then
    cp "$ROOT/packaging/icon.png" "$app/Contents/Resources/icon.png"
  fi

  echo "$app"
}

create_dmg() {
  local staging="$1"
  local rid="$2"
  local dmg_path="$OUT_ROOT/FlossPaint-${rid}-beta-signed.dmg"

  mkdir -p "$OUT_ROOT"
  rm -f "$dmg_path"

  cat > "$staging/READ ME FIRST.txt" <<EOF
Floss $VERSION ($rid)

Drag Floss.app to Applications, eject this disk, then open normally.
This build is signed and notarized for macOS Gatekeeper.
EOF

  echo "==> Creating DMG with hdiutil"
  hdiutil create \
    -volname "Floss $VERSION" \
    -srcfolder "$staging" \
    -ov \
    -format UDZO \
    "$dmg_path" >/dev/null

  echo "$dmg_path"
}

sign_dmg() {
  local dmg="$1"
  local identity="$2"
  echo "==> Signing DMG"
  codesign --force --timestamp --sign "$identity" "$dmg"
}

notarize_submit() {
  local artifact="$1"
  echo "==> Submitting to Apple notary service"

  local -a args=(submit "$artifact" --wait)
  if [[ -n "${APPLE_NOTARY_KEYCHAIN_PROFILE:-}" ]]; then
    args+=(--keychain-profile "$APPLE_NOTARY_KEYCHAIN_PROFILE")
  elif [[ -n "${APPLE_ID:-}" && -n "${APPLE_APP_PASSWORD:-}" && -n "${APPLE_TEAM_ID:-}" ]]; then
    args+=(--apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID")
  else
    echo "Set APPLE_NOTARY_KEYCHAIN_PROFILE or APPLE_ID + APPLE_APP_PASSWORD + APPLE_TEAM_ID." >&2
    echo "Create profile: xcrun notarytool store-credentials Floss-Notary ..." >&2
    exit 1
  fi

  xcrun notarytool "${args[@]}"
}

staple_artifact() {
  local artifact="$1"
  echo "==> Stapling notarization ticket"
  xcrun stapler staple -v "$artifact"
  xcrun stapler validate -v "$artifact"
}

process_app() {
  local app="$1"
  local rid="${2:-osx-arm64}"
  local identity
  identity="$(resolve_signing_identity)"

  [[ -d "$app/Contents/MacOS" ]] || { echo "Not a .app bundle: $app" >&2; exit 1; }

  sign_native_binaries "$app" "$identity"

  local staging="$ROOT/artifacts/macos/staging-$rid"
  rm -rf "$staging"
  mkdir -p "$staging"
  cp -a "$app" "$staging/Floss.app"

  local dmg
  dmg="$(create_dmg "$staging" "$rid")"
  sign_dmg "$dmg" "$identity"

  if [[ "${SKIP_NOTARIZE:-0}" != "1" ]]; then
    notarize_submit "$dmg"
    staple_artifact "$dmg"
    echo ""
    echo "Done: $dmg (signed + notarized)"
  else
    echo ""
    echo "Done: $dmg (signed only; SKIP_NOTARIZE=1)"
  fi

  rm -rf "$staging"
  ls -lh "$dmg"
}

process_dmg() {
  local dmg_in="$1"
  local rid="${2:-osx-arm64}"
  [[ -f "$dmg_in" ]] || { echo "DMG not found: $dmg_in" >&2; exit 1; }

  local mount_dir work staging app_path
  mount_dir="$(mktemp -d /tmp/floss-mount.XXXXXX)"
  work="$(mktemp -d /tmp/floss-sign.XXXXXX)"
  trap 'hdiutil detach "$mount_dir" -quiet 2>/dev/null || true; rm -rf "$mount_dir" "$work"' EXIT

  echo "==> Mounting $dmg_in"
  hdiutil attach -readonly -nobrowse -mountpoint "$mount_dir" "$dmg_in" >/dev/null
  [[ -d "$mount_dir/Floss.app" ]] || { echo "Floss.app not found in DMG" >&2; exit 1; }

  cp -a "$mount_dir/Floss.app" "$work/Floss.app"
  hdiutil detach "$mount_dir" -quiet
  trap 'rm -rf "$work"' EXIT

  process_app "$work/Floss.app" "$rid"
}

pack_rid() {
  local rid="$1"
  local app
  app="$(assemble_app_from_publish "$rid")"
  process_app "$app" "$rid"
}

usage() {
  cat >&2 <<EOF
Usage:
  $0 osx-arm64|osx-x64              Build, sign, notarize on this Mac
  $0 --app /path/to/Floss.app [rid]   Sign + repackage existing .app
  $0 --dmg /path/to/unsigned.dmg [rid]

Environment:
  APPLE_SIGNING_IDENTITY              Developer ID Application: … (TEAMID)
  APPLE_NOTARY_KEYCHAIN_PROFILE       Keychain profile for xcrun notarytool
  APPLE_ID / APPLE_APP_PASSWORD / APPLE_TEAM_ID   (alternative to profile)
  SKIP_NOTARIZE=1                     Sign only, skip notarization
EOF
  exit 1
}

main() {
  require_macos
  require_cmd codesign
  require_cmd hdiutil
  require_cmd xcrun
  require_cmd dotnet
  [[ -f "$ENTITLEMENTS" ]] || { echo "Missing $ENTITLEMENTS" >&2; exit 1; }

  if [[ $# -lt 1 ]]; then
    usage
  fi

  case "$1" in
    --app)
      [[ $# -ge 2 ]] || usage
      process_app "$2" "${3:-osx-arm64}"
      ;;
    --dmg)
      [[ $# -ge 2 ]] || usage
      process_dmg "$2" "${3:-osx-arm64}"
      ;;
    osx-arm64|osx-x64)
      pack_rid "$1"
      ;;
    *)
      usage
      ;;
  esac
}

main "$@"
