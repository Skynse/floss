# Installers for Floss

Build distributable installers with [Velopack](https://velopack.io/). **No auto-updates** — users download a fresh installer from [flosspaint.com](https://flosspaint.com) when you ship a new version.

## Build locally

```bash
dotnet tool restore

# Linux — produces an AppImage
./packaging/velopack/pack.sh linux-x64

# Windows — produces Setup.exe + portable zip (run on Windows or a Windows CI job)
./packaging/velopack/pack.sh win-x64
```

## What to publish

Upload these to your download page (ignore the `.nupkg` and `releases.*.json` files — those are only for auto-update feeds):

| Platform | File to ship |
|----------|----------------|
| Linux x64 | `FlossPaint-linux-x64-beta.AppImage` |
| Windows x64 | `FlossPaint-win-x64-beta-Setup.exe` (name may vary — check output dir) |
| Windows portable | `FlossPaint-win-x64-beta-Portable.zip` |

Output directory: `artifacts/velopack/{rid}-beta/`

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
