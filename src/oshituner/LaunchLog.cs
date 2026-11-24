using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace oshituner;

internal sealed record LaunchLogEntry(DateTimeOffset Timestamp, string Category, string Message)
{
    [JsonIgnore]
    public string Text => $"{Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}  [{Category}]  {Message}";
}

internal sealed class LaunchLog
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff'Z'";
    private const int TimestampLength = 24;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock;
    private DateTimeOffset _nextExpiry = DateTimeOffset.MaxValue;
    private bool _canWrite = true;

    public LaunchLog(InstallProfile profile, TimeProvider? clock = null)
    {
        FilePath = profile.PathFor("oshi.jsonl");
        _clock = clock ?? TimeProvider.System;
        TryWrite(() => Load(profile.PathFor("oshi.log")));
    }

    public string FilePath { get; }
    public ObservableCollection<LaunchLogEntry> Entries { get; } = [];

    public void Write(string category, string message)
    {
        Prune();
        var entry = new LaunchLogEntry(_clock.GetUtcNow(), category, message.Replace('\r', ' ').Replace('\n', ' '));
        Add(entry);
        TryWrite(() => File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine));
    }

    public void Prune()
    {
        var now = _clock.GetUtcNow();
        if (now < _nextExpiry) return;
        _nextExpiry = DateTimeOffset.MaxValue;
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var expiry = Entries[i].Timestamp + Retention;
            if (expiry <= now) Entries.RemoveAt(i);
            else if (expiry < _nextExpiry) _nextExpiry = expiry;
        }
        TryWrite(Rewrite);
    }

    public string[] Read()
    {
        Prune();
        return Entries.Select(entry => entry.Text).ToArray();
    }

    private void Load(string legacyPath)
    {
        var hasLegacy = File.Exists(legacyPath);
        var migrate = !File.Exists(FilePath) && hasLegacy;
        if (!migrate && !File.Exists(FilePath)) return;

        var now = _clock.GetUtcNow();
        var cutoff = now - Retention;
        var removed = false;
        foreach (var line in File.ReadLines(migrate ? legacyPath : FilePath))
        {
            var entry = migrate ? ReadLegacy(line) : ReadJson(line);
            if (entry is not null && entry.Timestamp > cutoff && entry.Timestamp <= now)
                Add(entry with { Timestamp = entry.Timestamp.ToUniversalTime() });
            else
                removed = true;
        }
        if (migrate || removed) Rewrite();
        if (hasLegacy) File.Delete(legacyPath);
    }

    private static LaunchLogEntry? ReadJson(string line)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<LaunchLogEntry>(line, Json);
            return entry is { Category: { Length: > 0 }, Message: not null } ? entry : null;
        }
        catch (JsonException) { return null; }
    }

    private static LaunchLogEntry? ReadLegacy(string line)
    {
        if (line.Length <= TimestampLength + 3 || !DateTimeOffset.TryParseExact(line.AsSpan(0, TimestampLength),
            TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            || !line.AsSpan(TimestampLength).StartsWith("  [", StringComparison.Ordinal))
            return null;
        var categoryStart = TimestampLength + 3;
        var end = line.IndexOf("]  ", categoryStart, StringComparison.Ordinal);
        return end > categoryStart ? new(time, line[categoryStart..end], line[(end + 3)..]) : null;
    }

    private void Add(LaunchLogEntry entry)
    {
        Entries.Add(entry);
        var expiry = entry.Timestamp + Retention;
        if (expiry < _nextExpiry) _nextExpiry = expiry;
    }

    private void Rewrite()
    {
        var staging = FilePath + ".new";
        File.WriteAllLines(staging, Entries.Select(entry => JsonSerializer.Serialize(entry, Json)));
        File.Move(staging, FilePath, overwrite: true);
    }

    private void TryWrite(Action action)
    {
        if (!_canWrite) return;
        try { action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _canWrite = false;
            Add(new(_clock.GetUtcNow(), "Log", "Disk logging is unavailable. This session is being kept in memory."));
        }
    }

}
