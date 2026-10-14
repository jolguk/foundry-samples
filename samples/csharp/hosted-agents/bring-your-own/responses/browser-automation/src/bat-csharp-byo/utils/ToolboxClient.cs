// Copyright (c) Microsoft. All rights reserved.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrowserAutomation;

public sealed record ToolboxTool(
    string Name,
    string Description,
    BinaryData InputSchema);

public sealed record ToolboxCallResult(
    string Output,
    string? BrowserSessionId,
    string? LiveViewUrl,
    bool IsError,
    string? SessionName = null);

public sealed class ToolboxClient
{
    private readonly string _endpoint;
    private readonly Func<string> _getToken;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(120),
    };
    private string? _sessionId;
    private IReadOnlyList<ToolboxTool>? _tools;
    private int _requestId;
    private bool _initialized;

    public ToolboxClient(
        string endpoint,
        Func<string> tokenProvider,
        ILogger logger)
    {
        _endpoint = endpoint;
        _getToken = tokenProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ToolboxTool>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tools is not null)
                return _tools;

            await InitializeCoreAsync(cancellationToken);
            var result = await PostCoreAsync(
                "tools/list",
                new { },
                cancellationToken);
            _tools = result.GetProperty("tools")
                .EnumerateArray()
                .Select(tool => new ToolboxTool(
                    tool.GetProperty("name").GetString()!,
                    tool.TryGetProperty("description", out var description)
                        ? description.GetString() ?? ""
                        : "",
                    BinaryData.FromString(
                        tool.GetProperty("inputSchema").GetRawText())))
                .ToList();
            _logger.LogInformation(
                "Discovered {Count} toolbox tools",
                _tools.Count);
            return _tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ToolboxCallResult> CallToolAsync(
        string name,
        BinaryData arguments,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeCoreAsync(cancellationToken);
            using var argumentDocument = JsonDocument.Parse(arguments);
            var result = await PostCoreAsync(
                "tools/call",
                new
                {
                    name,
                    arguments = argumentDocument.RootElement.Clone(),
                },
                cancellationToken);

            var textItems = result.TryGetProperty("content", out var content)
                ? content.EnumerateArray()
                    .Where(item =>
                        item.TryGetProperty("type", out var type)
                        && type.GetString() == "text")
                    .Select(item => item.GetProperty("text").GetString() ?? "")
                    .Where(text => text.Length > 0)
                    .ToList()
                : [];

            string? browserSessionId = null;
            string? liveViewUrl = null;
            var sanitized = new List<string>();
            foreach (var text in textItems)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        sanitized.Add(text);
                        continue;
                    }

                    if (root.TryGetProperty("browserSessionId", out var id))
                        browserSessionId ??= id.GetString();
                    if (root.TryGetProperty("liveViewUrl", out var url))
                        liveViewUrl ??= url.GetString();

                    if (root.TryGetProperty("liveViewUrl", out _))
                    {
                        var values = root.EnumerateObject()
                            .Where(property => property.Name != "liveViewUrl")
                            .ToDictionary(
                                property => property.Name,
                                property => property.Value.Clone());
                        values["note"] = JsonSerializer.SerializeToElement(
                            "The live view link is delivered directly to the user.");
                        sanitized.Add(JsonSerializer.Serialize(values));
                    }
                    else
                    {
                        sanitized.Add(text);
                    }
                }
                catch (JsonException)
                {
                    sanitized.Add(text);
                }
            }

            var output = sanitized.Count > 0
                ? string.Join("\n", sanitized)
                : result.GetRawText();
            var isError = result.TryGetProperty("isError", out var error)
                && error.GetBoolean();
            return new ToolboxCallResult(
                output,
                browserSessionId,
                liveViewUrl,
                isError);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await PostCoreAsync(
            "initialize",
            new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new
                {
                    name = "browser-automation-byo",
                    version = "1.0.0",
                },
            },
            cancellationToken);
        await PostNotificationCoreAsync(
            "notifications/initialized",
            cancellationToken);
        _initialized = true;
    }

    private async Task PostNotificationCoreAsync(
        string method,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _getToken());
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("mcp-session-id", _sessionId);

        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> PostCoreAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _requestId),
            method,
            @params = parameters,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _getToken());
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("mcp-session-id", _sessionId);

        using var response = await _client.SendAsync(
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("mcp-session-id", out var values))
            _sessionId = values.FirstOrDefault();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException(
                $"MCP {method} failed: {error.GetRawText()}");
        return root.GetProperty("result").Clone();
    }
}
