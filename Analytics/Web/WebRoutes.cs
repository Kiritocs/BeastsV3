using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BeastsV3.Shared;

namespace BeastsV3.Analytics.Web;

// Routes dashboard HTTP requests to WebHost and writes the responses.
public static class WebRoutes
{
    // 1x1 transparent PNG served when the beast icon file is missing.
    private static readonly byte[] DefaultBeastIconPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9h0AAAAASUVORK5CYII=");

    // Dispatches one request and writes its response.
    public static async Task HandleAsync(HttpListenerContext context, WebHost host)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";

        try
        {
            AddCorsHeaders(response);

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            if (Match(path, "/") || Match(path, "/index.html"))
            {
                await WriteHtmlAsync(response, Dashboard.Page);
                return;
            }

            if (Match(path, "/beast-icon.png") && request.HttpMethod == "GET")
            {
                await WritePngAsync(response, LoadBeastIcon());
                return;
            }

            if (Match(path, "/api/health") && request.HttpMethod == "GET")
            {
                await WriteJsonAsync(response, new { ok = true, serverTimeUtc = DateTime.UtcNow });
                return;
            }

            if (Match(path, "/api/session/current") && request.HttpMethod == "GET")
            {
                await WriteJsonAsync(response, host.LatestSnapshot);
                return;
            }

            if (Match(path, "/api/session/maps") && request.HttpMethod == "GET")
            {
                var limit = ParseInt(request.QueryString["limit"], 200, 1, 1000);
                var offset = ParseInt(request.QueryString["offset"], 0, 0, 100000);
                await WriteJsonAsync(response, host.BuildMapList(offset, limit));
                return;
            }

            if (Match(path, "/api/session/saves") && request.HttpMethod == "GET")
            {
                await WriteJsonAsync(response, host.ListSavedSessions());
                return;
            }

            if (Match(path, "/api/session/saves") && request.HttpMethod == "POST")
            {
                var req = await ReadBodyAsync<CreateSessionSaveRequest>(request) ?? new CreateSessionSaveRequest();
                await WriteActionAsync(response, host.CreateSave(req));
                return;
            }

            if (Match(path, "/api/session/compare") && request.HttpMethod == "POST")
            {
                var req = await ReadBodyAsync<CompareSessionsRequest>(request) ?? new CompareSessionsRequest();
                var result = host.CompareSaves(req);
                if (!result.Success)
                {
                    await WriteErrorAsync(response, CodeToStatus(result.Code), result.Code, result.Message);
                    return;
                }
                await WriteJsonAsync(response, result);
                return;
            }

            // /api/session/saves/{saveId}[/{action}]
            var segments = SplitPath(path);
            if (segments.Length >= 4 &&
                MatchSegment(segments[0], "api") &&
                MatchSegment(segments[1], "session") &&
                MatchSegment(segments[2], "saves"))
            {
                var saveId = WebUtility.UrlDecode(segments[3] ?? string.Empty);

                if (segments.Length == 4 && request.HttpMethod == "GET")
                {
                    var detail = host.GetSavedSessionDetail(saveId);
                    if (detail?.Session == null)
                    {
                        await WriteErrorAsync(response, 404, "not_found", "Session not found.");
                        return;
                    }
                    await WriteJsonAsync(response, detail);
                    return;
                }

                if (segments.Length == 5 && request.HttpMethod == "POST" && MatchSegment(segments[4], "load"))
                {
                    await WriteActionAsync(response, host.LoadSave(saveId));
                    return;
                }

                if (segments.Length == 5 && request.HttpMethod == "POST" && MatchSegment(segments[4], "unload"))
                {
                    await WriteActionAsync(response, host.UnloadSave(saveId));
                    return;
                }

                if (segments.Length == 4 && request.HttpMethod == "DELETE")
                {
                    await WriteActionAsync(response, host.DeleteSave(saveId));
                    return;
                }
            }

            await WriteErrorAsync(response, 404, "not_found", "Not found.");
        }
        catch (Exception ex)
        {
            Log.Debug($"Web dashboard request error ({path}): {ex.GetType().Name}: {ex.Message}");
            try { await WriteErrorAsync(response, 500, "internal_error", "Internal server error."); }
            catch { /* client disconnected */ }
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
        }
    }

    // ---- helpers --------------------------------------------------------

    // Adds permissive CORS headers.
    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    }

    // Serialises a payload as JSON.
    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
    {
        response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, WebHost.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length));
    }

    // Writes an HTML body.
    private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
    {
        response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html ?? string.Empty);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length));
    }

    // Writes a PNG body.
    private static async Task WritePngAsync(HttpListenerResponse response, byte[] pngBytes)
    {
        response.ContentType = "image/png";
        response.ContentLength64 = pngBytes?.Length ?? 0;
        if (pngBytes is { Length: > 0 })
            await response.OutputStream.WriteAsync(pngBytes.AsMemory(0, pngBytes.Length));
    }

    // Writes an action result as JSON, or as an error response on failure.
    private static async Task WriteActionAsync(HttpListenerResponse response, ApiActionResponse result)
    {
        if (result?.Success == true) { await WriteJsonAsync(response, result); return; }
        var code = result?.Code ?? "request_failed";
        await WriteErrorAsync(response, CodeToStatus(code), code, result?.Message ?? "Request failed.", result?.Details);
    }

    // Writes an error body with the given status code.
    private static async Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string code, string message, object details = null)
    {
        response.StatusCode = statusCode;
        await WriteJsonAsync(response, new ApiErrorResponse
        {
            Code = code ?? "error",
            Message = message ?? "Request failed.",
            Details = details,
        });
    }

    // Deserialises the request body, or null when empty.
    private static async Task<T> ReadBodyAsync<T>(HttpListenerRequest request) where T : class
    {
        if (request?.InputStream == null || !request.HasEntityBody) return null;

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(content)) return null;

        return JsonSerializer.Deserialize<T>(content, WebHost.JsonOptions);
    }

    // Parses and clamps an integer query value.
    private static int ParseInt(string value, int fallback, int min, int max)
    {
        if (!int.TryParse(value, out var parsed)) return fallback;
        return Math.Clamp(parsed, min, max);
    }

    // Splits a URL path into non-empty segments.
    private static string[] SplitPath(string path) =>
        (path ?? string.Empty).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Match(string path, string expected) =>
        string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchSegment(string segment, string expected) =>
        string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);

    // Maps an API error code to an HTTP status code.
    private static int CodeToStatus(string code) => code switch
    {
        "invalid_id" or "invalid_request" => 400,
        "not_found" => 404,
        "duplicate" or "not_loaded" => 409,
        _ => 400,
    };

    // Reads beast.png from the plugin's Resources folder, falling back to a blank PNG.
    private static byte[] LoadBeastIcon()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "source", "BeastsV3", "Resources", "beast.png"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "beast.png"),
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
        }
        catch { /* falls through to the default icon */ }
        return DefaultBeastIconPng;
    }
}
