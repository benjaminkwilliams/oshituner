using System.Text.Json;
using System.Text.Json.Serialization;

namespace oshituner;

internal sealed record OshiChannelKey(string Provider, string Id)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Id);
}

internal sealed record OshiEntry(OshiChannelKey Channel, string? DisplayName = null,
    string? Browser = null)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Channel.Id : DisplayName;
}

internal sealed record OshiRank(string Id, OshiEntry[] Channels, string? Name = null)
{
    public const int MaxNameLength = 20;

    public string NumberedName(int index) => string.IsNullOrWhiteSpace(Name)
        ? $"Rank {index + 1}" : $"Rank {index + 1} · {Name}";

    public string PositionLabel(int rankIndex, int memberIndex) => Channels.Length == 1
        ? $"{rankIndex + 1}" : $"{rankIndex + 1}.{memberIndex + 1}";

    public string MemberLabel(int rankIndex, int memberIndex)
    {
        var position = PositionLabel(rankIndex, memberIndex);
        return string.IsNullOrWhiteSpace(Name) ? $"Rank {position}" : $"Rank {position} · {Name}";
    }
}

internal sealed record OshiChannelTiming(OshiChannelKey Channel, int? RotationMinutes = null, int? FallbackMinutes = null);

internal sealed record OshiSettings(int Version, OshiRank[] Ranks, string DefaultBrowser, int EdgeCap, int ChromeCap,
    OshiChannelTiming[]? Timings = null, bool AutoLaunch = false,
    int AutoLaunchCooldownHours = OshiSettings.DefaultCooldownHours)
{
    public const int MaxBrowserTabs = 50;
    public const int DefaultWatchMinutes = 30;
    public const int DefaultFallbackMinutes = 10;
    public const int MinTimingMinutes = 5;
    public const int MaxTimingMinutes = 60;
    public const int DefaultCooldownHours = 1;
    public const int MaxCooldownHours = 5;

    public static bool ValidTiming(int? minutes) => minutes is null or >= MinTimingMinutes and <= MaxTimingMinutes;

    public OshiChannelTiming TimingFor(OshiChannelKey channel) =>
        Timings?.FirstOrDefault(rule => rule.Channel == channel) ?? new(channel);

    public OshiSettings WithTiming(OshiChannelTiming timing)
    {
        var timings = (Timings ?? []).Where(rule => rule.Channel != timing.Channel);
        if (timing.RotationMinutes.HasValue || timing.FallbackMinutes.HasValue) timings = timings.Append(timing);
        return this with { Timings = timings.ToArray() };
    }

    public static OshiSettings Create() => new(1, [], "edge", 1, 0);

    public string? RankLabelFor(OshiChannelKey? channel)
    {
        if (channel is null) return null;
        for (var rankIndex = 0; rankIndex < Ranks.Length; rankIndex++)
        {
            var rank = Ranks[rankIndex];
            var memberIndex = Array.FindIndex(rank.Channels, entry => entry.Channel == channel);
            if (memberIndex >= 0) return rank.MemberLabel(rankIndex, memberIndex);
        }
        return null;
    }

    public string BrowserFor(OshiEntry channel) => channel.Browser ?? DefaultBrowser;

    public int CapFor(string browser) => browser switch { "edge" => EdgeCap, "chrome" => ChromeCap, _ => 0 };

    private static bool IsBrowser(string? browser) => browser is "edge" or "chrome";

    public bool IsValid()
    {
        if (Version != 1 || Ranks is null || !IsBrowser(DefaultBrowser)
            || AutoLaunchCooldownHours is < DefaultCooldownHours or > MaxCooldownHours ||
            EdgeCap is < 0 or > MaxBrowserTabs || ChromeCap is < 0 or > MaxBrowserTabs)
            return false;

        var timedChannels = new HashSet<OshiChannelKey>();
        if (Timings is not null && !Timings.All(timing => timing?.Channel is { } key && key.IsValid()
            && timedChannels.Add(key) && ValidTiming(timing.RotationMinutes) && ValidTiming(timing.FallbackMinutes)))
            return false;

        var groups = new HashSet<string>(StringComparer.Ordinal);
        var channels = new HashSet<OshiChannelKey>();
        return Ranks.All(rank => rank is not null && !string.IsNullOrWhiteSpace(rank.Id) && groups.Add(rank.Id)
            && (rank.Name is null || rank.Name.Length <= OshiRank.MaxNameLength)
            && rank.Channels is { Length: > 0 } && rank.Channels.All(entry =>
                entry?.Channel is { } key && key.IsValid() && channels.Add(key)
                && (entry.Browser is null || IsBrowser(entry.Browser))));
    }
}

internal sealed class OshiSettingsConverter : JsonConverter<OshiSettings>
{
    public override OshiSettings? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var section = JsonDocument.ParseValue(ref reader);
        try
        {
            var settings = section.RootElement.Deserialize<OshiSettings>(options);
            return settings is not null && settings.IsValid() ? settings : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, OshiSettings value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
