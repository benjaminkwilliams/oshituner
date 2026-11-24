using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace oshituner;

internal sealed class TunerControl(
    TunerSession session, InstallProfile profile, Dispatcher dispatcher,
    Action openAbout, Action closeAbout, Func<bool> isAboutOpen,
    Action openClientId, Action closeClientId, Func<bool> isClientIdOpen,
    BrowserRegistration browsers, Action openBrowserIntegration, Action closeBrowserIntegration, DockWorkspace workspace,
    ThemeController theme, Action openTheme, Action closeTheme, OshiCoordinator oshi, LaunchLog log)
{
    public TunerState GetState() => new(1, profile.InstallFolder, AppInformation.Current.Version, AppInformation.Current.TwitchVersion,
        [new("twitch", session.ClientId, session.UserId, session.Busy, session.HasSavedAuthorization,
            session.ActivationUri is not null, session.CanReadSubscriptions, session.Message, session.GiftDiscoveryStatus)],
        session.Channels.Count, session.Channels.Count(row => row.IsLive), isAboutOpen(), isClientIdOpen());

    public FollowedChannelRow[] GetChannels(bool? online = null, bool? subbed = null) =>
        session.Channels.Where(row => (!online.HasValue || row.Online == online)
            && (!subbed.HasValue || row.Subbed == subbed)).ToArray();

    public OshiState GetOshi() => oshi.State;

    public string[] GetLog() => log.Read();

    public OshiState SetOshi(OshiSettings settings)
    {
        session.SetOshi(settings);
        return GetOshi();
    }

    public OshiState SetOshiRunning(bool running)
    {
        oshi.SetRunning(running);
        return GetOshi();
    }

    public OshiState AddOshiChannel(OshiChannelKey key, string? rankId = null)
    {
        if (!session.CanRank(key))
            throw new ArgumentException("Follow or subscribe to this channel before adding it to Oshi.");
        var row = session.Channels.First(channel => channel.Provider == key.Provider && channel.ChannelId == key.Id);
        var entry = new OshiEntry(key, row.DisplayName);
        var settings = session.Oshi;
        if (settings.Ranks.Any(rank => rank.Channels.Any(existing => existing.Channel == key))) return GetOshi();
        if (rankId is null)
            return SetOshi(settings with { Ranks = [.. settings.Ranks, new(Guid.NewGuid().ToString("N"), [entry])] });
        if (!settings.Ranks.Any(rank => rank.Id == rankId)) throw new ArgumentException("The Oshi rank no longer exists.");
        return SetOshi(settings with
        {
            Ranks = settings.Ranks.Select(rank => rank.Id == rankId ? rank with { Channels = [.. rank.Channels, entry] } : rank).ToArray()
        });
    }

    public void SetClientId(string provider, string clientId)
    {
        RequireTwitch(provider);
        session.SetClientId(clientId);
    }

    public void OpenClientId(string provider)
    {
        RequireTwitch(provider);
        openClientId();
    }

    public void CloseClientId(string provider)
    {
        RequireTwitch(provider);
        closeClientId();
    }

    public void Start(string provider, bool allowSignIn)
    {
        RequireTwitch(provider);
        _ = session.StartRefresh(allowSignIn);
    }

    public void Cancel(string provider)
    {
        RequireTwitch(provider);
        session.Cancel();
    }

    public void Disconnect(string provider)
    {
        RequireTwitch(provider);
        session.Disconnect();
    }

    public void OpenActivation(string provider)
    {
        RequireTwitch(provider);
        Open(session.ActivationUri ?? throw new InvalidOperationException("Twitch activation is not pending."));
    }

    public async Task WatchChannelAsync(string channelId, string browser, CancellationToken token, bool existingOnly = false)
    {
        var target = BrowserRegistration.GetTarget(browser);
        var login = session.Channels.FirstOrDefault(channel => channel.ChannelId == channelId)?.Login ?? "unknown channel";
        var operation = existingOnly ? "Manage open stream" : "Watch";
        log.Write("Watch", $"{target.DisplayName}: {operation} requested for {login}.");
        try
        {
            await session.WatchChannelAsync(channelId, browser, token, existingOnly);
            log.Write("Watch", $"{target.DisplayName}: {operation} for {login} confirmed.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            log.Write("Watch", $"{target.DisplayName}: {operation} for {login} failed ({exception.GetType().Name}).");
            throw;
        }
    }

    public async Task<TunerState> WatchOnUiAsync(string channelId, string browser, CancellationToken token, bool existingOnly = false)
    {
        await OnUiAsync(() => WatchChannelAsync(channelId, browser, token, existingOnly), token).Unwrap();
        return await OnUiAsync(GetState, token);
    }

    public void OpenChannel(string provider, string channelId)
    {
        RequireTwitch(provider);
        var channel = session.Channels.FirstOrDefault(row => row.Provider == provider && row.ChannelId == channelId)
            ?? throw new ArgumentException("The channel is not in the current grid.");
        Open(new Uri("https://www.twitch.tv/" + Uri.EscapeDataString(channel.Login)));
    }

    public BrowserPairing[] GetBrowserPairings() => browsers.GetPairings();

    public BrowserPairing[] SetBrowserPairing(string browser, bool paired, bool replace = false)
    {
        var target = BrowserRegistration.GetTarget(browser);
        if (paired) browsers.Pair(target, replace);
        else browsers.Unpair(target);
        return GetBrowserPairings();
    }

    public void OpenBrowserIntegration() => openBrowserIntegration();
    public void CloseBrowserIntegration() => closeBrowserIntegration();

    public ThemeSettings GetTheme() => theme.Current;
    public ThemeSettings SetTheme(ThemeSettings settings) { theme.Set(settings); return GetTheme(); }
    public ThemeSettings ResetTheme() => SetTheme(new());
    public void OpenTheme() => openTheme();
    public void CloseTheme() => closeTheme();
    public Task<string> ImportThemeImageAsync(string source, CancellationToken token) => theme.Images.ImportAsync(source, token);

    public WorkspaceState GetWorkspace() => workspace.Snapshot();
    public void OpenPane(string id) => workspace.Open(id);
    public void ClosePane(string id) => workspace.Close(id);
    public void MovePane(string id, DockLocation? dock) => workspace.Move(id, dock);
    public void PinPane(string id, bool pinned) => workspace.SetPinned(id, pinned);
    public void JoinFloatingGroup(string id, string peerId) => workspace.JoinFloatingGroup(id, peerId);
    public void RedockFloatingGroup(string id, DockLocation dock) => workspace.RedockFloatingGroup(id, dock);

    public int GetAutoRefresh() => session.AutoRefreshMinutes;

    public void SetAutoRefresh(int minutes) => session.SetAutoRefreshMinutes(minutes);

    public GridSort GetSort() => session.Sort;

    public void SetSort(GridSort sort) => session.SetSort(sort);

    public void ResetSort() => SetSort(GridSort.Default());

    public void OpenAbout() => openAbout();

    public void CloseAbout() => closeAbout();

    public void OpenAboutLink(string link)
    {
        var url = link switch
        {
            "repository" => AppInformation.Current.Repository,
            "donations" => AppInformation.Current.Donations,
            "license" => AppInformation.Current.Licenses[0].Url,
            "mcp_license" => AppInformation.Current.Licenses[1].Url,
            "image_decoder_license" => AppInformation.Current.Licenses[2].Url,
            _ => throw new ArgumentException("Choose repository, donations, license, mcp_license, or image_decoder_license.")
        };
        Open(new Uri(url));
    }

    public Task<T> OnUiAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(operation, DispatcherPriority.Normal, cancellationToken).Task;

    public Task<TunerState> ChangeAsync(Action operation, CancellationToken cancellationToken) => OnUiAsync(() =>
    {
        operation();
        return GetState();
    }, cancellationToken);

    private static void RequireTwitch(string provider)
    {
        if (provider != "twitch") throw new ArgumentException("Supported provider: twitch.");
    }

    private static void Open(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Could not open the browser. Check Windows default browser settings.", exception);
        }
    }
}

internal sealed record TunerState(int ApiVersion, string InstallFolder, string Version, string TwitchVersion,
    ProviderState[] Accounts, int ChannelCount, int LiveCount, bool AboutOpen, bool ClientIdOpen);

internal sealed record ProviderState(string Provider, string ClientId, string? UserId, bool Busy,
    bool HasSavedAuthorization, bool ActivationPending, bool CanReadSubscriptions, string Message, string GiftDiscoveryStatus);

internal sealed record LicenseInfo(string Name, string License, string Url);

internal sealed record AppInformation(string Name, string Version, string TwitchVersion, string Repository,
    string ThankYou, string Donations, LicenseInfo[] Licenses)
{
    private const string RepositoryUrl = "https://github.com/benjaminkwilliams/oshituner";

    public static AppInformation Current { get; } = new(
        "Oshi Tuner", typeof(AppInformation).Assembly.GetName().Version!.ToString(),
        typeof(AppInformation).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(entry => entry.Key == "TwitchVersion").Value!,
        RepositoryUrl,
        "Thank you to the following for their support!\n\nShadow Hexmatyar", "https://ko-fi.com/bkwilliams",
        [
            new("Oshi Tuner", "MIT", RepositoryUrl + "/blob/main/LICENSE"),
            new("MCP C# SDK", "Apache-2.0 and MIT notices", "https://github.com/modelcontextprotocol/csharp-sdk/blob/4cbf42ef107763f0bc75c358af8cb2dfe47a769a/LICENSE"),
            new("SkiaSharp", "MIT and third-party notices", RepositoryUrl + "/blob/main/src/oshituner/ThirdParty/SkiaSharp-LICENSE.txt")
        ]);
}
