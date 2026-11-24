using System.ComponentModel;
using ModelContextProtocol.Server;

namespace oshituner;

[McpServerToolType]
internal sealed class McpTunerTools
{
    [McpServerTool(Name = "get_state"), Description("Identify the running Oshi Tuner instance and read its provider connection and operation state.")]
    public static Task<TunerState> GetState(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetState, cancellationToken);

    [McpServerTool(Name = "get_log"), Description("Read the application log retained for up to 48 hours.")]
    public static Task<string[]> GetLog(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetLog, cancellationToken);

    [McpServerTool(Name = "get_oshi"), Description("Read saved channel ranks, browser limits, current assignments, and scheduling state.")]
    public static Task<OshiState> GetOshi(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetOshi, cancellationToken);

    [McpServerTool(Name = "set_oshi"), Description("Replace saved Oshi settings. Read get_oshi first and preserve ranks and preferences outside the requested edit.")]
    public static Task<OshiState> SetOshi(TunerControl control, CancellationToken cancellationToken, OshiSettings settings) =>
        control.OnUiAsync(() => control.SetOshi(settings), cancellationToken);

    [McpServerTool(Name = "add_oshi_channel"), Description("Rank a followed or subscribed Status channel. Omit rankId to create a new rank.")]
    public static Task<OshiState> AddOshiChannel(TunerControl control, CancellationToken cancellationToken,
        string channelId, string provider = "twitch", string? rankId = null) =>
        control.OnUiAsync(() => control.AddOshiChannel(new(provider, channelId), rankId), cancellationToken);

    [McpServerTool(Name = "set_oshi_running"), Description("Start or pause Oshi automation in the selected installation.")]
    public static Task<OshiState> SetOshiRunning(TunerControl control, CancellationToken cancellationToken, bool running) =>
        control.OnUiAsync(() => control.SetOshiRunning(running), cancellationToken);

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

    [McpServerTool(Name = "watch_channel"), Description("Open a managed Twitch stream in a separate browser window, or manage an existing stream with existingOnly.")]
    public static Task<TunerState> WatchChannel(TunerControl control, CancellationToken cancellationToken,
        string channelId, string browser, bool existingOnly = false) => control.WatchOnUiAsync(channelId, browser, cancellationToken, existingOnly);

    [McpServerTool(Name = "get_browser_pairings"), Description("Read Edge and Chrome pairing status for this installation.")]
    public static Task<BrowserPairing[]> GetBrowserPairings(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetBrowserPairings, cancellationToken);

    [McpServerTool(Name = "set_browser_pairing"), Description("Pair or unpair Edge or Chrome. Pass replace only with the user's approval to move an existing pairing.")]
    public static Task<BrowserPairing[]> SetBrowserPairing(TunerControl control, CancellationToken cancellationToken,
        string browser, bool paired, bool replace = false) =>
        control.OnUiAsync(() => control.SetBrowserPairing(browser, paired, replace), cancellationToken);

    [McpServerTool(Name = "open_browser_integration"), Description("Open Browser Integration settings.")]
    public static Task<TunerState> OpenBrowserIntegration(TunerControl control, CancellationToken cancellationToken) =>
        control.ChangeAsync(control.OpenBrowserIntegration, cancellationToken);

    [McpServerTool(Name = "close_browser_integration"), Description("Close Browser Integration settings.")]
    public static Task<TunerState> CloseBrowserIntegration(TunerControl control, CancellationToken cancellationToken) =>
        control.ChangeAsync(control.CloseBrowserIntegration, cancellationToken);

    [McpServerTool(Name = "get_theme"), Description("Read the theme of this installation.")]
    public static Task<ThemeSettings> GetTheme(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetTheme, cancellationToken);

    [McpServerTool(Name = "set_theme"), Description("Apply and save theme settings for this installation.")]
    public static Task<ThemeSettings> SetTheme(TunerControl control, CancellationToken cancellationToken, ThemeSettings theme) =>
        control.OnUiAsync(() => control.SetTheme(theme), cancellationToken);

    [McpServerTool(Name = "reset_theme"), Description("Restore this installation's default theme.")]
    public static Task<ThemeSettings> ResetTheme(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.ResetTheme, cancellationToken);

    [McpServerTool(Name = "open_theme"), Description("Open or focus the Theme window.")]
    public static Task<TunerState> OpenTheme(TunerControl control, CancellationToken cancellationToken) =>
        control.ChangeAsync(control.OpenTheme, cancellationToken);

    [McpServerTool(Name = "close_theme"), Description("Close the Theme window and discard uncommitted edits.")]
    public static Task<TunerState> CloseTheme(TunerControl control, CancellationToken cancellationToken) =>
        control.ChangeAsync(control.CloseTheme, cancellationToken);

    [McpServerTool(Name = "import_theme_image"), Description("Cache a local path, UNC path, or HTTP/HTTPS background image and return its asset ID.")]
    public static Task<string> ImportThemeImage(TunerControl control, CancellationToken cancellationToken, string source) =>
        control.ImportThemeImageAsync(source, cancellationToken);

    [McpServerTool(Name = "get_workspace"), Description("Read current docked and floating pane layout.")]
    public static Task<WorkspaceState> GetWorkspace(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetWorkspace, cancellationToken);

    [McpServerTool(Name = "open_pane"), Description("Open or focus a pane. The Twitch pane ID is twitch-status.")]
    public static Task<WorkspaceState> OpenPane(TunerControl control, CancellationToken cancellationToken, string id) =>
        control.OnUiAsync(() => { control.OpenPane(id); return control.GetWorkspace(); }, cancellationToken);

    [McpServerTool(Name = "close_pane"), Description("Close the requested status pane.")]
    public static Task<WorkspaceState> ClosePane(TunerControl control, CancellationToken cancellationToken, string id) =>
        control.OnUiAsync(() => { control.ClosePane(id); return control.GetWorkspace(); }, cancellationToken);

    [McpServerTool(Name = "move_pane"), Description("Move a pane to Center, Top, Bottom, Left or Right. Omit dock to float it.")]
    public static Task<WorkspaceState> MovePane(TunerControl control, CancellationToken cancellationToken, string id, DockLocation? dock = null) =>
        control.OnUiAsync(() => { control.MovePane(id, dock); return control.GetWorkspace(); }, cancellationToken);

    [McpServerTool(Name = "join_floating_group"), Description("Move a pane into the floating group containing a peer pane.")]
    public static Task<WorkspaceState> JoinFloatingGroup(TunerControl control, CancellationToken cancellationToken, string id, string peerId) =>
        control.OnUiAsync(() => { control.JoinFloatingGroup(id, peerId); return control.GetWorkspace(); }, cancellationToken);

    [McpServerTool(Name = "redock_floating_group"), Description("Redock every pane in the floating group containing the chosen pane.")]
    public static Task<WorkspaceState> RedockFloatingGroup(TunerControl control, CancellationToken cancellationToken, string id, DockLocation dock) =>
        control.OnUiAsync(() => { control.RedockFloatingGroup(id, dock); return control.GetWorkspace(); }, cancellationToken);

    [McpServerTool(Name = "pin_pane"), Description("Pin or unpin the docked group containing the pane.")]
    public static Task<WorkspaceState> PinPane(TunerControl control, CancellationToken cancellationToken, string id, bool pinned) =>
        control.OnUiAsync(() => { control.PinPane(id, pinned); return control.GetWorkspace(); }, cancellationToken);

    [McpServerTool(Name = "get_auto_refresh"), Description("Read the Twitch auto refresh interval in minutes.")]
    public static Task<int> GetAutoRefresh(TunerControl control, CancellationToken cancellationToken) =>
        control.OnUiAsync(control.GetAutoRefresh, cancellationToken);

    [McpServerTool(Name = "set_auto_refresh"), Description("Set Twitch auto refresh to 5, 10, 15, 30 or 60 minutes.")]
    public static Task<int> SetAutoRefresh(TunerControl control, CancellationToken cancellationToken, int minutes) =>
        control.OnUiAsync(() => { control.SetAutoRefresh(minutes); return control.GetAutoRefresh(); }, cancellationToken);

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

    [McpServerTool(Name = "open_about_link"), Description("Open one About link. Values are repository, donations, license, mcp_license, or image_decoder_license.")]
    public static Task<TunerState> OpenAboutLink(TunerControl control, CancellationToken cancellationToken, string link) =>
        control.ChangeAsync(() => control.OpenAboutLink(link), cancellationToken);
}
