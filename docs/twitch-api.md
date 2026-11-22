# Twitch API integration

This project uses Twitch's Helix API directly with .NET `HttpClient`; it does not depend on a third-party Twitch SDK.

## Show followed channels and live status

The app calls `GET /helix/channels/followed` for the full follow list and `GET /helix/streams/followed` for live status:

```http
GET https://api.twitch.tv/helix/streams/followed?user_id={authenticated-user-id}&first=100
Authorization: Bearer {user-access-token}
Client-Id: {registered-client-id}
```

The endpoint returns live broadcasts for the authenticated user's follows, ordered by viewer count. It requires a **user access token** with the `user:read:follows` scope. The `user_id` query value must match the user ID in that token. The app obtains this ID from Twitch's token validation response and follows pagination cursors until all results are read. Twitch limits this endpoint to streams from the first 2,000 channels the user followed.

The app matches live streams to followed channels by broadcaster ID. The channel grid shows all fetched follows, with live channels first. Channels beyond Twitch's live-status limit can show **Unknown**.

## Authorization and token handling

1. Register a separate Twitch application for this project in the [Twitch Developer Console](https://dev.twitch.tv/console). Use the **public** client type for a native Windows app and enable the device code grant. A client secret must not be embedded in a desktop app.
2. Enter the application's client ID in the window, or set `TWITCH_CLIENT_ID` before launch to prefill it and restore a saved session. Client IDs are public identifiers.
3. The app starts the [OAuth device code flow](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/), requesting only `user:read:follows`. The user approves access through Twitch's activation page.
4. It calls Twitch's [`/oauth2/validate`](https://dev.twitch.tv/docs/authentication/validate-tokens/) when starting and verifies the token's client ID, user ID, and required scope. Twitch requires third-party apps maintaining OAuth sessions to validate on startup and at least hourly while running.
5. Access and refresh tokens are stored encrypted with Windows DPAPI for the current Windows user under `%LOCALAPPDATA%\oshituner\tokens.dat`. Public-client refresh tokens expire after 30 days of inactivity, and Twitch may rotate refresh tokens. The app saves the latest refresh token after each refresh; if the grant is no longer valid, it asks the user to authorize again.

The app does not request or store a client secret. Do not log, commit, or share access or refresh tokens.

## Official documentation

- [Twitch API reference: Get Followed Streams](https://dev.twitch.tv/docs/api/reference/streams/#get-followed-streams)
- [Twitch API reference: Get Followed Channels](https://dev.twitch.tv/docs/api/reference/channels/#get-followed-channels)
- [Twitch authentication: getting OAuth access tokens](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/)
- [Twitch authentication: refreshing access tokens](https://dev.twitch.tv/docs/authentication/refresh-tokens/)
- [Twitch authentication: validating tokens](https://dev.twitch.tv/docs/authentication/validate-tokens/)
- [Twitch authentication: registering an app](https://dev.twitch.tv/docs/authentication/register-app/)
- [Twitch OAuth scopes](https://dev.twitch.tv/docs/authentication/scopes/)

## Local implementation map

- `src/oshituner/TwitchOAuthClient.cs` implements device authorization, refresh, validation, and required-scope checks.
- `src/oshituner/TwitchApiClient.cs` reads all pages from both Helix endpoints.
- `src/oshituner/TokenStore.cs` protects the serialized tokens with DPAPI.
