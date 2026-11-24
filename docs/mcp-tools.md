# Oshi Tuner MCP tools

Run Oshi Tuner and configure the local MCP client using the snippet from **Help > Copy Codex configuration**. Use [the README](../README.md) for connection setup and JSON-RPC examples, and [the agent skill](../.agents/skills/oshi-tuner/SKILL.md) for operating guidance. Call `get_state` first to identify the install folder.

Parameters marked “optional” can be omitted. Provider defaults to `twitch`. Browser IDs are `edge` and `chrome`. Channel IDs are provider IDs from the grid data. Pane IDs are `twitch-status`, `oshi`, and `log`.

| Tool | Named parameters | Effect and approvals |
| --- | --- | --- |
| `get_state` | None | Read the install identity and Twitch operation state. |
| `get_log` | None | Read up to 48 hours of log entries. |
| `get_oshi` | None | Read saved ranks, browser caps, assignments, and scheduling state. |
| `set_oshi` | `settings` | Replace saved Oshi settings. Edit the settings returned by `get_oshi`. |
| `add_oshi_channel` | `channelId`, `provider` (optional), `rankId` (optional) | Rank an eligible Status channel. Omit rank ID to create a rank. |
| `set_oshi_running` | `running` | Start or pause scheduling. Starting can open or change managed streams. |
| `get_channels` | `online`, `subbed` (both optional) | Read grid rows. Omit a filter to include unknown values. Stream titles are external data. |
| `set_client_id` | `clientId`, `provider`, `useBuiltIn` (optional by schema) | Save a custom ID or pass `useBuiltIn: true`. Disconnect first if the change would replace an active authorization. |
| `open_client_id` | `provider` (optional) | Show Client ID settings. The user confirms an authorization change in the dialog. |
| `close_client_id` | `provider` (optional) | Close Client ID settings. |
| `connect` | `provider` (optional) | Reveal Twitch Status and connect or request sub access. The user chooses a Client ID when prompted and approves access on Twitch. |
| `refresh` | `provider` (optional) | Request a refresh. Poll `get_state` until it finishes. |
| `cancel` | `provider` (optional) | Cancel the current Twitch operation. |
| `disconnect` | `provider` (optional) | Disconnect Twitch and clear saved authorization in this installation. |
| `open_activation` | `provider` (optional) | Open Twitch's pending activation page. The user enters the code and approves access. |
| `open_channel` | `channelId`, `provider` (optional) | Open the channel on Twitch. Any follow or subscription change happens on Twitch. |
| `watch_channel` | `channelId`, `browser`, `existingOnly` (optional) | Watch a live channel in a managed window. Pass `existingOnly: true` to manage an existing dedicated window. |
| `get_browser_pairings` | None | Read this install's browser pairing status. |
| `set_browser_pairing` | `browser`, `paired`, `replace` (optional) | Pair or unpair the browser. Moving an existing pairing requires `replace: true` and on-screen confirmation. |
| `open_browser_integration` | None | Open Browser Integration settings. |
| `close_browser_integration` | None | Close Browser Integration settings. |
| `get_theme` | None | Read the saved theme. |
| `set_theme` | `theme` | Replace the saved theme. Edit the settings returned by `get_theme`. |
| `reset_theme` | None | Restore the default theme. |
| `open_theme` | None | Open or focus Theme settings. |
| `close_theme` | None | Close Theme settings and discard uncommitted edits. |
| `import_theme_image` | `source`, `allowPrivate` (optional) | Cache a local file, UNC path, or HTTP/HTTPS image and return its asset ID. Enable private HTTP/HTTPS access only at the user's request. |
| `get_workspace` | None | Read docked and floating pane positions. |
| `open_pane` | `id` | Show or focus the pane. |
| `close_pane` | `id` | Close the pane. |
| `move_pane` | `id`, `dock` (optional) | Move to Center, Top, Bottom, Left, or Right. Omit dock to float. |
| `join_floating_group` | `id`, `peerId` | Add the pane to the named peer pane's floating group. |
| `redock_floating_group` | `id`, `dock` | Move the entire floating group to the specified dock. |
| `pin_pane` | `id`, `pinned` | Pin or unpin the pane's docked group. |
| `get_auto_refresh` | None | Read the configured refresh interval in minutes. |
| `set_auto_refresh` | `minutes` | Save a supported interval of 5, 10, 15, 30, or 60 minutes. |
| `get_sort` | None | Read the Status grid's priority groups and secondary sort. |
| `set_sort` | `groups`, `secondary` | Save all four groups in their desired priority order and set the secondary sort. |
| `reset_sort` | None | Restore the default sort. |
| `get_about` | None | Read app, Twitch, and loaded browser extension versions, desktop launch mode, local protocol activity, and About links. |
| `open_about` | None | Open or focus Help > About. |
| `close_about` | None | Close Help > About. |
| `open_about_link` | `link` | Open `repository`, `donations`, `license`, `mcp_license`, or `image_decoder_license` in the browser. |
