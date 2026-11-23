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

    private static string ExitPipe(InstallProfile profile) => "oshituner-exit-" + profile.Key;

    public static async Task ObserveExitAsync(InstallProfile profile, Action exit, CancellationToken token)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(ExitPipe(profile), PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(token);
            // Send the desktop PID so callers can wait until Windows releases the executable.
            await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), token);
            if (!token.IsCancellationRequested) exit();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static async Task<bool> RequestExitAsync(InstallProfile profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            using var pipe = new NamedPipeClientStream(".", ExitPipe(profile), PipeDirection.In,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(700, timeout.Token);
            var identity = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(identity, timeout.Token);
            try
            {
                using var desktop = Process.GetProcessById(BitConverter.ToInt32(identity));
                await desktop.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { /* The desktop process has already exited. */ }
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }
}
