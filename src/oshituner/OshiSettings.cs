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

internal sealed record OshiRank(string Id, OshiEntry[] Channels);

internal sealed record OshiSettings(int Version, OshiRank[] Ranks, string DefaultBrowser, int EdgeCap, int ChromeCap)
{
    public const int MaxBrowserTabs = 50;

    public static OshiSettings Create() => new(1, [], "edge", 1, 0);

    public string BrowserFor(OshiEntry channel) => channel.Browser ?? DefaultBrowser;

    public int CapFor(string browser) => browser switch { "edge" => EdgeCap, "chrome" => ChromeCap, _ => 0 };

    private static bool IsBrowser(string? browser) => browser is "edge" or "chrome";

    public bool IsValid()
    {
        if (Version != 1 || Ranks is null || !IsBrowser(DefaultBrowser) ||
            EdgeCap is < 0 or > MaxBrowserTabs || ChromeCap is < 0 or > MaxBrowserTabs)
            return false;

        var groups = new HashSet<string>(StringComparer.Ordinal);
        var channels = new HashSet<OshiChannelKey>();
        return Ranks.All(rank => rank is not null && !string.IsNullOrWhiteSpace(rank.Id) && groups.Add(rank.Id)
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
