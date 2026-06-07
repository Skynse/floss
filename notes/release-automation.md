# Release automation (CDN + site)

## Overview

| Step | Where | Trigger |
|------|--------|---------|
| Build installers | GitLab CI `pack:*` jobs | git tag `v*` |
| Upload binaries | `packaging/release/upload-cdn.sh` | CI `deploy:cdn` on tag |
| Download links | `cdn.flosspaint.com/downloads/*` | stable filenames |
| Version label on site | `cdn.flosspaint.com/downloads/version.json` | fetched at page render (no Vercel redeploy per release) |
| Marketing site | Vercel (`floss-site`) | git push to main |

## One-time setup

### 1. Secrets file (local, gitignored)

```bash
cp packaging/release/.env.release.example packaging/release/.env.release
# edit: GITLAB_TOKEN + R2_* values
./packaging/release/setup-gitlab-ci.sh
```

`setup-gitlab-ci.sh` pushes CI variables to GitLab via API (no web UI).

GitLab token: https://gitlab.com/-/user_settings/personal_access_tokens — scope **api**.

### Cloudflare R2

1. R2 bucket (e.g. `floss-cdn`)
2. Custom domain: `cdn.flosspaint.com` → bucket, public access or CDN rule
3. API token with Object Read & Write

### GitLab CI variables (floss repo, masked)

| Variable | Example |
|----------|---------|
| `R2_ACCESS_KEY_ID` | R2 access key |
| `R2_SECRET_ACCESS_KEY` | R2 secret |
| `R2_ACCOUNT_ID` | Cloudflare account id |
| `R2_BUCKET` | `floss-cdn` |
| `CDN_PUBLIC_URL` | `https://cdn.flosspaint.com` |

Optional: `R2_PREFIX` (default `downloads`), `R2_ENDPOINT` (override).

### Vercel (floss-site)

| Variable | Value |
|----------|--------|
| `NEXT_PUBLIC_DOWNLOAD_CDN` | `https://cdn.flosspaint.com` |

## Release workflow

```bash
# Bump <Version> in src/Floss.App/Floss.App.csproj, commit, then:
./packaging/release/release.sh
```

That tags `v{Version}`, pushes branch + tag → GitLab runs pack + `deploy:cdn`.

Or manually:

```bash
git tag v0.1.0-beta.2
git push origin main --tags
```

GitLab runs tests → pack jobs (linux always; win/mac if runners available) → `deploy:cdn` uploads artifacts to R2.

Site download page reads `version.json` from CDN; binary hrefs use `NEXT_PUBLIC_DOWNLOAD_CDN/downloads/<file>`.

## Local upload (without CI)

```bash
export R2_ACCESS_KEY_ID=...
export R2_SECRET_ACCESS_KEY=...
export R2_ACCOUNT_ID=...
export R2_BUCKET=floss-cdn
export CDN_PUBLIC_URL=https://cdn.flosspaint.com

./packaging/velopack/pack.sh linux-x64
./packaging/flatpak/build.sh
./packaging/portable/pack.sh all
./packaging/release/upload-cdn.sh
```

Requires [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html).

## Key files

| File | Role |
|------|------|
| `packaging/release/release.sh` | `git tag` + `git push` from csproj Version |
| `packaging/release/setup-gitlab-ci.sh` | Push CI variables via GitLab API |
| `packaging/release/.env.release.example` | Local secrets template |
| `packaging/release/upload-cdn.sh` | R2 upload + `version.json` |
| `.gitlab-ci.yml` | `deploy:cdn` stage on tags |
| `floss-site/app/content/floss.ts` | CDN base URL + download paths |
| `floss-site/app/lib/release-manifest.ts` | Fetch `version.json` |
