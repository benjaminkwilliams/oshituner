---
name: oshi-tuner
description: Use Oshi Tuner’s local MCP tools to inspect the Twitch session, list followed or subbed channels, check who is online, refresh the grid, change sorting, or open About in a running Oshi Tuner window.
---

# Control Oshi Tuner

Call `get_state` first. Use the install folder, app version, and Twitch integration version to identify the running copy. If multiple copies are available, ask which copy to control before changing state.

If the tools are missing, ask the user to open Oshi Tuner and choose **Help > Copy Codex configuration**. Have them paste the snippet into their local Codex configuration and reload the client. Treat the snippet’s authorization header as a local credential.

- Use `get_channels` with `online: true` and `subbed: true` to find known online subs. Explain that Twitch verifies subs for followed channels, so the view can omit sub-only channels.
- Use `refresh` to request fresh data and poll `get_state` until the provider’s `busy` flag clears. Read its message before interpreting the grid.
- For Twitch sign-in, use `set_client_id` if needed, then `connect`. When `activationPending` is set, use `open_activation` and let the user approve access in the browser. Use `cancel` or `disconnect` when asked.
- Use `get_sort` before `set_sort`. Supply all four group names in the desired order and the secondary sort. Use `reset_sort` only when asked to restore defaults.
- Use `open_channel` to hand follow and sub actions to Twitch. Use `get_about` to inspect the running versions and notices or `open_about` to show the window.
- If the endpoint is unavailable, check that the selected copy is running. If it reports an expired session, ask the user to reconnect through the browser.
