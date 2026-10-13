from __future__ import annotations

import json
import logging
from typing import Any

import httpx
from agent_framework._mcp import MCPStreamableHTTPTool
from azure.identity import get_bearer_token_provider

from .settings import AgentSettings

logger = logging.getLogger(__name__)

_live_view_url: str | None = None


class ToolboxAuth(httpx.Auth):
    def __init__(self, token_provider: Any):
        self._token_provider = token_provider

    def auth_flow(self, request: httpx.Request):
        request.headers["Authorization"] = f"Bearer {self._token_provider()}"
        yield request


def get_live_view_url() -> str | None:
    return _live_view_url


def clear_live_view_url() -> None:
    global _live_view_url
    _live_view_url = None


def resolve_toolbox_endpoint(settings: AgentSettings) -> str:
    return (
        f"{settings.project_endpoint.rstrip('/')}/toolboxes/"
        f"{settings.toolbox_name}/mcp?api-version=v1"
    )


def parse_toolbox_result(mcp_result: Any) -> str:
    parsed_content: list[Any] = []
    for item in getattr(mcp_result, "content", []) or []:
        text = getattr(item, "text", None)
        if text is None:
            parsed_content.append(str(item))
            continue
        try:
            parsed_content.append(json.loads(text))
        except json.JSONDecodeError:
            parsed_content.append(text)

    if not parsed_content:
        return "null"

    if len(parsed_content) == 1:
        value = parsed_content[0]
        if isinstance(value, dict) and value.get("browserSessionId"):
            result = dict(value)
            live_view_url = result.pop("liveViewUrl", None)
            if live_view_url:
                global _live_view_url
                _live_view_url = live_view_url
                result["note"] = "The live view link is delivered directly to the user."
            return json.dumps(result, ensure_ascii=False)
        if isinstance(value, str):
            return value
        return json.dumps(value, ensure_ascii=False)

    return json.dumps(parsed_content, ensure_ascii=False)


def make_toolbox_mcp_tool(
    settings: AgentSettings, credential: Any
) -> MCPStreamableHTTPTool:
    token_provider = get_bearer_token_provider(credential, settings.azure_scope)
    http_client = httpx.AsyncClient(
        auth=ToolboxAuth(token_provider),
        timeout=settings.mcp_timeout_seconds,
    )
    return MCPStreamableHTTPTool(
        name=settings.toolbox_name,
        url=resolve_toolbox_endpoint(settings),
        http_client=http_client,
        request_timeout=settings.mcp_timeout_seconds,
        load_prompts=False,
        parse_tool_results=parse_toolbox_result,
        description="Azure Playwright browser automation tools.",
        approval_mode="never_require",
    )
