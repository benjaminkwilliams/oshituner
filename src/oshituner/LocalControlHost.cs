using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace oshituner;

internal sealed class LocalControlHost(WebApplication app, string key, string token) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public int Port => new Uri(app.Urls.Single()).Port;
    public string CodexConfiguration =>
        $"[mcp_servers.oshi_tuner_{key[..12]}]\nurl = \"http://127.0.0.1:{Port}/mcp\"\nhttp_headers = {{ Authorization = \"Bearer {token}\" }}";

    public static async Task<LocalControlHost> StartAsync(TunerControl control, InstallProfile profile,
        int? port, CancellationToken cancellationToken)
    {
        var token = ReadOrCreateToken(profile.PathFor("control.dat"));
        if (port is > 0 and <= 65535)
        {
            try { return await StartAtAsync(control, profile.Key, token, port.Value, cancellationToken); }
            catch (IOException) { /* Another process took the previous port. */ }
        }
        return await StartAtAsync(control, profile.Key, token, 0, cancellationToken);
    }

    private static async Task<LocalControlHost> StartAtAsync(TunerControl control, string key,
        string token, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, port);
            server.Limits.MaxRequestBodySize = 65_536;
        });
        builder.Services.AddSingleton(control);
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "Oshi Tuner", Version = AppInformation.Current.Version };

        }).WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
        }).WithTools<McpTunerTools>();

        var app = builder.Build();
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        app.Use(async (context, next) =>
        {
            if (context.Request.Host.Host != "127.0.0.1" || context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var offered = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(expected, offered))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context);
        });
        app.MapPost("/rpc", (Delegate)((HttpContext http) => DispatchAsync(http, control)));
        app.MapMcp("/mcp");
        try
        {
            await app.StartAsync(cancellationToken);
            return new(app, key, token);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static string ReadOrCreateToken(string path)
    {
        if (File.Exists(path))
            return Convert.ToHexString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        var token = RandomNumberGenerator.GetBytes(32);
        var staging = path + ".new";
        File.WriteAllBytes(staging, ProtectedData.Protect(token, null, DataProtectionScope.CurrentUser));
        File.Move(staging, path);
        return Convert.ToHexString(token);
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
                    "import_theme_image" => await control.ImportThemeImageAsync(Required<string>(args, "source"), http.RequestAborted),
                    "watch_channel" => await control.WatchOnUiAsync(Required<string>(args, "channelId"),
                        Required<string>(args, "browser"), http.RequestAborted),
                    _ => await control.OnUiAsync(() => Execute(control, method.GetString()!, args), http.RequestAborted)
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

    private static object Execute(TunerControl control, string method, JsonElement args)
    {
        string Provider() => Optional<string?>(args, "provider") ?? "twitch";

        switch (method)
        {
            case "get_state": return control.GetState();
            case "get_oshi": return control.GetOshi();
            case "set_oshi": return control.SetOshi(Required<OshiSettings>(args, "settings"));
            case "set_oshi_running": return control.SetOshiRunning(Required<bool>(args, "running"));
            case "add_oshi_channel": return control.AddOshiChannel(new(Provider(), Required<string>(args, "channelId")),
                Optional<string?>(args, "rankId"));
            case "get_channels": return control.GetChannels(Optional<bool?>(args, "online"), Optional<bool?>(args, "subbed"));
            case "get_browser_pairings": return control.GetBrowserPairings();
            case "set_browser_pairing": return control.SetBrowserPairing(Required<string>(args, "browser"),
                Required<bool>(args, "paired"), Optional<bool?>(args, "replace") ?? false);
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
        finally { await app.DisposeAsync(); }
    }
}
