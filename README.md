# Oshi Tuner

Oshi Tuner is a Windows app for tracking Twitch channels you follow or subscribe to and watching streams in separate Edge or Chrome windows. Use Twitch Status to see what's live, or let Oshi Watch switch managed streams based on your rankings.

## Install and connect

1. Download the Windows x64 setup from [GitHub Releases](https://github.com/benjaminkwilliams/oshituner/releases/latest). The per-user installer includes the .NET runtime and adds **Oshi Tuner** to the Start menu.
2. Register a public application in the [Twitch Developer Console](https://dev.twitch.tv/console). In Oshi Tuner, enter its Client ID under **Plugins > Twitch > Client ID**. The app uses Twitch's device code flow.
3. Select **Connect Twitch**, then **Open Twitch**. Enter the displayed code and approve access in the browser. Choose **Enable subs** if your connected account needs subscription access.
4. To open and manage streams in a browser, pair Edge or Chrome under **Settings > Browser Integration** and load the bundled extension. Follow the [browser setup guide](docs/browser-integration.md) for unpacked installation and updates.

The app remembers the Client ID and stores Twitch authorization encrypted under your Windows account for each install folder. It restores the saved session on launch. The optional `TWITCH_CLIENT_ID` environment variable overrides the saved ID. See the [Twitch API guide](docs/twitch-api.md).

## Twitch Status

- **Windows > Twitch Status** opens the grid of followed channels and verified gifted subs. It shows live status, available subscription tiers, and saved Oshi ranks.
- Choose **Refresh > Now** for an immediate update or select 5, 10, 15, 30, or 60 minutes for automatic refresh. The default is 30 minutes. **Sort…** sets the default group order. Right-click a column heading to find, filter, or sort that column.
- **Watching** means a live channel has an open tab in either paired browser. Tab changes appear independently of the Twitch refresh timer. A selected Twitch tab can add a temporary row for a channel you don't follow.
- Select a browser icon in the **Watch** column to open a live stream in its own managed window. To bring an existing stream under Oshi's control, move it into its own window, right-click the row, and choose **Manage open stream** and the browser.
- To discover unfollowed gifted subs, leave Twitch's **Gifted Subscriptions** tab open in a paired browser. Once verified, the app can recheck those gifts with the browser closed.

## Oshi Watch

Right-click a followed or subscribed channel in Twitch Status and choose **Add to Oshi**. Open **Windows > Oshi** to name ranks, group channels at equal priority, assign browsers, and set separate Edge and Chrome tab limits. Right-click a row for **Set Rank** or **Move within rank**. Edit names, limits, and timings directly in their cells.

- Each browser's limit counts managed streams across all its windows, including streams opened from Twitch Status. A new managed stream gets its own window. When Oshi switches a slot, it reuses the same tab and window.
- A higher-ranked live channel can take over a lower-ranked slot. A newly live channel in the same rank can take over after the current stream's minimum watch time, which defaults to 30 minutes.
- When no ranked channel is being watched, Oshi can pick an eligible live follow or sub after 10 minutes by default. Both timing overrides accept 5 to 60 minutes per channel. Choose **Show unranked follows and subs** to set fallback timing for an unranked channel.
- Unfollowing a ranked channel preserves its saved rank. After removing it from Oshi, you can add it again once you follow or subscribe to it.

Right-click the Oshi grid and choose **Start Oshi Watch**. Manual launches start paused. **Settings > Auto Launch** lets a paired browser wake the app to the tray and start Oshi Watch, with a cooldown of 1 to 5 hours. The browser extensions also recover the Twitch player when it shows the supported **Error #1000** prompt. See the [browser integration guide](docs/browser-integration.md).

## Workspace, theme, and log

- Open panes from **Windows**, drag a pane tab to float it, or drop it on a dock target to dock it again. The pin keeps a docked group expanded. Each install remembers its workspace layout.
- Open **Settings > Theme** to change the palette, font, opacity, and background. Use **Vtuber 4k** or an image from a local file, UNC path, or HTTP/HTTPS URL. The app caches images and plays animated GIF and WebP backgrounds. Select **Allow local network destinations for this image URL** when you intend to load from a private network.
- Minimize or close the main window to keep Oshi Tuner running in the system tray. Right-click the tray icon and choose **Open Oshi Tuner** to restore the window or **Exit** to quit.
- Open **Windows > Log** to review startup, browser, and Oshi Watch activity. The per-install `oshi.jsonl` file retains 48 hours across launches. The pane shows its path and can copy the retained entries.

## Control a running copy from Codex

Choose **Help > Copy Codex configuration**, replace any previous Oshi Tuner entry in your local Codex configuration, and reload Codex. The snippet runs the selected installation's executable with `--mcp`. The app and client must run under the same Windows account and elevation level. Each installation has its own endpoint. See the [Oshi Tuner agent skill](.agents/skills/oshi-tuner/SKILL.md).

For JSON-RPC 2.0, run the selected executable with `--rpc`, write one UTF-8 request to standard input, close input, and read the response from standard output. The connection uses the same local Windows permissions as MCP. For example, `{"jsonrpc":"2.0","id":1,"method":"get_state"}` returns instance and Twitch session information. Use `get_oshi` for rankings and browser assignments or `get_log` for retained log entries.

## Build from source

Install the .NET 10 SDK on Windows, then build and run from the repository root.

```powershell
dotnet build src/oshituner/oshituner.csproj -warnaserror
dotnet run --project src/oshituner/oshituner.csproj --no-build
```

### Development tools

Install [ripgrep](https://github.com/BurntSushi/ripgrep) and set up Graphify with Python 3.14 and uv.

```powershell
winget install --id BurntSushi.ripgrep.MSVC --exact
winget install --id astral-sh.uv --exact
uv python install 3.14
uv tool install --python 3.14 graphifyy
graphify install --project --platform agents
graphify update .
```

### Build versions and installer

Local builds increment the app's build number in each checkout. The Twitch build number advances when a `Twitch*.cs` source file changes. Open **Help > About** to see both versions.

`src/oshituner/Version.props` holds the official versions used by CI. For an external pull request, stage or commit your changes, run `./scripts/Set-OfficialVersion.ps1` against the official base, and commit the resulting version change. To publish the Windows installer, push a tag on `main` that matches the official app version. The release workflow tests installation and uninstallation, attests the tested installer, and verifies its provenance before publishing.

To verify a downloaded installer with GitHub CLI, substitute the filename and release tag.

```powershell
gh attestation verify .\Oshi-Tuner-VERSION-win-x64-Setup.exe --repo benjaminkwilliams/oshituner --signer-workflow benjaminkwilliams/oshituner/.github/workflows/release.yml --source-ref refs/tags/vVERSION --deny-self-hosted-runners
```

To build it locally, install Inno Setup 6 and run `./scripts/Build-WindowsInstaller.ps1`.
