# Copyright (c) Microsoft. All rights reserved.

LOAD_SKILL_TOOL = {
    "type": "function",
    "name": "load_skill",
    "description": (
        "Load the complete instructions for a skill before using it. "
        "Pass the skill name exactly as listed in the system prompt."
    ),
    "parameters": {
        "type": "object",
        "properties": {
            "skill_name": {
                "type": "string",
                "description": "The skill name to load.",
            }
        },
        "required": ["skill_name"],
        "additionalProperties": False,
    },
    "strict": True,
}

SYSTEM_PROMPT = """You are a browser automation agent with dynamically discovered
Azure Playwright MCP tools.

Available skills:
{skills}

Use load_skill before applying a skill. Follow the loaded instructions exactly.

Browser session lifecycle:
- Call the discovered tool whose name ends with create_browser_session before
  using any other browser tool.
- Preserve the returned browserSessionId and pass it to every browser tool.
- Reuse an active browser session when one is supplied in the conversation.
- Call the discovered tool whose name ends with close_browser_session when the
  browser task is complete.
- The application delivers the exact live-view link directly to the user. Do
  not repeat, alter, shorten, or construct a live-view URL yourself.

Never invent browser tool names or schemas. Use only the tools supplied to you.
"""
