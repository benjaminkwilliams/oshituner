using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace oshituner;

internal sealed class BrowserRegistration(InstallProfile profile)
{
    internal static readonly BrowserTarget[] Targets =
    [
        new("edge", "Microsoft Edge", @"Microsoft\Edge", "cagcflabpbciehhnjchjipdipmgplipl"),
        new("chrome", "Google Chrome", @"Google\Chrome", "hnomfkgbacjeiocjcgbnnoaaloleefjf")
    ];

    private const string SuspendedManifestValue = "OshiTunerUpgradeManifest";
    private bool _replacementPending;

    public string InstallKey => profile.Key;
    public string ExecutablePath => profile.ExecutablePath;

    public void RequestDesktopWake(BrowserTarget target)
    {
        if (IsPairedHere(target)) AutoLaunch.Wake(profile);
    }

    public BrowserPairing[] GetPairings() => Targets.Select(target =>
    {
        var stateNow = ReadRegistration(target);
        var registered = stateNow.Suspended ?? stateNow.Active;
        var state = registered is null ? "unpaired" : Owns(registered, target) ? "paired_here" : "paired_elsewhere";
        return new BrowserPairing(target.Id, target.DisplayName, state,
            registered is null ? null : Path.GetDirectoryName(registered) == profile.DirectoryPath
                ? profile.InstallFolder : ReadOwner(registered));
    }).ToArray();

    public void Pair(BrowserTarget target, bool requestReplacement = false, Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = ReadRegistration(target);
        EnsureUpgradeIsIdle(previous);
        if (previous.Active is { } existing && !Owns(existing, target))
        {
            if (!requestReplacement)
                throw new InvalidOperationException($"{target.DisplayName} is already paired with another copy of Oshi Tuner.");
            if (_replacementPending)
                throw new InvalidOperationException("A browser pairing confirmation is already open.");
            _replacementPending = true;
            try
            {
                if (!BrowserPairingApproval.Confirm(target.DisplayName, ReadOwner(existing), profile.InstallFolder, owner, cancellationToken))
                    throw new InvalidOperationException("The browser pairing change was not approved.");
            }
            finally { _replacementPending = false; }
        }

        using var gate = AcquireGate(target);
        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadRegistration(target);
        EnsureUpgradeIsIdle(current);
        if (!string.Equals(previous.Active, current.Active, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The browser pairing changed while the request was pending. Try again.");

        var executable = profile.ExecutablePath;
        if (!File.Exists(executable))
            throw new InvalidOperationException("The Oshi Tuner executable is missing from this installation.");
        var manifest = new
        {
            name = target.HostName,
            description = "Oshi Tuner browser connection",
            path = executable,
            type = "stdio",
            allowed_origins = new[] { target.Origin }
        };
        var path = ManifestPath(target);
        var staging = path + ".new";
        File.WriteAllText(staging, JsonSerializer.Serialize(manifest));
        File.Move(staging, path, overwrite: true);
        using var key = Registry.CurrentUser.CreateSubKey(target.RegistryPath);
        key.SetValue("", path, RegistryValueKind.String);
    }

    public bool Unpair(BrowserTarget target)
    {
        using var gate = AcquireGate(target);
        var registration = ReadRegistration(target);
        EnsureUpgradeIsIdle(registration);
        if (registration.Active is not { } current || !Owns(current, target)) return false;
        Registry.CurrentUser.DeleteSubKey(target.RegistryPath, throwOnMissingSubKey: false);
        return true;
    }

    public bool IsPairedHere(BrowserTarget target) =>
        ReadRegistration(target).Active is { } current && Owns(current, target);

    internal InstallProfile Profile => profile;
    internal const string WatchSignalRole = "watch";
    internal static string BrowserRole(BrowserTarget target) => $"browser-{target.Id}";

    public static BrowserTarget GetTarget(string id) =>
        Targets.FirstOrDefault(target => target.Id == id)
        ?? throw new ArgumentException("Choose edge or chrome.");

    private static FileStream AcquireGate(BrowserTarget target)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oshituner");
        return new FileStream(Path.Combine(root, $"browser-{target.Id}.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private sealed record Registration(string? Active, string? Suspended);

    private static Registration ReadRegistration(BrowserTarget target)
    {
        using var key = Registry.CurrentUser.OpenSubKey(target.RegistryPath);
        return new(key?.GetValue("") as string, key?.GetValue(SuspendedManifestValue) as string);
    }

    private static void EnsureUpgradeIsIdle(Registration registration)
    {
        if (registration.Suspended is not null)
            throw new InvalidOperationException("This browser pairing is paused for an Oshi Tuner upgrade. Try again after setup finishes.");
    }

    private bool Owns(string path, BrowserTarget target) =>
        string.Equals(path, ManifestPath(target), StringComparison.OrdinalIgnoreCase);

    private string ManifestPath(BrowserTarget target) => profile.PathFor(target.HostName + ".json");

    private static string ReadOwner(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return Path.GetDirectoryName(document.RootElement.GetProperty("path").GetString())
                ?? manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return manifest;
        }
    }
}

internal sealed record BrowserTarget(string Id, string DisplayName, string VendorKey, string ExtensionId)
{
    public string ExecutableName => Id == "edge" ? "msedge.exe" : "chrome.exe";

    public string? FindExecutable()
    {
        var key = $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{ExecutableName}";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var app = hive.OpenSubKey(key);
            if (app?.GetValue("") is string executable && File.Exists(executable))
                return executable;
        }

        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), VendorKey, "Application", ExecutableName))
            .FirstOrDefault(File.Exists);
    }

    public string HostName => $"com.oshituner.{Id}";
    public string Origin => $"chrome-extension://{ExtensionId}/";
    public string RegistryPath => $@"Software\{VendorKey}\NativeMessagingHosts\{HostName}";
}

internal sealed record BrowserPairing(string Browser, string DisplayName, string State, string? InstallPath);
