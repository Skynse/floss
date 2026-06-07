#!/usr/bin/env bash
# Fail fast before a long pack run if a required host tool is missing.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
missing=0

require_cmd() {
  local name="$1"
  if ! command -v "$name" >/dev/null 2>&1; then
    echo "MISSING: $name" >&2
    missing=1
  fi
}

require_one_of() {
  local label="$1"
  shift
  for cmd in "$@"; do
    if command -v "$cmd" >/dev/null 2>&1; then
      return 0
    fi
  done
  echo "MISSING: one of $label (${*// /, })" >&2
  missing=1
}

echo "==> Checking pack toolchain"

require_cmd dotnet
require_cmd flatpak
require_cmd flatpak-builder
require_cmd zip
require_one_of "ImageMagick" magick convert
require_one_of "ISO9660 tool" genisoimage mkisofs
require_cmd mksquashfs
require_cmd eu-strip

if ! dotnet msbuild "$PROJECT" -getProperty:Version -nologo >/dev/null 2>&1; then
  echo "MISSING: readable Version in $PROJECT" >&2
  missing=1
fi

cd "$ROOT"
dotnet tool restore >/dev/null
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-LatestMajor}"
if ! dotnet tool run vpk --help >/dev/null 2>&1; then
  echo "MISSING: vpk (dotnet tool restore failed or vpk unavailable)" >&2
  missing=1
fi

if ((missing)); then
  echo "" >&2
  echo "Install Debian bookworm deps:" >&2
  echo "  sudo ./packaging/ci/bootstrap.sh" >&2
  echo "Or run the CI-parity check in Docker:" >&2
  echo "  ./packaging/ci/verify-pack.sh" >&2
  exit 1
fi

echo "All pack dependencies present."
