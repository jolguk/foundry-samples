// Copyright (c) Microsoft. All rights reserved.

namespace BrowserAutomation;

public static class Constants
{
    public const string AzureAiScope = "https://ai.azure.com/.default";
    public const string DefaultToolboxName = "browser-automation-tools";

    public const string LoadSkillDefinition = """
    {
      "type": "function",
      "name": "load_skill",
      "description": "Load the complete instructions for a skill before using it.",
      "parameters": {
        "type": "object",
        "properties": {
          "name": {
            "type": "string",
            "description": "The skill name to load."
          }
        },
        "required": ["name"],
        "additionalProperties": false
      }
    }
    """;

    public static string GetSystemPrompt(IEnumerable<string> skills) =>
        $"""
        You are a browser automation agent with dynamically discovered Azure
        Playwright MCP tools.

        Available skills: {string.Join(", ", skills)}

        Use load_skill before applying a skill. Follow the loaded instructions.

        Browser session lifecycle:
        - Call the discovered tool whose name ends with create_browser_session
          before using any other browser tool.
        - Preserve the returned browserSessionId and pass it to every browser tool.
        - Reuse an active browser session when one is supplied in the conversation.
        - Call the discovered tool whose name ends with close_browser_session when
          the browser task is complete.
        - The application delivers the exact live-view link directly to the user.
          Do not repeat, alter, shorten, or construct a live-view URL yourself.

        Never invent browser tool names or schemas. Use only the supplied tools.
        """;
}
