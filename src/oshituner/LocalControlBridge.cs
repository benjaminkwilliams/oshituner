using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Reflection;
using Microsoft.AspNetCore.Connections;
using ModelContextProtocol.Server;

namespace oshituner;

internal sealed class LocalMcpConnection(IServiceProvider services) : ConnectionHandler
{
    private readonly McpServerTool[] _tools = typeof(McpTunerTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.IsDefined(typeof(McpServerToolAttribute)))
        .Select(method => McpServerTool.Create(method, target: null, options: new() { Services = services })).ToArray();

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in _tools) tools.Add(tool);
        var transport = new StreamServerTransport(connection.Transport.Input.AsStream(),
            connection.Transport.Output.AsStream(), "Oshi Tuner");
        await using var server = McpServer.Create(transport, new()
        {
            ServerInfo = new() { Name = "Oshi Tuner", Version = AppInformation.Current.Version },
            ToolCollection = tools
        }, serviceProvider: services);
        await server.RunAsync(connection.ConnectionClosed).ConfigureAwait(false);
    }
}

internal static class LocalControlBridge
{
    internal static async Task<int> RunAsync(InstallProfile profile, bool mcp)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            if (mcp)
            {
                await using var pipe = await ConnectAsync(profile, mcp, CancellationToken.None).ConfigureAwait(false);
                using var shutdown = new CancellationTokenSource();
                var send = input.CopyToAsync(pipe, shutdown.Token);
                var receive = pipe.CopyToAsync(output, shutdown.Token);
                await await Task.WhenAny(send, receive).ConfigureAwait(false);
                await shutdown.CancelAsync().ConfigureAwait(false);
                return 0;
            }

            var bytes = new byte[LocalControlHost.MaxRpcBytes + 1];
            var read = await input.ReadAtLeastAsync(bytes, bytes.Length, false).ConfigureAwait(false);
            if (read == bytes.Length) throw new IOException("The JSON-RPC request exceeds the local size limit.");
            using var http = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (_, token) => await ConnectAsync(profile, mcp: false, token).ConfigureAwait(false)
            });
            using var content = new ByteArrayContent(bytes, 0, read);
            using var response = await http.PostAsync("http://localhost/rpc", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(output).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException
            or HttpRequestException or OperationCanceledException)
        {
            using var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            await error.WriteLineAsync("Could not reach this Oshi Tuner instance. Open it and check that the local API is available.");
            return 1;
        }
    }

    private static async ValueTask<NamedPipeClientStream> ConnectAsync(InstallProfile profile, bool mcp, CancellationToken token) =>
        await PipeRendezvous.ConnectAsync(profile, LocalControlHost.ControlRole, 1500, token,
            name => LocalControlHost.PipeName(name, mcp)).ConfigureAwait(false)
            ?? throw new IOException("The local API listener is unavailable.");
}
