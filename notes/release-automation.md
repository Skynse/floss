# Release automation (Gumroad)

Aseprite-style: one paid product, all platforms, buyers re-download updates from their Gumroad library. No in-app licensing or auto-updater.

All installers are **built on Linux CI** (cross-publish for Windows/macOS portable zips).

## Overview

| Step | Where | Trigger |
|------|--------|---------|
| Build installers | GitLab CI `pack:all` (Linux) | git tag `v*` |
| Upload binaries | `packaging/release/upload-gumroad.sh` | CI `deploy:gumroad` on tag |
| Purchase + downloads | Gumroad product | embed checkout on flosspaint.com |
| Updates | Buyer Gumroad library | manual re-download after each release |

### Artifacts per release

| Platform | File |
|----------|------|
| Linux | AppImage + Flatpak |
| Windows | portable zip |
| macOS arm64 + x64 | `.dmg` with `Floss.app` (unsigned, built on Linux) |

## One-time setup

### 1. Gumroad product

1. [Gumroad](https://gumroad.com) → create a **digital product** (single tier).
2. Set **custom permalink** to `floss` (Share → URL). Public link: `https://popshuvit.gumroad.com/l/floss`.
3. Copy **product id** from edit URL: `…/products/<GUMROAD_PRODUCT_ID>/edit` (stays `dqmpcx` — used by CLI/CI, not the public slug).
4. [Advanced settings](https://app.gumroad.com/settings/advanced) → **access token** with `edit_products` scope.

### 2. GitLab CI variables

```bash
glab variable set GUMROAD_ACCESS_TOKEN "..." -m -p
glab variable set GUMROAD_PRODUCT_ID "dqmpcx" -p
glab variable set GUMROAD_PRODUCT_URL "https://popshuvit.gumroad.com/l/floss" -p
```

### 3. floss-site

Point the download/buy page at your Gumroad product. Gumroad is fulfillment — not the marketing site CDN.

## Release workflow

```bash
# Bump <Version> in src/Floss.App/Floss.App.csproj, commit, then:
./packaging/release/release.sh
```

GitLab: `pack:all` → `deploy:gumroad` (tests are local only).

## Local upload (without CI)

```bash
export GUMROAD_ACCESS_TOKEN=...
export GUMROAD_PRODUCT_ID=dqmpcx

./packaging/velopack/pack.sh linux-x64
./packaging/flatpak/build.sh
./packaging/portable/pack.sh all
./packaging/release/upload-gumroad.sh
```

## Key files

| File | Role |
|------|------|
| `packaging/release/release.sh` | `git tag` + `git push` from csproj Version |
| `packaging/release/upload-gumroad.sh` | Upload/replace product files via gumroad-cli |
| `packaging/portable/pack.sh` | Cross-platform portable zips from Linux |
| `.gitlab-ci.yml` | `pack:all` + `deploy:gumroad` on tags |
