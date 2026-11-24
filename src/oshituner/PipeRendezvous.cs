using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace oshituner;

internal sealed class PipeRendezvous : IDisposable
{
    private const string Prefix = "oshituner-";
    private const int TokenLength = 64;
    private const int MaxRecordBytes = 1024;
    private readonly string _record;
    private readonly FileStream _lease;
    private bool _published;

    internal PipeRendezvous(InstallProfile profile, string role)
    {
        _record = RecordPath(profile, role);
        _lease = new FileStream(_record + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static async Task<PipeRendezvous> OpenAsync(InstallProfile profile, string role,
        Action<string> diagnostic, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new PipeRendezvous(profile, role); }
            catch (Exception exception) when (attempt < 2 && exception is (IOException or UnauthorizedAccessException))
            {
                diagnostic($"IPC discovery lease unavailable ({exception.GetType().Name}); retrying.");
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
    }

    internal static string NewName() => Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenLength / 2));

    private static string RecordPath(InstallProfile profile, string role) => profile.PathFor($"ipc-{role}.dat");

    internal static string? Read(InstallProfile profile, string role)
    {
        try
        {
            using var stream = new FileStream(RecordPath(profile, role), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaxRecordBytes) throw new IOException("Invalid IPC discovery record size.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var name = Encoding.ASCII.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            if (name.Length != Prefix.Length + TokenLength || !name.StartsWith(Prefix, StringComparison.Ordinal)
                || !name.AsSpan(Prefix.Length).ContainsOnlyLowerHex())
                throw new IOException("Invalid IPC discovery endpoint.");
            return name;
        }
        catch (FileNotFoundException) { return null; }
        catch (CryptographicException exception) { throw new IOException("Could not decrypt the IPC discovery record.", exception); }
    }

    internal void Publish(string name)
    {
        var staging = _record + ".new";
        try
        {
            File.WriteAllBytes(staging, ProtectedData.Protect(Encoding.ASCII.GetBytes(name), null, DataProtectionScope.CurrentUser));
            File.Move(staging, _record, overwrite: true);
            _published = true;
        }
        catch (CryptographicException exception) { throw new IOException("Could not protect the IPC discovery record.", exception); }
        finally { File.Delete(staging); }
    }

    internal NamedPipeServerStream Bind()
    {
        var name = NewName();
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            Publish(name);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    internal static async Task<NamedPipeClientStream?> ConnectAsync(InstallProfile profile, string role,
        int timeout, CancellationToken token, Func<string, string>? address = null)
    {
        string? previous = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var name = Read(profile, role);
            if (name is null)
            {
                if (attempt == 0) await Task.Delay(30, token).ConfigureAwait(false);
                continue;
            }
            if (name == previous) return null;
            previous = name;
            var pipe = new NamedPipeClientStream(".", address?.Invoke(name) ?? name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(timeout, token).ConfigureAwait(false);
                if (!PipePeer.IsServer(pipe, profile.ExecutablePath))
                    throw new UnauthorizedAccessException("The IPC server does not belong to this installation.");
                return pipe;
            }
            catch (TimeoutException) { pipe.Dispose(); }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (_published) File.Delete(_record);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _lease.Dispose(); }
    }
}

file static class EndpointName
{
    internal static bool ContainsOnlyLowerHex(this ReadOnlySpan<char> value)
    {
        foreach (var item in value)
            if (!char.IsAsciiHexDigitLower(item)) return false;
        return true;
    }
}
