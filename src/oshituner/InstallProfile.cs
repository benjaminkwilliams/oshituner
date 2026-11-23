using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace oshituner;

internal sealed class InstallProfile
{
    public string InstallFolder { get; }
    public string Key { get; }
    public string DirectoryPath { get; }
    public string LegacyTokenPath { get; }

    public InstallProfile() : this(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oshituner")) { }

    internal InstallProfile(string folder, string root)
    {
        InstallFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(InstallFolder.ToUpperInvariant()))).ToLowerInvariant();
        DirectoryPath = Path.Combine(root, "installs", Key);
        LegacyTokenPath = Path.Combine(root, "tokens.dat");
        Directory.CreateDirectory(DirectoryPath);
    }

    public FileStream Lock() => new(Path.Combine(DirectoryPath, "running.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public string PathFor(string name) => Path.Combine(DirectoryPath, name);
}

internal sealed class SettingsStore(InstallProfile profile)
{
    private readonly string _path = profile.PathFor("settings.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public TunerSettings Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<TunerSettings>(File.ReadAllText(_path), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(TunerSettings settings)
    {
        var staging = _path + ".new";
        File.WriteAllText(staging, JsonSerializer.Serialize(settings, Json));
        File.Move(staging, _path, overwrite: true);
    }
}

internal sealed record TunerSettings(string? TwitchClientId = null, GridSort? Sort = null, int? ControlPort = null,
    int? AutoRefreshMinutes = null, Dictionary<string, TwitchChannel[]>? RememberedTwitchGifts = null, WorkspaceState? Workspace = null, ThemeSettings? Theme = null);
