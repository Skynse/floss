#!/usr/bin/env bash
# Push R2/CDN CI variables to GitLab from packaging/release/.env.release
#
# One-time:
#   cp packaging/release/.env.release.example packaging/release/.env.release
#   # fill in GITLAB_TOKEN + R2_* values
#   ./packaging/release/setup-gitlab-ci.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ENV_FILE="$ROOT/packaging/release/.env.release"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Missing $ENV_FILE" >&2
  echo "Copy from packaging/release/.env.release.example and fill in values." >&2
  exit 1
fi

# shellcheck disable=SC1090
set -a
source "$ENV_FILE"
set +a

if [[ -z "${GITLAB_TOKEN:-}" ]]; then
  echo "Set GITLAB_TOKEN in $ENV_FILE (api scope)." >&2
  exit 1
fi

PROJECT="${GITLAB_PROJECT:-Skynse/floss}"
PROJECT_ENC="${PROJECT//\//%2F}"
API="https://gitlab.com/api/v4/projects/${PROJECT_ENC}"

require_var() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "Missing $name in $ENV_FILE" >&2
    exit 1
  fi
}

require_var R2_ACCESS_KEY_ID
require_var R2_SECRET_ACCESS_KEY
require_var R2_ACCOUNT_ID
require_var R2_BUCKET
require_var CDN_PUBLIC_URL

if ! command -v curl >/dev/null; then
  echo "curl is required." >&2
  exit 1
fi

gitlab_api() {
  local method="$1"
  local path="$2"
  shift 2
  curl -fsS -X "$method" \
    --header "PRIVATE-TOKEN: $GITLAB_TOKEN" \
    "$@" \
    "${API}${path}"
}

upsert_variable() {
  local key="$1"
  local value="$2"
  local masked="${3:-true}"
  local protected="${4:-true}"

  if gitlab_api GET "/variables/${key}" >/dev/null 2>&1; then
    gitlab_api PUT "/variables/${key}" \
      --form "value=${value}" \
      --form "masked=${masked}" \
      --form "protected=${protected}" \
      >/dev/null
    echo "  updated $key"
  else
    gitlab_api POST "/variables" \
      --form "key=${key}" \
      --form "value=${value}" \
      --form "masked=${masked}" \
      --form "protected=${protected}" \
      >/dev/null
    echo "  created $key"
  fi
}

echo "==> GitLab CI variables → $PROJECT"
upsert_variable R2_ACCESS_KEY_ID "$R2_ACCESS_KEY_ID" true true
upsert_variable R2_SECRET_ACCESS_KEY "$R2_SECRET_ACCESS_KEY" true true
upsert_variable R2_ACCOUNT_ID "$R2_ACCOUNT_ID" false true
upsert_variable R2_BUCKET "$R2_BUCKET" false true
upsert_variable CDN_PUBLIC_URL "$CDN_PUBLIC_URL" false true

if [[ -n "${R2_PREFIX:-}" ]]; then
  upsert_variable R2_PREFIX "$R2_PREFIX" false true
fi
if [[ -n "${R2_ENDPOINT:-}" ]]; then
  upsert_variable R2_ENDPOINT "$R2_ENDPOINT" false true
fi

echo ""
echo "Done. Push a tag to release:"
echo "  ./packaging/release/release.sh"
