using System.Net.Http.Headers;
using System.Text.Json;
using HistoryAurora.Web;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
// REQ-WEB-001: frontend development stays local until Portunus provides remote authentication.
var port = args.SkipWhile(arg => arg != "--port").Skip(1).Select(text => int.TryParse(text, out var value) ? value : 0)
    .FirstOrDefault(value => value is > 0 and < 65536);
var url = $"http://127.0.0.1:{(port == 0 ? 5280 : port)}";
builder.WebHost.UseUrls(url);
var parentMode = ParentLink.Requested(args);
if (parentMode)
    builder.Logging.ClearProviders();   // stdout belongs to the module protocol (REQ-WEB-041).
builder.Services.AddHttpClient("portunus", client => client.Timeout = TimeSpan.FromMinutes(5));
var channel = new FrontendChannel();
var app = builder.Build();
// REQ-WEB-042: dev runs serve the source folder; the module package serves the copy embedded in this assembly.
var physicalRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
IFileProvider webFiles = Directory.Exists(physicalRoot)
    ? new PhysicalFileProvider(physicalRoot)
    : new EmbeddedFileProvider(typeof(FrontendChannel).Assembly, "HistoryAurora.Web.wwwroot");
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' blob:; frame-ancestors 'none'";
    // Reject DNS rebinding and cross-site requests before attaching the privileged local credential.
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    // EventSource cannot send custom headers; the push stream instead requires a same-origin fetch.
    var sameOriginStream = context.Request.Path.Equals("/api/events") && HttpMethods.IsGet(context.Request.Method)
        && context.Request.Headers["Sec-Fetch-Site"] == "same-origin";
    if (context.Request.Path.StartsWithSegments("/api") && !sameOriginStream
        && (context.Request.Headers["X-Aurora-Web"] != "1"
            || (context.Request.Headers.TryGetValue("Origin", out var callerOrigin)
                && callerOrigin != $"{context.Request.Scheme}://{context.Request.Host}")))
    {
        context.Response.StatusCode = 403;
        return;
    }
    await next(context);
});
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
// REQ-WEB-043: the page changes with every hot install while browsers and shells stay open,
// so nothing is stored: a stale app.js broke 2.1.1's native pages, and embedded files carry a fixed
// Last-Modified, which makes ETags collide whenever an edit keeps the file length.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = webFiles,
    OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-store",
});
// REQ-WEB-040: host-originated UI commands reach the most recently active page over SSE.
app.MapGet("/api/events", async (HttpContext context) =>
{
    var id = context.Request.Query["client"].ToString();
    var kind = context.Request.Query["kind"].ToString() switch { "mobile" => "mobile", "shell" => "shell", _ => "desktop" };
    if (id.Length is < 8 or > 64 || !id.All(char.IsAsciiLetterOrDigit))
    {
        context.Response.StatusCode = 400;
        return;
    }
    await channel.StreamAsync(context, id, kind, kind == "shell" ? context.Request.Query["launch"].ToString() : "");
});
app.MapPost("/api/events/active", async (HttpContext context) =>
{
    var input = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted);
    var known = input.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.String
        && channel.MarkActive(client.GetString()!, !input.TryGetProperty("visible", out var visible) || visible.ValueKind != JsonValueKind.False);
    return known ? Results.Json(new { success = true }) : Results.NotFound(new { success = false, message = "网页未连接推送通道" });
});
app.MapPost("/api/events/reply", async (HttpContext context) =>
{
    var input = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted);
    if (!input.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
        || !input.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { success = false, message = "回执缺少 id 或 result" });
    return channel.Complete(id.GetString()!, result)
        ? Results.Json(new { success = true })
        : Results.NotFound(new { success = false, message = "该请求已结束或不存在" });
});
// REQ-WEB-018: the native pure graph layout remains the single source for coordinates and lanes.
app.MapPost("/api/graph-layout", async (HttpContext context) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var parsed = HistoryAurora.Shell.Components.Graph.SwimlaneReader.Read(document.RootElement.GetRawText());
        if (!parsed.Ok) return Results.BadRequest(new { success = false, message = parsed.Error });
        var layout = HistoryAurora.Shell.Components.Graph.SwimlaneLayout.Arrange(parsed.Value!);
        return Results.Json(new { success = true, layout,
            description = new { parsed.Value!.Title, parsed.Value.LaneTitles, parsed.Value.SelectAction }, metrics = new
        {
            nodeWidth = HistoryAurora.Shell.Components.Graph.SwimlaneLayout.NodeWidth,
            nodeHeight = HistoryAurora.Shell.Components.Graph.SwimlaneLayout.NodeHeight,
            laneHeight = HistoryAurora.Shell.Components.Graph.SwimlaneLayout.LaneHeight,
            laneTitleWidth = HistoryAurora.Shell.Components.Graph.SwimlaneLayout.LaneTitleWidth
        } });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { success = false, message = "泳道描述不是有效 JSON" });
    }
});
// REQ-WEB-012: share Aurora's authoritative native page descriptions with the browser.
app.MapGet("/api/frontend", () => Results.Content(
    HistoryAurora.Shell.HostedPages.Views.HostedPageDescriptions.Json, "application/json"));
// REQ-WEB-039: the native reader owns page DTOs, defaults and envelope validation.
app.MapPost("/api/page-description", (Delegate)HistoryAurora.Web.DescriptionEndpoints.ReadPages);
app.MapPost("/api/action-description", (Delegate)HistoryAurora.Web.DescriptionEndpoints.ReadActions);
// Personal standalone panels share the desktop source, without modifying it.
app.MapGet("/api/panels", () =>
{
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HistoryVulcan", "ModuleData", "HistoryAurora", "panels");
    try { return Results.Json(HistoryAurora.Web.PanelFileReader.Read(directory), HistoryAurora.Web.PanelFileReader.WriteOptions); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Results.Json(new { success = false, message = "无法读取独立面板目录" }, statusCode: 503);
    }
});
// REQ-WEB-013: read desktop scene history without changing its files or current window.
app.MapGet("/api/layout-history", async (HttpContext context) =>
{
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HistoryVulcan", "ModuleData", "HistoryAurora", "layout");
    var layouts = new Dictionary<string, JsonElement>();
    var errors = new List<string>();
    if (Directory.Exists(directory))
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.layout.v1.json"))
        {
            var scene = Path.GetFileName(file)[..^".layout.v1.json".Length];
            if (scene.EndsWith(".defaults", StringComparison.OrdinalIgnoreCase) || scene == "all") continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, context.RequestAborted));
                layouts[scene] = document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{scene}：无法读取桌面布局历史");
            }
        }
    }
    return Results.Json(new { schemaVersion = 1, layouts, errors });
});
app.MapGet("/api/module-root", () => Results.Json(new { path = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HistoryVulcan", "Modules") }));
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
if (parentMode)
    _ = ParentLink.Start(channel, app.Lifetime, url);
app.Run();
