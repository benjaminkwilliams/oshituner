using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace oshituner;

internal sealed class LocalControlHost(WebApplication app, InstallProfile profile, PipeRendezvous endpoint) : IAsyncDisposable
{
    internal const int MaxRpcBytes = 65_536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public string CodexConfiguration =>
        $"[mcp_servers.oshi_tuner_{profile.Key[..12]}]\ncommand = {JsonSerializer.Serialize(profile.ExecutablePath)}\nargs = [\"--mcp\"]";

    internal const string ControlRole = "control";

    internal static string PipeName(string name, bool mcp) => name + (mcp ? "-mcp" : "-rpc");

    public static async Task<LocalControlHost> StartAsync(TunerControl control, InstallProfile profile,
        Action<string> diagnostic, CancellationToken cancellationToken)
    {
        // Migrating to Windows IPC invalidates the old network credential, including on rollback.
        File.Delete(profile.PathFor("control.dat"));
        File.Delete(profile.PathFor("control.dat.new"));

        var endpoint = await PipeRendezvous.OpenAsync(profile, ControlRole, diagnostic, cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await StartEndpointAsync(control, profile, endpoint, cancellationToken); }
                catch (Exception exception) when (attempt < 2 && exception is (IOException or UnauthorizedAccessException))
                {
                    diagnostic($"Control IPC listener unavailable ({exception.GetType().Name}); retrying.");
                    await Task.Delay(200, cancellationToken);
                }
            }
        }
        catch
        {
            endpoint.Dispose();
            throw;
        }
    }

    private static async Task<LocalControlHost> StartEndpointAsync(TunerControl control, InstallProfile profile,
        PipeRendezvous endpoint, CancellationToken cancellationToken)
    {
        var name = PipeRendezvous.NewName();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseNamedPipes(options => options.CurrentUserOnly = true);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Configure(new ConfigurationBuilder().Build());
            server.ListenNamedPipe(PipeName(name, mcp: false));
            server.ListenNamedPipe(PipeName(name, mcp: true), mcp => mcp.UseConnectionHandler<LocalMcpConnection>());
            server.Limits.MaxRequestBodySize = MaxRpcBytes;
        });
        builder.Services.AddSingleton(control);
        builder.Services.AddSingleton<LocalMcpConnection>();
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "Oshi Tuner", Version = AppInformation.Current.Version };
        }).WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
        }).WithTools<McpTunerTools>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalIpAddress is not null || context.Connection.RemoteIpAddress is not null
                || !string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.MapPost("/rpc", (Delegate)((HttpContext http) => DispatchAsync(http, control)));
        app.MapMcp("/mcp");
        try
        {
            await app.StartAsync(cancellationToken);
            endpoint.Publish(name);
            return new(app, profile, endpoint);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task<IResult> DispatchAsync(HttpContext http, TunerControl control)
    {
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted); }
        catch (JsonException) { return Error(null, -32700, "Invalid JSON."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var protocol)
                || protocol.ValueKind != JsonValueKind.String || protocol.GetString() != "2.0"
                || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                return Error(null, -32600, "Invalid JSON-RPC 2.0 request.");

            JsonElement? id = root.TryGetProperty("id", out var member) ? member.Clone() : null;
            if (id is { ValueKind: not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null) })
                return Error(null, -32600, "Invalid request ID.");
            var args = root.TryGetProperty("params", out var parameters) ? parameters : default;
            if (args.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                return id.HasValue ? Error(id, -32602, "Pass named parameters as an object.") : Results.NoContent();

            try
            {
                object result = method.GetString() switch
                {
                    "import_theme_image" => await control.ImportThemeImageAsync(Required<string>(args, "source"),
                        http.RequestAborted, Optional<bool?>(args, "allowPrivate") ?? false),
                    "watch_channel" => await control.WatchOnUiAsync(Required<string>(args, "channelId"),
                        Required<string>(args, "browser"), http.RequestAborted, Optional<bool?>(args, "existingOnly") ?? false),
                    _ => await control.OnUiAsync(() => Execute(control, method.GetString()!, args, http.RequestAborted), http.RequestAborted)
                };
                return id.HasValue ? Results.Json(new { jsonrpc = "2.0", id, result }, Json) : Results.NoContent();
            }
            catch (MissingMethodException) { return id.HasValue ? Error(id, -32601, "Unknown method.") : Results.NoContent(); }
            catch (Exception exception) when (exception is ArgumentException or JsonException or KeyNotFoundException)
            {
                return id.HasValue ? Error(id, -32602, "Invalid named parameters.") : Results.NoContent();
            }
            catch (InvalidDataException exception)
            {
                return id.HasValue ? Error(id, -32602, exception.Message) : Results.NoContent();
            }
            catch (InvalidOperationException exception)
            {
                return id.HasValue ? Error(id, -32000, exception.Message) : Results.NoContent();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return id.HasValue ? Error(id, -32000, "Could not update the local settings.") : Results.NoContent();
            }
        }
    }

    private static object Execute(TunerControl control, string method, JsonElement args, CancellationToken cancellationToken)
    {
        string Provider() => Optional<string?>(args, "provider") ?? "twitch";

        switch (method)
        {
            case "get_state": return control.GetState();
            case "get_oshi": return control.GetOshi();
            case "get_log": return control.GetLog();
            case "set_oshi": return control.SetOshi(Required<OshiSettings>(args, "settings"));
            case "set_oshi_running": return control.SetOshiRunning(Required<bool>(args, "running"));
            case "add_oshi_channel": return control.AddOshiChannel(new(Provider(), Required<string>(args, "channelId")),
                Optional<string?>(args, "rankId"));
            case "get_channels": return control.GetChannels(Optional<bool?>(args, "online"), Optional<bool?>(args, "subbed"));
            case "get_browser_pairings": return control.GetBrowserPairings();
            case "set_browser_pairing": return control.SetBrowserPairing(Required<string>(args, "browser"),
                Required<bool>(args, "paired"), Optional<bool?>(args, "replace") ?? false, cancellationToken);
            case "open_browser_integration": control.OpenBrowserIntegration(); break;
            case "close_browser_integration": control.CloseBrowserIntegration(); break;
            case "get_theme": return control.GetTheme();
            case "set_theme": return control.SetTheme(Required<ThemeSettings>(args, "theme"));
            case "reset_theme": return control.ResetTheme();
            case "open_theme": control.OpenTheme(); break;
            case "close_theme": control.CloseTheme(); break;
            case "get_workspace": return control.GetWorkspace();
            case "open_pane": control.OpenPane(Required<string>(args, "id")); return control.GetWorkspace();
            case "close_pane": control.ClosePane(Required<string>(args, "id")); return control.GetWorkspace();
            case "move_pane": control.MovePane(Required<string>(args, "id"), Optional<DockLocation?>(args, "dock")); return control.GetWorkspace();
            case "join_floating_group": control.JoinFloatingGroup(Required<string>(args, "id"), Required<string>(args, "peerId")); return control.GetWorkspace();
            case "redock_floating_group": control.RedockFloatingGroup(Required<string>(args, "id"), Required<DockLocation>(args, "dock")); return control.GetWorkspace();
            case "pin_pane": control.PinPane(Required<string>(args, "id"), Required<bool>(args, "pinned")); return control.GetWorkspace();
            case "get_auto_refresh": return control.GetAutoRefresh();
            case "set_auto_refresh": control.SetAutoRefresh(Required<int>(args, "minutes")); return control.GetAutoRefresh();
            case "get_sort": return control.GetSort();
            case "get_about": return AppInformation.Current;
            case "set_client_id": control.SetClientId(Provider(), Required<string>(args, "clientId")); break;
            case "open_client_id": control.OpenClientId(Provider()); break;
            case "close_client_id": control.CloseClientId(Provider()); break;
            case "connect": control.Start(Provider(), true); break;
            case "refresh": control.Start(Provider(), false); break;
            case "cancel": control.Cancel(Provider()); break;
            case "disconnect": control.Disconnect(Provider()); break;
            case "open_activation": control.OpenActivation(Provider()); break;
            case "open_channel": control.OpenChannel(Provider(), Required<string>(args, "channelId")); break;
            case "set_sort": control.SetSort(new(Required<GridGroup[]>(args, "groups"), Required<SecondarySort>(args, "secondary"))); break;
            case "reset_sort": control.ResetSort(); break;
            case "open_about": control.OpenAbout(); break;
            case "close_about": control.CloseAbout(); break;
            case "open_about_link": control.OpenAboutLink(Required<string>(args, "link")); break;
            default: throw new MissingMethodException();
        }
        return control.GetState();
    }

    private static T Required<T>(JsonElement args, string name)
    {
        if (!TryGetParameter(args, name, out var value) || value.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("A required parameter is missing.");
        return value.Deserialize<T>(Json)!;
    }

    private static T? Optional<T>(JsonElement args, string name) =>
        TryGetParameter(args, name, out var value) ? value.Deserialize<T>(Json) : default;

    private static bool TryGetParameter(JsonElement args, string name, out JsonElement value)
    {
        value = default;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out value);
    }

    private static IResult Error(JsonElement? id, int code, string message) =>
        Results.Json(new { jsonrpc = "2.0", id, error = new { code, message } }, Json);

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(timeout.Token); }
        finally
        {
            try { await app.DisposeAsync(); }
            finally { endpoint.Dispose(); }
        }
    }
}
