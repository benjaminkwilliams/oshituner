using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace oshituner;

internal sealed class TunerControl(
    TunerSession session, InstallProfile profile, Dispatcher dispatcher,
    Action openAbout, Action closeAbout, Func<bool> isAboutOpen,
    Action openClientId, Action closeClientId, Func<bool> isClientIdOpen)
{
    public TunerState GetState() => new(1, profile.InstallFolder, AppInformation.Current.Version, AppInformation.Current.TwitchVersion,
        [new("twitch", session.ClientId, session.UserId, session.Busy, session.HasSavedAuthorization,
            session.ActivationUri is not null, session.CanReadSubscriptions, session.Message)],
        session.Channels.Count, session.Channels.Count(row => row.IsLive), isAboutOpen(), isClientIdOpen());

    public FollowedChannelRow[] GetChannels(bool? online = null, bool? subbed = null) =>
        session.Channels.Where(row => (!online.HasValue || row.Online == online)
            && (!subbed.HasValue || row.Subbed == subbed)).ToArray();

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

    public void OpenChannel(string provider, string channelId)
    {
        RequireTwitch(provider);
        var channel = session.Channels.FirstOrDefault(row => row.Provider == provider && row.ChannelId == channelId)
            ?? throw new ArgumentException("The channel is not in the current grid.");
        Open(new Uri("https://www.twitch.tv/" + Uri.EscapeDataString(channel.Login)));
    }

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
            _ => throw new ArgumentException("Choose repository, donations, license, or mcp_license.")
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
    bool HasSavedAuthorization, bool ActivationPending, bool CanReadSubscriptions, string Message);

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
            new("MCP C# SDK", "Apache-2.0 and MIT notices", "https://github.com/modelcontextprotocol/csharp-sdk/blob/4cbf42ef107763f0bc75c358af8cb2dfe47a769a/LICENSE")
        ]);
}
