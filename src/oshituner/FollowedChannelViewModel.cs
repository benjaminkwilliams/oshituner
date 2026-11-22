using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace oshituner;

internal sealed class FollowedChannelViewModel
{
    public ObservableCollection<FollowedChannelRow> FollowedChannels { get; } = [];
}

internal sealed record FollowedChannelRow(
    string Provider,
    string ChannelId,
    string DisplayName,
    string Login,
    DateTimeOffset? FollowedAt,
    bool Followed,
    bool? Online,
    bool? Subbed,
    string GameName,
    string StreamTitle,
    int ViewerCount)
{
    public bool IsLive => Online == true;
    public string Status => Online is true ? "LIVE" : Online is false ? "Offline" : "Unknown";
    public string SubscriptionStatus => Subbed is true ? "Subbed" : Subbed is false ? "—" : "Unknown";
}

[JsonConverter(typeof(JsonStringEnumConverter<GridGroup>))]
internal enum GridGroup { SubbedAndFollowed, Online, SubbedOnly, Followed }

[JsonConverter(typeof(JsonStringEnumConverter<SecondarySort>))]
internal enum SecondarySort { Viewers, ChannelName, MostRecentlyFollowed }

internal sealed record GridSort(GridGroup[] Groups, SecondarySort Secondary)
{
    public static GridSort Default() => new(
        [GridGroup.SubbedAndFollowed, GridGroup.Online, GridGroup.SubbedOnly, GridGroup.Followed],
        SecondarySort.Viewers);

    public bool IsValid() => Groups is { Length: 4 } &&
        Groups.All(group => Enum.IsDefined(group)) && Groups.Distinct().Count() == 4 && Enum.IsDefined(Secondary);

    public FollowedChannelRow[] Apply(IEnumerable<FollowedChannelRow> channels)
    {
        var grouped = channels.OrderBy(row => Array.FindIndex(Groups, group => Matches(group, row)) is var index
            && index >= 0 ? index : Groups.Length);

        var ordered = Secondary switch
        {
            SecondarySort.ChannelName => grouped.ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase),
            SecondarySort.MostRecentlyFollowed => grouped.ThenByDescending(row => row.FollowedAt),
            _ => grouped.ThenByDescending(row => row.ViewerCount)
        };
        return ordered.ThenBy(row => row.Login, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Provider, StringComparer.Ordinal).ThenBy(row => row.ChannelId, StringComparer.Ordinal).ToArray();
    }

    private static bool Matches(GridGroup group, FollowedChannelRow row) => group switch
    {
        GridGroup.SubbedAndFollowed => row.Subbed == true && row.Followed,
        GridGroup.Online => row.Online == true,
        GridGroup.SubbedOnly => row.Subbed == true && !row.Followed,
        GridGroup.Followed => row.Followed,
        _ => false
    };
}
