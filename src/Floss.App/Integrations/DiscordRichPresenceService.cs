using System;
using System.Reflection;
using DiscordRPC;
using DiscordRPC.Logging;

namespace Floss.App.Integrations;

/// <summary>
/// Discord Rich Presence via local IPC to the desktop client.
/// Requires a registered Discord application ID and the Discord app running.
/// </summary>
public sealed class DiscordRichPresenceService : IDisposable
{
    private const int MaxFieldLength = 128;

    private DiscordRpcClient? _client;
    private DateTime? _sessionStartUtc;
    private bool _disposed;

    public bool IsEnabled => App.Config.DiscordRichPresenceEnabled;

    public void Start()
    {
        if (_disposed || !IsEnabled)
            return;

        var appId = ResolveApplicationId();
        if (string.IsNullOrWhiteSpace(appId))
            return;

        if (_client != null)
            return;

        _client = new DiscordRpcClient(appId)
        {
            Logger = new ConsoleLogger(LogLevel.Warning, false)
        };
        _client.OnReady += (_, _) => PushPresence(_lastDetails, _lastState);
        _client.Initialize();
        _sessionStartUtc ??= DateTime.UtcNow;
        PushPresence(_lastDetails, _lastState);
    }

    public void Stop()
    {
        _client?.Dispose();
        _client = null;
        _sessionStartUtc = null;
    }

    public void Update(string? documentName, string? toolName, string? canvasSize)
    {
        if (!IsEnabled)
        {
            Stop();
            return;
        }

        Start();

        string details;
        string state;
        if (string.IsNullOrWhiteSpace(documentName))
        {
            details = "In Floss Studio";
            state = string.IsNullOrWhiteSpace(toolName) ? "Idle" : toolName;
        }
        else
        {
            details = "Editing";
            var parts = new System.Collections.Generic.List<string> { documentName };
            if (!string.IsNullOrWhiteSpace(toolName))
                parts.Add(toolName);
            if (!string.IsNullOrWhiteSpace(canvasSize))
                parts.Add(canvasSize);
            state = string.Join(" — ", parts);
        }

        PushPresence(details, state);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private string _lastDetails = "In Floss Studio";
    private string _lastState = "Idle";

    private void PushPresence(string details, string state)
    {
        _lastDetails = Truncate(details, MaxFieldLength);
        _lastState = Truncate(state, MaxFieldLength);

        if (_client == null || !_client.IsInitialized)
            return;

        var presence = new RichPresence
        {
            Details = _lastDetails,
            State = _lastState,
            Assets = new Assets
            {
                LargeImageKey = DiscordRichPresenceDefaults.LargeImageKey,
                LargeImageText = "Floss Studio",
                SmallImageKey = DiscordRichPresenceDefaults.SmallImageKey,
                SmallImageText = AppVersionLabel()
            },
            Buttons =
            [
                new Button
                {
                    Label = "Website",
                    Url = DiscordRichPresenceDefaults.WebsiteUrl
                }
            ]
        };

        if (_sessionStartUtc is { } started)
            presence.Timestamps = Timestamps.FromTimeSpan(DateTime.UtcNow - started);

        _client.SetPresence(presence);
    }

    private static string ResolveApplicationId()
    {
        var fromConfig = App.Config.DiscordApplicationId;
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig.Trim();

        var fromEnv = Environment.GetEnvironmentVariable("FLOSS_DISCORD_APP_ID");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        return DiscordRichPresenceDefaults.ApplicationId;
    }

    private static string AppVersionLabel()
    {
        try
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver == null ? "Floss" : $"Floss {ver}";
        }
        catch
        {
            return "Floss";
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..(maxLength - 1)] + "…";
    }
}

internal static class DiscordRichPresenceDefaults
{
    public const string ApplicationId = "1512829417619984497";

    /// <summary>Rich Presence art asset key (Developer Portal → Rich Presence → Art Assets).</summary>
    public const string LargeImageKey = "floss";

    public const string SmallImageKey = "floss";

    public const string WebsiteUrl = "https://flosspaint.com";
}
