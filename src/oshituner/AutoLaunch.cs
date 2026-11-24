using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace oshituner;

internal static class AutoLaunch
{
    public const string Argument = "--auto-launch";

    public static void Wake(InstallProfile profile)
    {
        TryWake(profile, DateTimeOffset.UtcNow, () =>
        {
            var start = new ProcessStartInfo(Path.Combine(profile.InstallFolder, "oshituner.exe"));
            start.ArgumentList.Add(Argument);
            using var app = Process.Start(start);
            return app is not null;
        });
    }

    internal static bool TryWake(InstallProfile profile, DateTimeOffset now, Func<bool> start)
    {
        try
        {
            using var gate = Gate(profile);
            if (new SettingsStore(profile).Read().Oshi is not { AutoLaunch: true } settings) return false;
            var stamp = profile.PathFor("auto-launch.dat");
            if (File.Exists(stamp) && DateTimeOffset.TryParse(File.ReadAllText(stamp), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var last) && now < last.AddHours(settings.AutoLaunchCooldownHours))
                return false;

            // The desktop and both browser helpers share the install profile. A busy lock is a running desktop.
            profile.Lock().Dispose();
            if (!start()) return false;
            WriteStamp(stamp, now);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    public static void RecordExit(InstallProfile profile)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var gate = Gate(profile);
                WriteStamp(profile.PathFor("auto-launch.dat"), DateTimeOffset.UtcNow);
                return;
            }
            catch (IOException) when (attempt < 3) { Thread.Sleep(25); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        }
    }

    private static FileStream Gate(InstallProfile profile) =>
        new(profile.PathFor("auto-launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static void WriteStamp(string path, DateTimeOffset now)
    {
        var staging = path + ".new";
        File.WriteAllText(staging, now.ToString("O", CultureInfo.InvariantCulture));
        File.Move(staging, path, overwrite: true);
    }
}
