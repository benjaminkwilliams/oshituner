---
name: twitch-api
description: Use when implementing or changing this project's Twitch OAuth, Helix API requests, token handling, or live-follow listing.
---

# Twitch API work in this project

Before changing Twitch integration code, read [the local Twitch API guide](../../../docs/twitch-api.md) and check the linked official Twitch documentation for current endpoint and OAuth requirements.

- Use Twitch Helix via `HttpClient`; preserve the existing typed request/response pattern and explicit HTTP error messages.
- This Windows WPF app uses the public-client OAuth device code grant. Keep the flow free of a client secret.
- Request only the OAuth scopes needed by the endpoint. The followed-live-streams feature requires a user token with `user:read:follows`.
- Validate OAuth tokens on startup and while a session remains active; check the token client ID, user ID, and required scope before using it.
- Preserve Windows DPAPI protection for persisted tokens. Persist a newly rotated refresh token immediately after refreshing.
- Follow Helix pagination cursors and preserve Twitch's API constraints in user-facing behavior.
- Do not log, commit, or expose access tokens, refresh tokens, or other credentials.
