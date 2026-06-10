# Installers for Floss

Build distributable installers with [Velopack](https://velopack.io/). **No auto-updates** — users download a fresh installer from [flosspaint.com](https://flosspaint.com) when you ship a new version.

## Build locally

```bash
dotnet tool restore

# Linux — AppImage + Flatpak
./packaging/velopack/pack.sh linux-x64
./packaging/flatpak/build.sh

# Windows + macOS from Linux
./packaging/portable/pack.sh win-x64          # portable zip
./packaging/macos/pack-dmg.sh all             # .app inside .dmg (unsigned)
./packaging/portable/pack.sh all              # win zip + mac dmg
```

Velopack `.dmg` via `vpk` still requires macOS (Apple codesign tools). We build unsigned DMGs on Linux with `packaging/macos/pack-dmg.sh` instead.

## What to publish

| Platform | CI / default file |
|----------|-------------------|
| Linux x64 (AppImage) | `FlossPaint-linux-x64-beta.AppImage` |
| Linux x64 (Flatpak) | `com.flosspaint.Floss.flatpak` |
| Windows x64 | `FlossPaint-win-x64-beta-portable.zip` |
| macOS Apple Silicon | `FlossPaint-osx-arm64-beta.dmg` |
| macOS Intel | `FlossPaint-osx-x64-beta.dmg` |

Output directories:
- Velopack: `artifacts/velopack/{rid}-beta/`
- Flatpak: `artifacts/flatpak/com.flosspaint.Floss.flatpak`

Copy everything to the site with `./packaging/release/sync-to-site.sh` (writes to `../floss-site/public/downloads/`).

## Version

Bump semver2 in `src/Floss.App/Floss.App.csproj`:

```xml
<Version>0.1.0-beta.2</Version>
```

Or override at pack time:

```bash
FLOSS_VERSION=0.1.0-beta.2 ./packaging/velopack/pack.sh linux-x64
```

## GitLab CI

Push a tag or run the pipeline manually — see `.gitlab-ci.yml`. Installer artifacts are attached to the pipeline job (and release, if you use tags).

## Notes

- Publish is multi-file self-contained (not single-file) — required by Velopack.
- `VelopackApp.Build().Run()` in `Program.cs` is only for install-time hooks when users run the Velopack installer — not for checking updates.
