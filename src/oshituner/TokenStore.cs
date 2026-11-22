using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal sealed class TokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "oshituner",
        "tokens.dat");

    public bool HasSavedAuthorization => File.Exists(_path);

    public void Clear() => File.Delete(_path);

    public async Task<TwitchTokens?> ReadAsync()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path);
            var tokenBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TwitchTokens>(tokenBytes, JsonOptions)
                ?? throw new InvalidOperationException("The saved Twitch authorization is empty. Remove the saved token file and sign in again.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                $"Could not decrypt the saved Twitch authorization. Remove '{_path}' and sign in again.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The saved Twitch authorization is invalid. Remove '{_path}' and sign in again.",
                exception);
        }
    }

    public async Task SaveAsync(TwitchTokens tokens)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Could not determine the Twitch token storage directory.");
        Directory.CreateDirectory(directory);

        var tokenBytes = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOptions);
        var protectedBytes = ProtectedData.Protect(tokenBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, protectedBytes);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}

internal sealed record TwitchTokens(string AccessToken, string RefreshToken);

internal sealed record TwitchAccess(string AccessToken, string UserId);
