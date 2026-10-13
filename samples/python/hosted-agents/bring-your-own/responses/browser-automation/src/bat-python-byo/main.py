# Copyright (c) Microsoft. All rights reserved.

import asyncio
import json
import logging
import os

from azure.ai.agentserver.responses import (
    CreateResponse,
    ResponseContext,
    ResponseEventStream,
    ResponsesAgentServerHost,
    ResponsesServerOptions,
)
from azure.ai.agentserver.responses.models import get_input_expanded
from azure.ai.projects import AIProjectClient
from azure.identity import DefaultAzureCredential
from dotenv import load_dotenv

from utils.constants import LOAD_SKILL_TOOL, SYSTEM_PROMPT
from utils.skills import list_skills, load_skill
from utils.toolbox import ToolboxCallResult, ToolboxClient
from utils.utils import redact

load_dotenv(override=False)

logger = logging.getLogger(__name__)
logging.basicConfig(level=logging.INFO)

_endpoint = os.environ["FOUNDRY_PROJECT_ENDPOINT"]
_model = os.environ["AZURE_AI_MODEL_DEPLOYMENT_NAME"]
_toolbox_name = os.getenv("TOOLBOX_NAME", "browser-automation-tools")

_credential = DefaultAzureCredential()
_project_client = AIProjectClient(endpoint=_endpoint, credential=_credential)
_responses_client = _project_client.get_openai_client().responses
_toolbox = ToolboxClient(
    f"{_endpoint.rstrip('/')}/toolboxes/{_toolbox_name}/mcp",
    _credential,
)
_tools: list[dict] | None = None
_active_sessions: dict[str, str | None] = {}


def _get_input_text(request: CreateResponse) -> str | None:
    value = request.get("input")
    if isinstance(value, str):
        return value
    for item in get_input_expanded(request):
        content = item.get("content")
        if isinstance(content, str):
            return content
        for part in content or []:
            if text := part.get("text"):
                return text
    return None


def _build_input(current_input: str, history: list) -> list[dict]:
    input_items = []
    for item in history:
        for content in item.get("content") or []:
            text = content.get("text")
            if content.get("type") == "output_text" and text:
                input_items.append({"role": "assistant", "content": text})
            elif content.get("type") == "input_text" and text:
                input_items.append({"role": "user", "content": text})
    if _active_sessions:
        input_items.append({
            "role": "user",
            "content": (
                "[Active browserSessionId values: "
                + ", ".join(_active_sessions)
                + "]"
            ),
        })
    input_items.append({"role": "user", "content": current_input})
    return input_items


async def _handle_tool_call(call: object) -> ToolboxCallResult:
    name = getattr(call, "name", "")
    arguments = json.loads(getattr(call, "arguments", "") or "{}")
    logger.info("[tool] name=%s arguments=%s", name, redact(json.dumps(arguments)))

    if name == "load_skill":
        skill_name = arguments.get("skill_name", "")
        return ToolboxCallResult(
            output=json.dumps(load_skill(skill_name), ensure_ascii=False)
        )

    result = await asyncio.to_thread(_toolbox.call_tool, name, arguments)
    short_name = name.rsplit("___", maxsplit=1)[-1]
    if short_name == "create_browser_session" and result.browser_session_id:
        _active_sessions[result.browser_session_id] = result.live_view_url
    elif short_name == "close_browser_session":
        session_id = (
            arguments.get("browserSessionId")
            or arguments.get("browser_session_id")
        )
        if session_id:
            _active_sessions.pop(str(session_id), None)
    return result


async def _get_tools() -> list[dict]:
    global _tools
    if _tools is None:
        browser_tools = await asyncio.to_thread(_toolbox.response_tools)
        _tools = [LOAD_SKILL_TOOL, *browser_tools]
    return _tools


async def _run_agent_loop(input_items: list[dict]):
    instructions = SYSTEM_PROMPT.format(skills=", ".join(list_skills()))
    tools = await _get_tools()

    for _ in range(20):
        response = await asyncio.to_thread(
            _responses_client.create,
            model=_model,
            instructions=instructions,
            input=input_items,
            tools=tools,
            store=False,
        )
        tool_calls = [
            item
            for item in response.output
            if getattr(item, "type", None) == "function_call"
        ]
        if not tool_calls:
            yield "reply", response.output_text or "(No response)"
            return

        for call in tool_calls:
            try:
                result = await _handle_tool_call(call)
                if result.live_view_url:
                    yield (
                        "session",
                        "🔴 Created Browser Session: "
                        f"[Live View]({result.live_view_url})\n",
                    )
                output = result.output
                if result.is_error:
                    output = f"Tool error: {output}"
            except Exception as exc:
                logger.exception("Tool call failed: %s", call.name)
                output = f"Tool call failed: {exc}"

            input_items.append({
                "type": "function_call",
                "id": call.id,
                "call_id": call.call_id,
                "name": call.name,
                "arguments": call.arguments,
            })
            input_items.append({
                "type": "function_call_output",
                "call_id": call.call_id,
                "output": output,
            })

            yield "log", f"🔧 `{call.name}`\n"

    yield "reply", "Tool-call limit reached before the task completed."


app = ResponsesAgentServerHost(
    options=ResponsesServerOptions(
        default_fetch_history_count=20,
        sse_keep_alive_interval_seconds=15,
    ),
)


@app.response_handler
async def handler(
    request: CreateResponse,
    context: ResponseContext,
    cancellation_signal: asyncio.Event,
):
    stream = ResponseEventStream(
        response_id=context.response_id,
        request=request,
    )
    yield stream.emit_created()
    yield stream.emit_in_progress()

    user_input = _get_input_text(request) or ""
    verbose_mode = user_input.strip().startswith("/verbose")
    if verbose_mode:
        user_input = user_input.replace("/verbose", "", 1).strip()

    message_item = stream.add_output_item_message()
    yield message_item.emit_added()
    text_content = message_item.add_text_content()
    yield text_content.emit_added()

    if not user_input:
        yield text_content.emit_delta("No input provided.")
    else:
        try:
            history = await context.get_history()
        except Exception as exc:
            logger.warning("get_history failed; continuing without history: %s", exc)
            history = []

        input_items = _build_input(user_input, history)
        final_reply = ""
        async for kind, text in _run_agent_loop(input_items):
            if cancellation_signal.is_set():
                final_reply = "Request was cancelled."
                break
            if kind == "session" or (kind == "log" and verbose_mode):
                yield text_content.emit_delta(text)
            elif kind == "reply":
                final_reply = text

        yield text_content.emit_delta(final_reply)
        links = [
            f"- `{session_id}`: [Live View]({url})"
            for session_id, url in _active_sessions.items()
            if url
        ]
        if links:
            yield text_content.emit_delta(
                "\n\n---\n**Active Browser Sessions:**\n" + "\n".join(links)
            )

    yield text_content.emit_text_done()
    yield text_content.emit_done()
    yield message_item.emit_done()
    yield stream.emit_completed()


app.run()
