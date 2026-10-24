using System.Net.Http.Headers;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
// REQ-WEB-001: frontend development stays local until Portunus provides remote authentication.
builder.WebHost.UseUrls("http://127.0.0.1:5280");
builder.Services.AddHttpClient("portunus", client => client.Timeout = TimeSpan.FromMinutes(5));
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    // Reject DNS rebinding and cross-site requests before attaching the privileged local credential.
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api")
        && (context.Request.Headers["X-Aurora-Web"] != "1"
            || (context.Request.Headers.TryGetValue("Origin", out var callerOrigin)
                && callerOrigin != $"{context.Request.Scheme}://{context.Request.Host}")))
    {
        context.Response.StatusCode = 403;
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
// REQ-WEB-010: browser-owned source picker lists server paths without reading file contents.
app.MapPost("/api/files", async (HttpContext context) =>
{
    try
    {
        var input = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body,
            cancellationToken: context.RequestAborted);
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String
            || pathValue.GetString()!.Length > 32768)
            return Results.BadRequest(new { success = false, message = "路径无效" });
        var path = pathValue.GetString()!;
        var directoriesOnly = input.TryGetProperty("directory", out var directoryValue) && directoryValue.ValueKind == JsonValueKind.True;
        var select = input.TryGetProperty("select", out var selectValue) && selectValue.ValueKind == JsonValueKind.True;
        if (select)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return Results.BadRequest(new { success = false, message = "请输入完整路径" });
            path = Path.GetFullPath(path);
            if (directoriesOnly ? !Directory.Exists(path) : !File.Exists(path))
                return Results.BadRequest(new { success = false, message = directoriesOnly ? "目录不存在" : "文件不存在" });
            return Results.Json(new { success = true, path });
        }
        if (string.IsNullOrWhiteSpace(path))
            return Results.Json(new { success = true, path = "", parent = "", truncated = false,
                entries = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new { name = d.Name, path = d.RootDirectory.FullName, directory = true }) });
        if (!Path.IsPathFullyQualified(path))
            return Results.BadRequest(new { success = false, message = "请输入完整路径" });
        path = Path.GetFullPath(path);
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!;
        var folder = new DirectoryInfo(path);
        var entries = folder.EnumerateFileSystemInfos().Where(entry => !directoriesOnly || entry is DirectoryInfo)
            .Take(2001).Select(entry => new { name = entry.Name, path = entry.FullName, directory = entry is DirectoryInfo }).ToArray();
        return Results.Json(new { success = true, path = folder.FullName, parent = folder.Parent?.FullName ?? "",
            truncated = entries.Length > 2000,
            entries = entries.Take(2000).OrderByDescending(entry => entry.directory).ThenBy(entry => entry.name, StringComparer.OrdinalIgnoreCase) });
    }
    catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
    {
        return Results.BadRequest(new { success = false, message = ex is JsonException ? "请求不是有效 JSON" : "无法读取路径：" + ex.Message });
    }
});
app.MapPost("/api/command", async (HttpContext context, IHttpClientFactory factory) =>
{
    if (context.Request.Headers["X-Aurora-Web"] != "1")
        return Results.StatusCode(403);
    if (context.Request.Headers.TryGetValue("Origin", out var origin)
        && origin != $"{context.Request.Scheme}://{context.Request.Host}")
        return Results.StatusCode(403);
    try
    {
        var input = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body,
            cancellationToken: context.RequestAborted);
        if (!input.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 65536)
            return Results.BadRequest(new { success = false, message = "指令为空或过长" });
        // Read on every request: Portunus rotates both port and token after restarting.
        var endpointPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HistoryVulcan", "ModuleData", "HistoryPortunus", "endpoint.json");
        using var endpoint = JsonDocument.Parse(await File.ReadAllTextAsync(endpointPath, context.RequestAborted));
        var port = endpoint.RootElement.GetProperty("port").GetInt32();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/command");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            endpoint.RootElement.GetProperty("accessToken").GetString());
        request.Headers.Add("X-HistoryVulcan-Client", "Shell");
        request.Headers.Add("X-Client-Name", "AuroraWeb");
        request.Content = JsonContent.Create(new { text = value.GetString() });
        using var response = await factory.CreateClient("portunus").SendAsync(request, context.RequestAborted);
        var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
        return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { success = false, message = "请求或网关描述不是有效 JSON" });
    }
    catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException
        or KeyNotFoundException or InvalidOperationException)
    {
        return Results.Json(new { success = false, message = "无法连接本机 Portunus，请确认 OneHistory 服务已启动" }, statusCode: 503);
    }
});
app.Run();
