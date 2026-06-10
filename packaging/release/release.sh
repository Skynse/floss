#!/usr/bin/env bash
# Tag + push → GitLab CI builds installers and uploads to Gumroad.
#
# Usage:
#   ./packaging/release/release.sh           # tag v{Version from csproj}, push
#   ./packaging/release/release.sh --dry-run
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Floss.App/Floss.App.csproj"
DRY_RUN=false

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=true ;;
    -h|--help)
      echo "Usage: $0 [--dry-run]"
      echo "Reads Version from Floss.App.csproj, tags v{version}, pushes to origin."
      exit 0
      ;;
    *)
      echo "Unknown option: $arg" >&2
      exit 1
      ;;
  esac
done

VERSION="$(dotnet msbuild "$PROJECT" -getProperty:Version -nologo)"
TAG="v${VERSION}"

if [[ -z "$VERSION" ]]; then
  echo "Could not read Version from $PROJECT" >&2
  exit 1
fi

if ! git -C "$ROOT" diff --quiet || ! git -C "$ROOT" diff --cached --quiet; then
  echo "Working tree has uncommitted changes. Commit or stash first." >&2
  git -C "$ROOT" status -sb >&2
  exit 1
fi

if git -C "$ROOT" rev-parse "$TAG" >/dev/null 2>&1; then
  echo "Tag $TAG already exists." >&2
  echo "Bump <Version> in $PROJECT or delete the tag locally." >&2
  exit 1
fi

BRANCH="$(git -C "$ROOT" rev-parse --abbrev-ref HEAD)"
REMOTE="${RELEASE_REMOTE:-origin}"

echo "==> Release $TAG (csproj Version=$VERSION, branch=$BRANCH, remote=$REMOTE)"

run() {
  if $DRY_RUN; then
    echo "  [dry-run] $*"
  else
    "$@"
  fi
}

run git -C "$ROOT" tag -a "$TAG" -m "Release $VERSION"
run git -C "$ROOT" push "$REMOTE" HEAD
run git -C "$ROOT" push "$REMOTE" "$TAG"

echo ""
if $DRY_RUN; then
  echo "Dry run only. Re-run without --dry-run to publish."
else
  echo "Pushed $TAG. Watch pipeline:"
  echo "  https://gitlab.com/Skynse/floss/-/pipelines"
  echo ""
  echo "When deploy:gumroad finishes, check your Gumroad product files."
  if [[ -n "${GUMROAD_PRODUCT_URL:-}" ]]; then
    echo "  $GUMROAD_PRODUCT_URL"
  fi
fi
