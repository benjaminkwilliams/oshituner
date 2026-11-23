---
name: oshi-tuner
description: Control a running Oshi Tuner installation through its local MCP tools, including Twitch status and refresh, browser pairing, pane placement, and theme settings.
---

# Control Oshi Tuner

Call `get_state` first. Use the install folder, app version, and Twitch integration version to identify the running copy. If multiple copies are available, ask which copy to control before changing state.

If the tools are missing, ask the user to open Oshi Tuner and choose **Help > Copy Codex configuration**. Have them paste the snippet into their local Codex configuration and reload the client. Treat the snippet’s authorization header as a local credential.

- Use `get_channels` with `online: true` and `subbed: true` to find known online subs. `followed`, `gifted`, and `subscriptionCheckStale` distinguish the row states. Discovery for newly gifted channels depends on a paired browser with an available Twitch Gifts tab. Helix rechecks remembered channels even when the browser is unavailable.
- Use `refresh` to request fresh data and poll `get_state` until the provider’s `busy` flag clears. Read its message before interpreting the grid.
- For Twitch sign-in, use `set_client_id` if needed, then `connect`. When `activationPending` is set, use `open_activation` and let the user approve access in the browser. Use `cancel` or `disconnect` when asked.
- Use `get_auto_refresh` and `set_auto_refresh` for the saved 5, 10, 15, 30, or 60 minute interval. The provider's `giftDiscoveryStatus` explains whether browser discovery ran on the last refresh.
- Use `get_browser_pairings` before `set_browser_pairing`. Pair only the requested browser. Pass `replace: true` only after the user approves switching an existing browser pairing to this installation.
- Use `get_workspace` to inspect dock and floating groups. `open_pane` focuses an existing pane or opens it at its last dock. The Twitch pane ID is `twitch-status`. `move_pane` accepts Center, Top, Bottom, Left or Right, or floats when the dock is omitted. `pin_pane` changes the group containing a docked pane. Use `join_floating_group` to move a pane into another floating group and `redock_floating_group` to redock a whole group.
- Use `get_theme` before applying a change with `set_theme`, which replaces the saved settings for this copy. Use `import_theme_image` to cache a user-provided file, UNC path or HTTP/HTTPS URL and pass its returned ID as `imageId`. Use `reset_theme` when the user asks for defaults.
- Use `get_sort` before `set_sort`. Supply all four group names in the desired order and the secondary sort. Use `reset_sort` only when asked to restore defaults.
- Use `open_channel` to hand follow and sub actions to Twitch. Use `get_about` to inspect the running versions and notices or `open_about` to show the window.
- If the endpoint is unavailable, check that the selected copy is running. If it reports an expired session, ask the user to reconnect through the browser.
