#!/usr/bin/env bash
# Install the same toolchain GitLab CI uses (Debian bookworm + .NET 10).
# Must be sourced (not executed) so PATH exports persist:
#   source packaging/ci/bootstrap.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PACKAGES_FILE="$ROOT/packaging/ci/debian-bookworm.packages"

if [[ ! -f "$PACKAGES_FILE" ]]; then
  echo "Missing package list: $PACKAGES_FILE" >&2
  exit 1
fi

mapfile -t PACKAGES < <(
  grep -Ev '^\s*(#|$)' "$PACKAGES_FILE"
)

if ((${#PACKAGES[@]} == 0)); then
  echo "No packages listed in $PACKAGES_FILE" >&2
  exit 1
fi

echo "==> apt-get install (${#PACKAGES[@]} packages from debian-bookworm.packages)"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends "${PACKAGES[@]}"

echo "==> dotnet SDK 10"
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
export DOTNET_ROOT=/usr/share/dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-LatestMajor}"
export DOTNET_NOLOGO="${DOTNET_NOLOGO:-true}"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-true}"

dotnet --version

if [[ -n "${CI_COMMIT_TAG:-}" ]]; then
  export FLOSS_VERSION="${CI_COMMIT_TAG#v}"
  echo "Release version: $FLOSS_VERSION"
fi
