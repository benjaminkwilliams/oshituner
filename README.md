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

In the app, enter the client ID if it is blank, then select **Connect Twitch**. Select **Open Twitch** and approve access using the displayed code. The app requests only `user:read:follows`. It stores the authorization under your Windows user profile, protected with DPAPI. For automatic sign-in on subsequent runs, set `TWITCH_CLIENT_ID` to the same client ID before starting the app.

See [docs/twitch-api.md](docs/twitch-api.md) for the API, authorization flow, and operational details.
