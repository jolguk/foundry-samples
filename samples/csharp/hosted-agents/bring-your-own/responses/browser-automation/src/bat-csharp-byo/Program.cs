// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using OpenAI.Responses;

namespace BrowserAutomation;

public class Program
{
    public static void Main(string[] args)
    {
        Env.NoClobber().TraversePath().Load();

        var endpoint = Environment.GetEnvironmentVariable(
                "FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException(
                "FOUNDRY_PROJECT_ENDPOINT environment variable is not set.");
        var deployment = Environment.GetEnvironmentVariable(
                "AZURE_AI_MODEL_DEPLOYMENT_NAME")
            ?? throw new InvalidOperationException(
                "AZURE_AI_MODEL_DEPLOYMENT_NAME environment variable is not set.");
        var toolboxName = Environment.GetEnvironmentVariable("TOOLBOX_NAME")
            ?? Constants.DefaultToolboxName;

        var credential = new DefaultAzureCredential();
        var responsesClient = new AIProjectClient(
                new Uri(endpoint),
                credential)
            .ProjectOpenAIClient
            .GetProjectResponsesClientForModel(deployment);

        ResponsesServer.Run<BrowserAutomationHandler>(configure: builder =>
        {
            builder.Services.AddSingleton(responsesClient);
            builder.Services.AddSingleton(new ToolboxConfig(
                $"{endpoint.TrimEnd('/')}/toolboxes/{toolboxName}/mcp?api-version=v1",
                credential));
        });
    }
}

public record ToolboxConfig(
    string Endpoint,
    DefaultAzureCredential Credential);

public class BrowserAutomationHandler : ResponseHandler
{
    private readonly ProjectResponsesClient _responsesClient;
    private readonly ILogger<BrowserAutomationHandler> _logger;
    private static readonly ConcurrentDictionary<string, string>
        ActiveSessions = new();
    private static ToolboxClient? _toolbox;
    private static readonly object ToolboxLock = new();

    public BrowserAutomationHandler(
        ProjectResponsesClient responsesClient,
        ToolboxConfig toolboxConfig,
        ILogger<BrowserAutomationHandler> logger)
    {
        _responsesClient = responsesClient;
        _logger = logger;
        lock (ToolboxLock)
        {
            _toolbox ??= new ToolboxClient(
                toolboxConfig.Endpoint,
                () => toolboxConfig.Credential.GetToken(
                    new Azure.Core.TokenRequestContext(
                        [Constants.AzureAiScope]),
                    default).Token,
                logger);
        }
    }

    public override IAsyncEnumerable<ResponseStreamEvent> CreateAsync(
        CreateResponse request,
        ResponseContext context,
        CancellationToken cancellationToken) =>
        new TextResponse(
            context,
            request,
            createTextStream: ct => ProcessAsync(
                request,
                context,
                ct));

    private async IAsyncEnumerable<string> ProcessAsync(
        CreateResponse request,
        ResponseContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var userInput = await context.GetInputTextAsync(
            cancellationToken: cancellationToken) ?? "";
        if (string.IsNullOrWhiteSpace(userInput))
        {
            yield return "No input provided.";
            yield break;
        }

        var verbose = userInput.TrimStart().StartsWith(
            "/verbose",
            StringComparison.OrdinalIgnoreCase);
        if (verbose)
            userInput = userInput.Replace(
                "/verbose",
                "",
                StringComparison.OrdinalIgnoreCase).TrimStart();

        var inputItems = new List<ResponseItem>();
        try
        {
            var history = await context.GetHistoryAsync(cancellationToken);
            foreach (var item in history.OfType<OutputItemMessage>())
            {
                foreach (var content in item.Content ?? [])
                {
                    if (content is MessageContentOutputTextContent
                        { Text: { } assistantText })
                        inputItems.Add(
                            ResponseItem.CreateAssistantMessageItem(
                                assistantText));
                    else if (content is MessageContentInputTextContent
                        { Text: { } userText })
                        inputItems.Add(
                            ResponseItem.CreateUserMessageItem(userText));
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "get_history failed; continuing without: {Error}",
                exception.Message);
        }

        if (!ActiveSessions.IsEmpty)
        {
            inputItems.Add(ResponseItem.CreateUserMessageItem(
                "[Active browserSessionId values: "
                + string.Join(", ", ActiveSessions.Keys)
                + "]"));
        }
        inputItems.Add(ResponseItem.CreateUserMessageItem(userInput));

        var instructions = Constants.GetSystemPrompt(Skills.ListSkills());
        var tools = await BuildToolsAsync(cancellationToken);
        string finalReply = "";

        for (var round = 0; round < 20; round++)
        {
            var options = new CreateResponseOptions
            {
                Instructions = instructions,
            };
            foreach (var tool in tools)
                options.Tools.Add(tool);
            foreach (var item in inputItems)
                options.InputItems.Add(item);

            var response = await _responsesClient.CreateResponseAsync(
                options,
                cancellationToken);
            var calls = response.Value.OutputItems
                .OfType<FunctionCallResponseItem>()
                .ToList();
            if (calls.Count == 0)
            {
                finalReply = response.Value.GetOutputText()
                    ?? "(No response)";
                break;
            }

            foreach (var call in calls)
            {
                var result = await HandleToolCallAsync(
                    call.FunctionName,
                    call.FunctionArguments,
                    cancellationToken);
                if (result.LiveViewUrl is not null)
                {
                    yield return "🔴 Created Browser Session: "
                        + $"[Live View]({result.LiveViewUrl})\n";
                }
                if (verbose)
                    yield return $"🔧 `{call.FunctionName}`\n";

                inputItems.Add(ResponseItem.CreateFunctionCallItem(
                    call.CallId,
                    call.FunctionName,
                    call.FunctionArguments));
                inputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                    call.CallId,
                    result.IsError
                        ? $"Tool error: {result.Output}"
                        : result.Output));
            }
        }

        if (string.IsNullOrEmpty(finalReply))
            finalReply = "Tool-call limit reached before the task completed.";
        yield return finalReply;

        var links = ActiveSessions
            .Where(pair => pair.Value.Length > 0)
            .Select(pair =>
                $"- `{pair.Key}`: [Live View]({pair.Value})")
            .ToList();
        if (links.Count > 0)
        {
            yield return "\n\n---\n**Active Browser Sessions:**\n"
                + string.Join("\n", links);
        }
    }

    private async Task<ToolboxCallResult> HandleToolCallAsync(
        string functionName,
        BinaryData arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "[tool] name={Name} arguments={Arguments}",
                functionName,
                Redaction.Redact(arguments.ToString()));

            if (functionName == "load_skill")
            {
                using var document = JsonDocument.Parse(arguments);
                var name = document.RootElement.GetProperty("name").GetString()
                    ?? "";
                var skill = Skills.LoadSkill(name);
                var output = skill is null
                    ? JsonSerializer.Serialize(new
                    {
                        error = $"Skill '{name}' not found.",
                    })
                    : JsonSerializer.Serialize(new
                    {
                        skill = name,
                        instructions = skill,
                    });
                return new ToolboxCallResult(
                    output,
                    null,
                    null,
                    skill is null);
            }

            var result = await _toolbox!.CallToolAsync(
                functionName,
                arguments,
                cancellationToken);
            var shortName = functionName.Split("___").Last();
            if (shortName == "create_browser_session"
                && result.BrowserSessionId is not null)
            {
                ActiveSessions[result.BrowserSessionId] =
                    result.LiveViewUrl ?? "";
            }
            else if (shortName == "close_browser_session")
            {
                using var document = JsonDocument.Parse(arguments);
                var root = document.RootElement;
                if (root.TryGetProperty("browserSessionId", out var id))
                    ActiveSessions.TryRemove(id.GetString() ?? "", out _);
            }
            return result;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Tool '{Name}' failed",
                functionName);
            return new ToolboxCallResult(
                $"Tool call failed: {exception.Message}",
                null,
                null,
                true);
        }
    }

    private async Task<List<ResponseTool>> BuildToolsAsync(
        CancellationToken cancellationToken)
    {
        var definitions = new List<JsonElement>();
        using (var localDocument = JsonDocument.Parse(
                   Constants.LoadSkillDefinition))
        {
            definitions.Add(localDocument.RootElement.Clone());
        }

        var tools = new List<ResponseTool>();
        var local = definitions[0];
        tools.Add(ResponseTool.CreateFunctionTool(
            functionName: local.GetProperty("name").GetString()!,
            functionDescription: local.GetProperty("description").GetString()!,
            functionParameters: BinaryData.FromString(
                local.GetProperty("parameters").GetRawText()),
            strictModeEnabled: false));

        foreach (var tool in await _toolbox!.ListToolsAsync(cancellationToken))
        {
            tools.Add(ResponseTool.CreateFunctionTool(
                functionName: tool.Name,
                functionDescription: tool.Description,
                functionParameters: tool.InputSchema,
                strictModeEnabled: false));
        }
        return tools;
    }
}
