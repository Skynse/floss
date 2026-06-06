# Discord Rich Presence

## Library
- NuGet: `DiscordRichPresence` 1.6.1.70 ([Lachee/discord-rpc-csharp](https://github.com/Lachee/discord-rpc-csharp))
- Docs: [Discord Setting Rich Presence](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)

## Floss files
| Area | Path |
|------|------|
| Service | `src/Floss.App/Integrations/DiscordRichPresenceService.cs` |
| MainWindow hooks | `src/Floss.App/MainWindow/MainWindow.Discord.cs` |
| Config | `AppConfig.DiscordRichPresenceEnabled`, `DiscordApplicationId` |
| Settings UI | `SettingsWindow.cs` → General → Discord |
| Lifecycle | `App.axaml.cs` — start on `MainWindow.Opened`, dispose on `desktop.Exit` |

## Application ID (required)
1. [Discord Developer Portal](https://discord.com/developers/applications) → New Application → **Floss**
2. Copy **Application ID**
3. Set one of:
   - `DiscordRichPresenceDefaults.ApplicationId` in `DiscordRichPresenceService.cs`
   - `DiscordApplicationId` in Floss config JSON
   - `FLOSS_DISCORD_APP_ID` environment variable

Default application ID: `1512829417619984497` (override via config or `FLOSS_DISCORD_APP_ID`).

## Art assets (Developer Portal → Rich Presence → Art Assets)
| Key | Use |
|-----|-----|
| `floss` | Large + small image (512×512 recommended) |

## Presence fields
- **Details:** `Editing` or `In Floss Studio`
- **State:** document name, active tool preset, canvas size
- **Button:** Website → https://flosspaint.com
- **Elapsed time:** session start when main window opens

## Requirements
- Discord **desktop** client running (Linux/Windows/macOS)
- User: Settings → Activity Privacy → display current activity enabled
