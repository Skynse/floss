# Release automation (Gumroad)

Aseprite-style: one paid product, all platforms, buyers re-download updates from their Gumroad library. No in-app licensing or auto-updater.

## Overview

| Step | Where | Trigger |
|------|--------|---------|
| Build installers | GitLab CI `pack:*` jobs | git tag `v*` |
| Upload binaries | `packaging/release/upload-gumroad.sh` | CI `deploy:gumroad` on tag |
| Purchase + downloads | Gumroad product | embed checkout on flosspaint.com |
| Updates | Buyer Gumroad library | manual re-download after each release |

## One-time setup

### 1. Gumroad product

1. [Gumroad](https://gumroad.com) → create a **digital product** (single tier — same app for everyone).
2. Attach placeholder files or leave empty; CI replaces them on first tagged release.
3. Copy **product id** from the edit URL: `…/products/<GUMROAD_PRODUCT_ID>/edit`
4. [Advanced settings](https://app.gumroad.com/settings/advanced) → create **access token** with `edit_products` scope.

### 2. GitLab CI variables

```bash
cp packaging/release/.env.release.example packaging/release/.env.release
# edit GUMROAD_ACCESS_TOKEN + GUMROAD_PRODUCT_ID
./packaging/release/setup-gitlab-ci.sh
```

Or with glab:

```bash
glab variable set GUMROAD_ACCESS_TOKEN "..." -m -p
glab variable set GUMROAD_PRODUCT_ID "..." -p
glab variable set GUMROAD_PRODUCT_URL "https://yourname.gumroad.com/l/floss" -p
```

| Variable | Example |
|----------|---------|
| `GUMROAD_ACCESS_TOKEN` | Gumroad API token |
| `GUMROAD_PRODUCT_ID` | Product id from dashboard |
| `GUMROAD_PRODUCT_URL` | Public product link (optional, CI environment URL) |

### 3. floss-site

Point the download/buy page at your Gumroad product (embed widget or link). Do **not** host release binaries on the marketing site — Gumroad is fulfillment.

## Release workflow

```bash
# Bump <Version> in src/Floss.App/Floss.App.csproj, commit, then:
./packaging/release/release.sh
```

That tags `v{Version}`, pushes branch + tag → GitLab runs pack + `deploy:gumroad`.

Or manually:

```bash
git tag v0.1.0-beta.2
git push origin master --tags
```

GitLab runs tests → pack jobs → `deploy:gumroad` uploads artifacts to the Gumroad product (replaces all attached files).

Optional: send a [Gumroad email update](https://gumroad.com/help/article/169-how-to-send-an-update) to buyers when a release ships.

## Local upload (without CI)

```bash
export GUMROAD_ACCESS_TOKEN=...
export GUMROAD_PRODUCT_ID=...

./packaging/velopack/pack.sh linux-x64
./packaging/flatpak/build.sh
./packaging/portable/pack.sh all
./packaging/release/upload-gumroad.sh
```

## Key files

| File | Role |
|------|------|
| `packaging/release/release.sh` | `git tag` + `git push` from csproj Version |
| `packaging/release/setup-gitlab-ci.sh` | Push CI variables via GitLab API |
| `packaging/release/upload-gumroad.sh` | Upload/replace product files via gumroad-cli |
| `packaging/release/.env.release.example` | Local secrets template |
| `.gitlab-ci.yml` | `deploy:gumroad` stage on tags |
