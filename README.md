# Oshi Tuner

A small Windows WPF app that shows which Twitch channels you follow are live.

## Setup

1. Register a Twitch application in the [Twitch Developer Console](https://dev.twitch.tv/console). Configure it as a **public** application that supports the device code grant. The app does not need a client secret.
2. To prefill the application's client ID, set it in PowerShell:

   ```powershell
   $env:TWITCH_CLIENT_ID = "your-client-id"
   ```

3. Run the app:

   ```powershell
   dotnet run --project src/oshituner
   ```

## Development tools

Install [ripgrep](https://github.com/BurntSushi/ripgrep) on Windows with:

```powershell
winget install --id BurntSushi.ripgrep.MSVC --exact
```

Restart the terminal if `rg` is not immediately available.

Graphify is installed with Python 3.14 and uv:

```powershell
winget install --id astral-sh.uv --exact
uv python install 3.14
uv tool install --python 3.14 graphifyy
graphify install --project --platform agents
```

Create or refresh the local knowledge graph with:

```powershell
graphify update .
```

In the app, enter the client ID if it is blank, then select **Connect Twitch**. Select **Open Twitch** and approve access using the displayed code. The app requests `user:read:follows` and `user:read:subscriptions`. It remembers the Client ID and stores encrypted authorization for each install folder under your Windows user profile. The `TWITCH_CLIENT_ID` environment variable overrides the saved ID.

Use **Sort** to choose group priority and a secondary sort. Use **Open on Twitch** to manage follows and subs. Twitch lets the app check sub status for followed channels. Subs outside the follow list can be missing.

## Control a running copy from Codex

Start Oshi Tuner and select **Help > Copy Codex configuration**. Paste the snippet into your local Codex configuration and reload Codex in VS Code. Keep that snippet local because it contains a credential for the authenticated loopback server. If multiple copies run from different folders, configure each separately and choose the intended copy by its install folder and version. The [agent skill](.agents/skills/oshi-tuner/SKILL.md) covers the available operations.

The same loopback host exposes JSON-RPC 2.0 at `/rpc`. Send a POST with the Bearer header from the local snippet and named parameters. For example, the request body `{"jsonrpc":"2.0","id":1,"method":"get_state"}` returns the instance and its current Twitch session state.

## Build versions

Local builds increment the app Build number in each checkout. The Twitch Build number advances when a `Twitch*.cs` source file changes. Open **Help > About** to see both versions.

`src/oshituner/Version.props` records the official versions. When preparing an outside PR for integration, fetch the latest official branch, stage or commit the PR changes, and run `./scripts/Set-OfficialVersion.ps1` from that integration checkout. Commit the resulting version change. The pull request check verifies the assigned app and Twitch versions against the official base. CI builds stamp the versions from `Version.props`.

See [docs/twitch-api.md](docs/twitch-api.md) for the API, authorization flow, and operational details.
