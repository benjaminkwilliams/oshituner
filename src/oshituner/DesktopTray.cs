using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace oshituner;

internal sealed class DesktopTray : IDisposable
{
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.NotifyIcon _icon;

    public DesktopTray(Dispatcher dispatcher, Action open, Action exit)
    {
        _menu.Items.Add("Open Oshi Tuner", null, (_, _) => dispatcher.InvokeAsync(open));
        _menu.Items.Add("Exit", null, (_, _) => dispatcher.InvokeAsync(exit));
        _icon = new() { Icon = SystemIcons.Application, Text = "Oshi Tuner", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => dispatcher.InvokeAsync(open);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private const string ExitRole = "exit";
    private const byte ExitRequest = 1;

    public static async Task ObserveExitAsync(InstallProfile profile, Action exit, Action<string> diagnostic, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var endpoint = new PipeRendezvous(profile, ExitRole);
                using var pipe = endpoint.Bind();
                await pipe.WaitForConnectionAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var request = new byte[1];
                if (await pipe.ReadAsync(request, deadline.Token) != 1 || request[0] != ExitRequest) continue;
                await pipe.WriteAsync(request, deadline.Token);
                if (!token.IsCancellationRequested) exit();
                return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostic($"Shutdown IPC unavailable ({exception.GetType().Name}); retrying.");
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public static async Task<bool> RequestExitAsync(InstallProfile profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var pipe = await PipeRendezvous.ConnectAsync(profile, ExitRole, 700, timeout.Token);
            if (pipe is null || PipePeer.VerifiedServerId(pipe, profile.ExecutablePath) is not { } processId) return false;
            using var desktop = Process.GetProcessById(checked((int)processId));
            _ = desktop.SafeHandle;
            await pipe.WriteAsync(new byte[] { ExitRequest }, timeout.Token);
            var confirmation = new byte[1];
            await pipe.ReadExactlyAsync(confirmation, timeout.Token);
            if (confirmation[0] != ExitRequest) return false;
            await desktop.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException
            or OperationCanceledException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
