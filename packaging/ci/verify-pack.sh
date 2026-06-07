#!/usr/bin/env bash
# Run pack:all inside debian:bookworm — exact parity with GitLab CI.
#
# Usage:
#   ./packaging/ci/verify-pack.sh
#   ./packaging/ci/verify-pack.sh --check-deps-only
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CHECK_ONLY=false

for arg in "$@"; do
  case "$arg" in
    --check-deps-only) CHECK_ONLY=true ;;
    -h|--help)
      echo "Usage: $0 [--check-deps-only]"
      echo "Runs packaging/ci/bootstrap.sh + check-deps.sh (+ pack-all unless --check-deps-only)"
      echo "inside debian:bookworm via Docker."
      exit 0
      ;;
    *)
      echo "Unknown option: $arg" >&2
      exit 1
      ;;
  esac
done

if command -v docker >/dev/null 2>&1; then
  CONTAINER=docker
elif command -v podman >/dev/null 2>&1; then
  CONTAINER=podman
else
  echo "Neither docker nor podman found." >&2
  echo "Install one of them to run CI-parity pack verification." >&2
  exit 1
fi

chmod +x "$ROOT/packaging/ci/"*.sh \
  "$ROOT/packaging/velopack/pack.sh" \
  "$ROOT/packaging/flatpak/build.sh" \
  "$ROOT/packaging/portable/pack.sh" \
  "$ROOT/packaging/macos/pack-dmg.sh"

inner='source packaging/ci/bootstrap.sh && bash packaging/ci/check-deps.sh'
if ! $CHECK_ONLY; then
  inner="$inner && bash packaging/ci/pack-all.sh"
fi

echo "==> CI-parity verify in debian:bookworm via $CONTAINER (check_only=$CHECK_ONLY)"
# flatpak-builder needs FUSE for rofiles (GitLab runners provide it; local containers need --privileged).
$CONTAINER run --rm --privileged \
  -v "$ROOT:/builds/Skynse/floss:Z" \
  -w /builds/Skynse/floss \
  debian:bookworm \
  bash -lc "$inner"

echo ""
if $CHECK_ONLY; then
  echo "Dependency check passed in CI environment."
else
  echo "Full pack passed in CI environment."
fi
