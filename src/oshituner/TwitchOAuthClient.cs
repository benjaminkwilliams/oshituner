using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

[SupportedOSPlatform("windows")]
internal sealed class TwitchOAuthClient(HttpClient httpClient, string clientId, TokenStore tokenStore)
{
    private const string RequiredScope = "user:read:follows";
    private static readonly Uri DeviceEndpoint = new("https://id.twitch.tv/oauth2/device");
    private static readonly Uri TokenEndpoint = new("https://id.twitch.tv/oauth2/token");
    private static readonly Uri ValidateEndpoint = new("https://id.twitch.tv/oauth2/validate");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TwitchAccess?> GetAccessAsync(Action<Uri, string>? showDeviceCode, CancellationToken cancellationToken)
    {
        var tokens = await tokenStore.ReadAsync();
        if (tokens is not null)
        {
            var identity = await ValidateAsync(tokens.AccessToken, cancellationToken);
            if (identity is not null)
            {
                EnsureAuthorization(identity);
                return new TwitchAccess(tokens.AccessToken, identity.UserId!);
            }

            var refreshedTokens = await TryRefreshAsync(tokens.RefreshToken, cancellationToken);
            if (refreshedTokens is not null)
            {
                await tokenStore.SaveAsync(refreshedTokens);
                var refreshedIdentity = await ValidateAsync(refreshedTokens.AccessToken, cancellationToken);
                if (refreshedIdentity is not null)
                {
                    EnsureAuthorization(refreshedIdentity);
                    return new TwitchAccess(refreshedTokens.AccessToken, refreshedIdentity.UserId!);
                }
            }
        }

        if (showDeviceCode is null)
        {
            return null;
        }

        tokens = await AuthorizeWithDeviceCodeAsync(showDeviceCode, cancellationToken);
        var newIdentity = await ValidateAsync(tokens.AccessToken, cancellationToken)
            ?? throw new InvalidOperationException("Twitch did not accept the newly issued access token.");
        EnsureAuthorization(newIdentity);
        await tokenStore.SaveAsync(tokens);
        return new TwitchAccess(tokens.AccessToken, newIdentity.UserId!);
    }

    private async Task<TwitchIdentity?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateEndpoint);
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        EnsureSuccess(response, "validate Twitch access token");
        return await response.Content.ReadFromJsonAsync<TwitchIdentity>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Twitch returned an empty token validation response.");
    }

    private void EnsureAuthorization(TwitchIdentity identity)
    {
        if (!string.Equals(identity.ClientId, clientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved Twitch token belongs to a different client ID. Disconnect and sign in again.");
        }

        if (string.IsNullOrWhiteSpace(identity.UserId))
        {
            throw new InvalidOperationException("Twitch returned an app token; a user access token is required to read your followed channels.");
        }

        if (identity.Scopes is null || !identity.Scopes.Contains(RequiredScope, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"The Twitch token is missing the required '{RequiredScope}' permission. Disconnect and authorize again.");
        }
    }

    private async Task<TwitchTokens?> TryRefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        }), cancellationToken);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        EnsureSuccess(response, "refresh Twitch access token");
        return await ReadTokensAsync(response);
    }

    private async Task<TwitchTokens> AuthorizeWithDeviceCodeAsync(Action<Uri, string> showDeviceCode, CancellationToken cancellationToken)
    {
        using var deviceResponse = await httpClient.PostAsync(DeviceEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scopes"] = RequiredScope
        }), cancellationToken);
        EnsureSuccess(deviceResponse, "start Twitch device authorization");

        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceCodeResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Twitch returned an empty device authorization response.");
        if (string.IsNullOrWhiteSpace(device.DeviceCode)
            || string.IsNullOrWhiteSpace(device.UserCode)
            || !Uri.TryCreate(device.VerificationUri, UriKind.Absolute, out var activationUri)
            || activationUri.Scheme != Uri.UriSchemeHttps
            || activationUri.Host is not ("www.twitch.tv" or "twitch.tv")
            || device.ExpiresIn <= 0)
        {
            throw new InvalidOperationException("Twitch returned an invalid device authorization response.");
        }

        showDeviceCode(activationUri, device.UserCode);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        var intervalSeconds = Math.Max(device.Interval, 1);
        while (DateTimeOffset.UtcNow.AddSeconds(intervalSeconds) < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
            using var tokenResponse = await httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = RequiredScope,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }), cancellationToken);

            if (tokenResponse.IsSuccessStatusCode)
            {
                return await ReadTokensAsync(tokenResponse);
            }

            var error = await ReadErrorAsync(tokenResponse);
            if (error == "authorization_pending")
            {
                continue;
            }

            if (error == "slow_down")
            {
                intervalSeconds += 5;
                continue;
            }

            if (error == "access_denied")
            {
                throw new InvalidOperationException("Access was denied on Twitch. Connect again to retry.");
            }

            EnsureSuccess(tokenResponse, "complete Twitch device authorization");
        }

        throw new InvalidOperationException("Twitch device authorization expired. Connect again to retry.");
    }

    private async Task<TwitchTokens> ReadTokensAsync(HttpResponseMessage response)
    {
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Twitch returned an empty access token response.");
        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken)
            || tokenResponse.ExpiresIn <= 0
            || string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
        {
            throw new InvalidOperationException("Twitch returned incomplete token data. Check that the registered app uses the public client type and supports device code authorization.");
        }

        return new TwitchTokens(tokenResponse.AccessToken, tokenResponse.RefreshToken);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Could not {operation} (HTTP {(int)response.StatusCode}).",
                inner: null,
                response.StatusCode);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? body;
            }

            if (json.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            return body;
        }

        return body;
    }

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    private sealed record TwitchIdentity(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("scopes")] string[] Scopes,
        [property: JsonPropertyName("user_id")] string? UserId);
}
