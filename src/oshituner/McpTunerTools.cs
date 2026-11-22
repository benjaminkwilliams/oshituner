using System.ComponentModel;
using ModelContextProtocol.Server;

namespace oshituner;

[McpServerToolType]
internal sealed class McpTunerTools
{
    [McpServerTool(Name = "get_state"), Description("Identify the running Oshi Tuner instance and read its provider connection and operation state.")]
    public static Task<TunerState> GetState(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetState, cancellationToken);

    [McpServerTool(Name = "get_channels"), Description("Query channels in the displayed order. Omitting a filter includes unknown values.")]
    public static Task<FollowedChannelRow[]> GetChannels(TunerControl control, CancellationToken cancellationToken,
        bool? online = null, bool? subbed = null) => control.OnUiAsync(() => control.GetChannels(online, subbed), cancellationToken);

    [McpServerTool(Name = "set_client_id"), Description("Set the current provider application Client ID before connecting.")]
    public static Task<TunerState> SetClientId(TunerControl control, CancellationToken cancellationToken,
        string clientId, string provider = "twitch") => control.ChangeAsync(() => control.SetClientId(provider, clientId), cancellationToken);

    [McpServerTool(Name = "open_client_id"), Description("Open the provider Client ID settings on the selected instance.")]
    public static Task<TunerState> OpenClientId(TunerControl control, CancellationToken cancellationToken, string provider = "twitch") =>
        control.ChangeAsync(() => control.OpenClientId(provider), cancellationToken);

    [McpServerTool(Name = "close_client_id"), Description("Close the provider Client ID settings on the selected instance.")]
    public static Task<TunerState> CloseClientId(TunerControl control, CancellationToken cancellationToken, string provider = "twitch") =>
        control.ChangeAsync(() => control.CloseClientId(provider), cancellationToken);

    [McpServerTool(Name = "connect"), Description("Start connecting or upgrading Twitch permissions. Check state for browser approval.")]
    public static Task<TunerState> Connect(TunerControl control, CancellationToken cancellationToken, string provider = "twitch") =>
        control.ChangeAsync(() => control.Start(provider, true), cancellationToken);

    [McpServerTool(Name = "refresh"), Description("Refresh Twitch data without starting browser authorization. Poll get_state for completion.")]
    public static Task<TunerState> Refresh(TunerControl control, CancellationToken cancellationToken, string provider = "twitch") =>
        control.ChangeAsync(() => control.Start(provider, false), cancellationToken);

    [McpServerTool(Name = "cancel"), Description("Cancel the running provider operation.")]
    public static Task<TunerState> Cancel(TunerControl control, CancellationToken cancellationToken, string provider = "twitch") =>
        control.ChangeAsync(() => control.Cancel(provider), cancellationToken);

    [McpServerTool(Name = "disconnect"), Description("Disconnect the provider for this copy of Oshi Tuner and clear its saved authorization.")]
    public static Task<TunerState> Disconnect(TunerControl control, CancellationToken cancellationToken, string provider = "twitch") =>
        control.ChangeAsync(() => control.Disconnect(provider), cancellationToken);

    [McpServerTool(Name = "open_activation"), Description("Open the pending activation page for the user to approve on Twitch.")]
    public static Task<TunerState> OpenActivation(TunerControl control, CancellationToken cancellationToken, string provider = "twitch") =>
        control.ChangeAsync(() => control.OpenActivation(provider), cancellationToken);

    [McpServerTool(Name = "open_channel"), Description("Open a displayed channel on Twitch for the user to manage their follow or sub.")]
    public static Task<TunerState> OpenChannel(TunerControl control, CancellationToken cancellationToken,
        string channelId, string provider = "twitch") =>
        control.ChangeAsync(() => control.OpenChannel(provider, channelId), cancellationToken);

    [McpServerTool(Name = "get_sort"), Description("Read the current priority groups and secondary sort.")]
    public static Task<GridSort> GetSort(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetSort, cancellationToken);

    [McpServerTool(Name = "set_sort"), Description("Set the ordered list of all four priority groups and secondary sort.")]
    public static Task<TunerState> SetSort(TunerControl control, CancellationToken cancellationToken,
        GridGroup[] groups, SecondarySort secondary) => control.ChangeAsync(() => control.SetSort(new(groups, secondary)), cancellationToken);

    [McpServerTool(Name = "reset_sort"), Description("Restore default channel sort priorities.")]
    public static Task<TunerState> ResetSort(TunerControl control, CancellationToken cancellationToken) =>
        control.ChangeAsync(control.ResetSort, cancellationToken);

    [McpServerTool(Name = "get_about"), Description("Read the running app version, repository, donations, and license notices.")]
    public static AppInformation GetAbout() => AppInformation.Current;

    [McpServerTool(Name = "open_about"), Description("Open the About window on the selected instance.")]
    public static Task<TunerState> OpenAbout(TunerControl control, CancellationToken cancellationToken) =>
        control.ChangeAsync(control.OpenAbout, cancellationToken);

    [McpServerTool(Name = "close_about"), Description("Close the About window on the selected instance.")]
    public static Task<TunerState> CloseAbout(TunerControl control, CancellationToken cancellationToken) =>
        control.ChangeAsync(control.CloseAbout, cancellationToken);

    [McpServerTool(Name = "open_about_link"), Description("Open one About link. Values are repository, donations, license, or mcp_license.")]
    public static Task<TunerState> OpenAboutLink(TunerControl control, CancellationToken cancellationToken, string link) =>
        control.ChangeAsync(() => control.OpenAboutLink(link), cancellationToken);
}
