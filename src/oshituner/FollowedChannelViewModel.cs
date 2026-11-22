using System.Collections.ObjectModel;

namespace oshituner;

internal sealed class FollowedChannelViewModel
{
    public ObservableCollection<FollowedChannelRow> FollowedChannels { get; } = [];
}

internal sealed record FollowedChannelRow(
    string DisplayName,
    string Login,
    DateTimeOffset FollowedAt,
    bool IsLive,
    string Status,
    string GameName,
    string StreamTitle,
    int ViewerCount);
