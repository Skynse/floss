# Local NuGet: DiscordRichPresence

NuGet.org `1.6.1.70` (Aug 2025) is the latest **published** release, but upstream [discord-rpc-csharp](https://github.com/Lachee/discord-rpc-csharp) master includes post-release fixes — notably null-safe `Assets.Merge` (PR #282) for Discord IPC responses on Linux.

We ship `DiscordRichPresence.1.6.1.71-master.nupkg` built from upstream master until a newer official package is published.

## Rebuild

```bash
git clone --depth 1 https://github.com/Lachee/discord-rpc-csharp.git /tmp/discord-rpc-csharp
cd /tmp/discord-rpc-csharp/DiscordRPC
# Bump version in DiscordRPC.nuspec if replacing the checked-in package
dotnet pack -c Release -o /path/to/floss/third_party/nuget
```

Restore uses `nuget.config` at the repo root (`floss-local` source).
