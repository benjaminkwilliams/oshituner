using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace oshituner;

internal sealed partial class BrowserDiscovery(BrowserRegistration registration)
{
    private const int Version = 1;
    private const int MaxBytes = 32_768;

    [GeneratedRegex("^[a-zA-Z0-9_]{1,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchLogin();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, SemaphoreSlim> _requests = BrowserRegistration.Targets
        .ToDictionary(target => target.Id, _ => new SemaphoreSlim(1));

    public async Task<BrowserGiftResult> GetGiftChannelsAsync(CancellationToken cancellationToken)
    {
        var responses = await Task.WhenAll(BrowserRegistration.Targets.Select(async target =>
            await QueryAsync(target, "gift_channels", null, cancellationToken) ?? new BrowserReply(Version, "", "unavailable", [])));
        var logins = responses.SelectMany(response => response.Logins).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var status = responses.Any(response => response.Status == "complete") ? "complete"
            : responses.Any(response => response.Status == "partial") ? "partial"
            : responses.Any(response => response.Status == "signed_out") ? "signed_out" : "unavailable";
        return new(status, logins);
    }

    public Task<BrowserWatchSnapshot[]> GetWatchTabsAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(BrowserRegistration.Targets.Select(async target =>
        {
            var reply = await QueryAsync(target, "watch_channels", null, cancellationToken);
            return reply is { Status: "complete", ActiveLogins: not null }
                ? new BrowserWatchSnapshot(target.Id, true, reply.Logins, reply.ActiveLogins)
                : new BrowserWatchSnapshot(target.Id, registration.IsPairedHere(target), [], []);
        }));

    public Task<OshiBrowserSnapshot[]> GetOshiTabsAsync(CancellationToken token) =>
        Task.WhenAll(BrowserRegistration.Targets.Select(target => GetOshiTabsAsync(target, token)));

    public async Task<OshiBrowserSnapshot> GetOshiTabsAsync(BrowserTarget target, CancellationToken token)
    {
        var reply = await QueryAsync(target, "oshi_tabs", null, token);
        var recoveries = reply?.RecoveryEvents is { Length: <= 40 } events
            ? events.Where(item => item is not null && Guid.TryParseExact(item.Id, "N", out _) &&
                item.Phase is "attempted" or "recovered" or "unconfirmed" &&
                item.Login is not null && TwitchLogin().IsMatch(item.Login)).ToArray() : [];
        return reply is { Status: "complete", ManagedTabs: { Length: <= 500 } tabs } &&
            tabs.Select(tab => tab?.TabId).Distinct().Count() == tabs.Length &&
            tabs.All(tab => tab is not null && tab.TabId >= 0 && Guid.TryParseExact(tab.Revision, "N", out _) &&
                tab.Login is not null && reply.Logins.Contains(tab.Login, StringComparer.OrdinalIgnoreCase))
            ? new(target.Id, "ready", reply.Logins, tabs, reply.WindowCount is >= 0 ? reply.WindowCount : null,
                reply.ExtensionVersion is { Length: <= 24 } version && System.Version.TryParse(version, out _) ? version : null, recoveries)
            : new(target.Id, !registration.IsPairedHere(target) ? "unpaired"
                : reply?.Status == "unavailable" ? "update extension" : "browser unavailable", [], []);
    }

    public async Task<bool> ApplyOshiMutationAsync(string browser, string? login, OshiBrowserMutation mutation,
        CancellationToken token) => await QueryAsync(BrowserRegistration.GetTarget(browser), "oshi_mutate",
            login, token, mutation) is { Status: "complete" };

    public async Task OpenWatchChannelAsync(string browser, string login, OshiChannelKey channel, int cap,
        bool existingOnly, CancellationToken cancellationToken)
    {
        if (!TwitchLogin().IsMatch(login)) throw new ArgumentException("Invalid Twitch channel login.");
        var target = BrowserRegistration.GetTarget(browser);
        var mutation = new OshiBrowserMutation(existingOnly ? "adopt" : "watch", Guid.NewGuid().ToString("N"),
            Channel: channel, TabCap: cap);
        var reply = await QueryAsync(target, "oshi_mutate", login, cancellationToken, mutation);
        cancellationToken.ThrowIfCancellationRequested();
        var startedBrowser = reply is null && !existingOnly && cap > 0 &&
            TryLaunchPairedBrowser(target, StartupAddress(login, mutation.Assignment));
        if (startedBrowser)
        {
            // The browser already owns the startup tab. A retry may adopt it but must not open another window.
            var adoption = mutation with { Action = "adopt_startup" };
            for (var attempt = 0; attempt < 10 && (reply is null || reply.Reason == "missing"); attempt++)
            {
                await Task.Delay(250, cancellationToken);
                reply = await QueryAsync(target, "oshi_mutate", login, cancellationToken, adoption);
            }
        }

        if (reply is { Status: "complete" }) return;
        throw new InvalidOperationException(reply?.Reason switch
        {
            "capacity" => $"{target.DisplayName} has reached the Oshi tab limit of {cap}. Close a managed stream or change the limit.",
            "missing" => $"The stream tab could not be found in the paired {target.DisplayName} profile.",
            "duplicate" => $"Close extra copies of this stream in {target.DisplayName} before managing it.",
            "shared_window" => "Move the stream into its own browser window before managing it.",
            "owned_elsewhere" => "That stream tab belongs to another Oshi Tuner installation.",
            _ when startedBrowser => $"Opened the stream in {target.DisplayName}, but Oshi Tuner could not manage its tab. Check the paired extension.",
            _ => $"Could not manage the stream. Reload the Oshi Tuner extension in the paired {target.DisplayName} profile."
        });
    }

    public bool TryLaunchForOshi(string browser, string login, string assignment) => TwitchLogin().IsMatch(login) &&
        TryLaunchPairedBrowser(BrowserRegistration.GetTarget(browser), StartupAddress(login, assignment));

    private static string StartupAddress(string login, string assignment) => $"https://www.twitch.tv/{login}#oshi-start={assignment}";

    private bool TryLaunchPairedBrowser(BrowserTarget target, string address) =>
        registration.IsPairedHere(target) && TryOpenBrowserWithoutWindows(target, address);

    private static bool TryOpenBrowserWithoutWindows(BrowserTarget target, string address)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target.ExecutableName));
        try
        {
            if (processes.Any(process => process.MainWindowHandle != 0) || target.FindExecutable() is not { } executable) return false;
            var start = new ProcessStartInfo(executable);
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(address);
            using var browser = Process.Start(start);
            return browser is not null;
        }
        catch (Win32Exception) { return false; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public async Task ObserveWatchChangesAsync(Action changed, Action<string> diagnostic, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var endpoint = new PipeRendezvous(registration.Profile, BrowserRegistration.WatchSignalRole);
                while (!token.IsCancellationRequested)
                {
                    using var signal = endpoint.Bind();
                    await signal.WaitForConnectionAsync(token);
                    if (!PipePeer.IsClient(signal, registration.ExecutablePath)) continue;
                    await signal.WriteAsync(new byte[] { 1 }, token);
                    changed();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostic($"Browser watch IPC unavailable ({exception.GetType().Name}); retrying.");
                await Task.Delay(1000, token);
            }
        }
    }

    private async Task<BrowserReply?> QueryAsync(BrowserTarget target, string method, string? login, CancellationToken cancellationToken,
        OshiBrowserMutation? mutation = null)
    {
        if (!registration.IsPairedHere(target)) return null;
        var gate = _requests[target.Id];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var pipe = await PipeRendezvous.ConnectAsync(registration.Profile, BrowserRegistration.BrowserRole(target),
                250, timeout.Token).ConfigureAwait(false);
            if (pipe is null) return null;
            var id = Guid.NewGuid().ToString("N");
            var scope = method.StartsWith("oshi_", StringComparison.Ordinal) ? registration.InstallKey : null;
            await WriteFrameAsync(pipe, new { version = Version, id, method, login, scope, mutation }, timeout.Token).ConfigureAwait(false);
            var frame = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (frame is null) return null;
            var reply = JsonSerializer.Deserialize<BrowserReply>(frame, Json);
            if (reply is null || reply.Version != Version || reply.Id != id || reply.Logins is not { Length: <= 500 }
                || reply.ActiveLogins is { Length: > 500 }
                || reply.Status is not ("complete" or "partial" or "signed_out" or "unavailable"))
                return null;
            if (reply.Status is "signed_out" or "unavailable") return reply with { Logins = [], ActiveLogins = [] };
            var valid = reply.Logins.Where(value => value is not null && TwitchLogin().IsMatch(value)).ToArray();
            var active = reply.ActiveLogins?.Intersect(valid, StringComparer.OrdinalIgnoreCase).ToArray();
            return reply with
            {
                Status = valid.Length == reply.Logins.Length ? reply.Status : "partial",
                Logins = valid,
                ActiveLogins = active
            };
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally { gate.Release(); }
    }

    public static async Task<int> RunNativeHostAsync(BrowserRegistration registration, BrowserTarget target)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var lifetime = new CancellationTokenSource();
        try
        {
            var watchSupported = 0;
            void EnableWatch(int capabilities) => Interlocked.Exchange(ref watchSupported, capabilities);
            var read = ReadBrowserReplyAsync(input, registration, target, EnableWatch, lifetime.Token);
            using var endpoint = await PipeRendezvous.OpenAsync(registration.Profile, BrowserRegistration.BrowserRole(target),
                message => Console.Error.WriteLine($"{target.DisplayName}: {message}"), lifetime.Token).ConfigureAwait(false);
            while (registration.IsPairedHere(target) && !read.IsCompleted)
            {
                NamedPipeServerStream listener;
                try { listener = endpoint.Bind(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"{target.DisplayName} native IPC listener unavailable ({exception.GetType().Name}); retrying.");
                    await Task.Delay(1000, lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                using var pipe = listener;
                var connection = pipe.WaitForConnectionAsync(lifetime.Token);
                if (!await WaitForRegisteredConnectionAsync(connection, read, registration, target, lifetime.Token).ConfigureAwait(false))
                    break;
                await connection.ConfigureAwait(false);
                if (!PipePeer.IsClient(pipe, registration.ExecutablePath)) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(4));
                byte[]? request;
                BrowserRequest command;
                try
                {
                    request = await ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false);
                    if (request is null || !registration.IsPairedHere(target)) continue;
                    command = ParseBrowserRequest(request, registration.InstallKey);
                }
                catch (Exception exception) when (exception is IOException or JsonException
                    || exception is OperationCanceledException && !lifetime.IsCancellationRequested) { continue; }

                byte[] response;
                if (RequiredCapability(command.Method) > Volatile.Read(ref watchSupported))
                {
                    response = JsonSerializer.SerializeToUtf8Bytes(new BrowserReply(Version, command.Id, "unavailable", []), Json);
                }
                else
                {
                    await WriteFrameBytesAsync(output, request, deadline.Token).ConfigureAwait(false);
                    var reply = await read.WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (reply is null) break;
                    var identity = JsonSerializer.Deserialize<BrowserReply>(reply, Json);
                    if (identity is null || identity.Id != command.Id || identity.Version != Version)
                        throw new IOException("Unexpected browser reply.");
                    response = reply;
                    read = ReadBrowserReplyAsync(input, registration, target, EnableWatch, lifetime.Token);
                }
                try { await WriteFrameBytesAsync(pipe, response, deadline.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException
                    || exception is OperationCanceledException && !lifetime.IsCancellationRequested) { }
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            return 1;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await SignalWatchChangeAsync(registration).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForRegisteredConnectionAsync(Task connection, Task read,
        BrowserRegistration registration, BrowserTarget target, CancellationToken token)
    {
        while (registration.IsPairedHere(target))
        {
            var refresh = Task.Delay(TimeSpan.FromSeconds(1), token);
            var completed = await Task.WhenAny(connection, read, refresh).ConfigureAwait(false);
            if (completed == read) return false;
            if (completed == connection) return true;
        }
        return false;
    }

    public static async Task WakeUnregisteredHostAsync(BrowserRegistration registration, BrowserTarget target)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using (await PipeRendezvous.ConnectAsync(registration.Profile, BrowserRegistration.BrowserRole(target),
                300, timeout.Token).ConfigureAwait(false)) { }
            await Task.Delay(250, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
    }

    private static BrowserRequest ParseBrowserRequest(byte[] frame, string installKey)
    {
        using var request = JsonDocument.Parse(frame);
        var root = request.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != Version
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(id.GetString(), "N", out _)
            || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            throw new JsonException("Invalid browser request.");
        var name = method.GetString()!;
        _ = RequiredCapability(name);
        if (name.StartsWith("oshi_", StringComparison.Ordinal) &&
            (!root.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String || scope.GetString() != installKey))
            throw new JsonException("Invalid browser request installation.");
        if (root.TryGetProperty("login", out var login) && login.ValueKind != JsonValueKind.Null &&
            (login.ValueKind != JsonValueKind.String || !TwitchLogin().IsMatch(login.GetString()!)))
            throw new JsonException("Invalid browser request channel.");
        return new(id.GetString()!, name);
    }

    private static int RequiredCapability(string method) => method switch
    {
        "gift_channels" => 0,
        "watch_channels" or "open_watch_channel" => 1,
        "oshi_tabs" or "oshi_mutate" => 2,
        _ => throw new JsonException("Unknown browser request.")
    };

    private sealed record BrowserRequest(string Id, string Method);

    private static async Task<byte[]?> ReadBrowserReplyAsync(Stream input, BrowserRegistration registration,
        BrowserTarget target, Action<int> enableWatch, CancellationToken token)
    {
        while (await ReadFrameAsync(input, token).ConfigureAwait(false) is { } frame)
        {
            using var message = JsonDocument.Parse(frame);
            if (message.RootElement.ValueKind == JsonValueKind.Object
                && message.RootElement.TryGetProperty("event", out var name)
                && name.ValueKind == JsonValueKind.String && name.GetString() == "watch_changed")
            {
                enableWatch(message.RootElement.TryGetProperty("managed", out var managed)
                    && managed.ValueKind == JsonValueKind.True ? 2 : 1);
                if (message.RootElement.TryGetProperty("wake", out var wake) && wake.ValueKind == JsonValueKind.True)
                    registration.RequestDesktopWake(target);
                else
                    await SignalWatchChangeAsync(registration).ConfigureAwait(false);
                continue;
            }
            return frame;
        }
        return null;
    }

    private static async Task SignalWatchChangeAsync(BrowserRegistration registration)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            using var signal = await PipeRendezvous.ConnectAsync(registration.Profile, BrowserRegistration.WatchSignalRole,
                100, timeout.Token).ConfigureAwait(false);
            if (signal is null) return;
            await signal.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException
            or OperationCanceledException) { }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[sizeof(int)];
        if (await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxBytes) throw new IOException("Invalid browser message size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static Task WriteFrameAsync<T>(Stream stream, T message, CancellationToken token) =>
        WriteFrameBytesAsync(stream, JsonSerializer.SerializeToUtf8Bytes(message, Json), token);

    private static async Task WriteFrameBytesAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private sealed record BrowserReply(int Version, string Id, string Status, string[] Logins, string[]? ActiveLogins = null,
        OshiBrowserTab[]? ManagedTabs = null, string? Reason = null, int? WindowCount = null, string? ExtensionVersion = null,
        BrowserRecoveryEvent[]? RecoveryEvents = null);
}

internal sealed record BrowserGiftResult(string Status, string[] Logins);
internal sealed record BrowserWatchSnapshot(string Browser, bool Available, string[] Logins, string[] ActiveLogins);
internal sealed record OshiBrowserTab(int TabId, string Login, string Revision, OshiChannelKey? Channel = null);
internal sealed record OshiBrowserSnapshot(string Browser, string Status, string[] Logins, OshiBrowserTab[] Tabs, int? WindowCount = null,
    string? ExtensionVersion = null, BrowserRecoveryEvent[]? RecoveryEvents = null);
internal sealed record BrowserRecoveryEvent(string Id, string Phase, string Login);
internal sealed record OshiBrowserMutation(string Action, string Assignment, int? TabId = null, string? Revision = null,
    string? ExpectedLogin = null, OshiChannelKey? Channel = null, int? TabCap = null, bool FocusOnOpen = false);
