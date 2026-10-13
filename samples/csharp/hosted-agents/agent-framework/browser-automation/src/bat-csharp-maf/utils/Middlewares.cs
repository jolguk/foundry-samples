// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace BrowserAutomation;

public static class Middlewares
{
    private static readonly ILogger Logger =
        LoggerFactory.Create(builder => builder.AddConsole())
            .CreateLogger("Middlewares");
    private static string? _liveViewUrl;

    public static async ValueTask<object?> FunctionInvocationMiddleware(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var functionName = context.Function.Name;
        var arguments = context.CallContent?.Arguments;
        var safeArguments = arguments is null
            ? ""
            : Redaction.Redact(
                string.Join(", ", arguments.Select(pair => $"{pair.Key}={pair.Value}")));
        Logger.LogInformation(
            "[tool] name={Name} arguments={Arguments}",
            functionName,
            safeArguments);

        var result = await next(context, cancellationToken);

        if (functionName.EndsWith(
                "close_browser_session",
                StringComparison.OrdinalIgnoreCase))
        {
            _liveViewUrl = null;
            return result;
        }

        if (!functionName.EndsWith(
                "create_browser_session",
                StringComparison.OrdinalIgnoreCase))
            return result;

        try
        {
            using var document = JsonDocument.Parse(result?.ToString() ?? "");
            var root = document.RootElement;
            if (!root.TryGetProperty("browserSessionId", out var sessionId))
                return result;

            if (root.TryGetProperty("liveViewUrl", out var liveViewUrl))
                _liveViewUrl = liveViewUrl.GetString();

            return JsonSerializer.Serialize(new
            {
                browserSessionId = sessionId.GetString(),
                note = "The live view link is delivered directly to the user.",
            });
        }
        catch (JsonException)
        {
            return result;
        }
    }

    public static async Task<AgentResponse> LiveViewUrlMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var response = await innerAgent.RunAsync(
            messages,
            session,
            options,
            cancellationToken);
        if (!string.IsNullOrEmpty(_liveViewUrl))
        {
            response.Messages.Add(new ChatMessage(
                ChatRole.Assistant,
                $"\n\n🔴 [Browser Live View]({_liveViewUrl})"));
        }
        return response;
    }

    public static async IAsyncEnumerable<AgentResponseUpdate>
        LiveViewUrlStreamingMiddleware(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            AIAgent innerAgent,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var initialUrl = _liveViewUrl;
        var injected = !string.IsNullOrEmpty(initialUrl);

        await foreach (var update in innerAgent.RunStreamingAsync(
                           messages,
                           session,
                           options,
                           cancellationToken))
        {
            if (!injected && !string.IsNullOrEmpty(_liveViewUrl))
            {
                yield return new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [new TextContent(
                        $"🔴 Created Browser Session: [Live View]({_liveViewUrl})\n\n")]);
                injected = true;
            }
            yield return update;
        }

        if (!string.IsNullOrEmpty(_liveViewUrl))
        {
            yield return new AgentResponseUpdate(
                ChatRole.Assistant,
                [new TextContent(
                    $"\n\n🔴 [Browser Live View]({_liveViewUrl})")])
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }
    }
}
