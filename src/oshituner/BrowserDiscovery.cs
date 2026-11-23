using System.Buffers.Binary;
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

    public async Task<BrowserGiftResult> GetGiftChannelsAsync(CancellationToken cancellationToken)
    {
        var responses = await Task.WhenAll(BrowserRegistration.Targets.Select(target => QueryAsync(target, cancellationToken)));
        var logins = responses.SelectMany(response => response.Logins).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var status = responses.Any(response => response.Status == "complete") ? "complete"
            : responses.Any(response => response.Status == "partial") ? "partial"
            : responses.Any(response => response.Status == "signed_out") ? "signed_out" : "unavailable";
        return new(status, logins);
    }

    private async Task<BrowserGiftResult> QueryAsync(BrowserTarget target, CancellationToken cancellationToken)
    {
        if (!registration.IsPairedHere(target)) return new("unavailable", []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var pipe = new NamedPipeClientStream(".", registration.PipeName(target), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(250, timeout.Token).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            await WriteFrameAsync(pipe, new { version = Version, id, method = "gift_channels" }, timeout.Token).ConfigureAwait(false);
            var frame = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (frame is null) return new("unavailable", []);
            var reply = JsonSerializer.Deserialize<BrowserReply>(frame, Json);
            if (reply is null || reply.Version != Version || reply.Id != id || reply.Logins is not { Length: <= 500 }
                || reply.Status is not ("complete" or "partial" or "signed_out" or "unavailable"))
                return new("unavailable", []);
            if (reply.Status is "signed_out" or "unavailable") return new(reply.Status, []);
            var valid = reply.Logins.Where(login => login is not null && TwitchLogin().IsMatch(login)).ToArray();
            return new(valid.Length == reply.Logins.Length ? reply.Status : "partial", valid);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new("unavailable", []);
        }
    }

    public static async Task<int> RunNativeHostAsync(BrowserRegistration registration, BrowserTarget target)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var lifetime = new CancellationTokenSource();
        try
        {
            var read = ReadFrameAsync(input, lifetime.Token);
            while (registration.IsPairedHere(target))
            {
                using var pipe = new NamedPipeServerStream(registration.PipeName(target), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var connection = pipe.WaitForConnectionAsync(lifetime.Token);
                if (!await WaitForRegisteredConnectionAsync(connection, read, registration, target, lifetime.Token).ConfigureAwait(false))
                    break;
                await connection.ConfigureAwait(false);
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    var request = await ReadFrameAsync(pipe, requestTimeout.Token).ConfigureAwait(false);
                    if (request is null || !registration.IsPairedHere(target)) continue;
                    await WriteFrameBytesAsync(output, request, requestTimeout.Token).ConfigureAwait(false);
                    var response = await read.WaitAsync(requestTimeout.Token).ConfigureAwait(false);
                    if (response is null) break;
                    read = ReadFrameAsync(input, lifetime.Token);
                    await WriteFrameBytesAsync(pipe, response, requestTimeout.Token).ConfigureAwait(false);
                }
                catch (IOException) when (!read.IsCompleted) { }
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return 1;
        }
        finally { await lifetime.CancelAsync().ConfigureAwait(false); }
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
            using (var pipe = new NamedPipeClientStream(".", registration.PipeName(target), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                await pipe.ConnectAsync(300, timeout.Token).ConfigureAwait(false);
            await Task.Delay(250, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException) { }
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

    private sealed record BrowserReply(int Version, string Id, string Status, string[] Logins);
}

internal sealed record BrowserGiftResult(string Status, string[] Logins);
