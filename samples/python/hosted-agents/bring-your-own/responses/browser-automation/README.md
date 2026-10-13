# Browser Automation Agent (Python, Bring Your Own Responses)

This sample implements the Responses agent loop directly with the Python SDK
and uses the GA Azure Playwright MCP tools published through a Foundry Toolbox.

## What this sample demonstrates

- MCP initialization and dynamic browser-tool discovery with `tools/list`
- Conversion of MCP `inputSchema` values into Responses function tools
- Generic forwarding of browser calls through MCP `tools/call`
- Cross-request tracking of active `browserSessionId` values
- Exact live-view URL injection outside model-generated text
- Local skills for form filling and data extraction

The application hardcodes only the local `load_skill` function. Browser tool
names and schemas always come from the Toolbox MCP server.

## How it works

1. The runtime connects to the configured Toolbox MCP endpoint.
2. It initializes MCP and discovers browser tools through `tools/list`.
3. MCP schemas are converted into Responses function-tool definitions.
4. Model tool calls are forwarded generically through `tools/call`.
5. The returned `browserSessionId` is retained across requests.
6. `liveViewUrl` is removed from model-visible output and emitted directly in
   the streamed response.

## Key files

| Path | Purpose |
| --- | --- |
| `src/bat-python-byo/main.py` | Responses handler, dynamic tool loop, session tracking, and live-view streaming. |
| `src/bat-python-byo/utils/toolbox.py` | MCP initialize, `tools/list`, and `tools/call` client. |
| `src/bat-python-byo/utils/constants.py` | Local skill schema and system prompt. |
| `src/bat-python-byo/skills/` | Optional task procedures loaded by the model. |
| `hooks/setup-toolbox.*` | Publishes the GA browser automation toolbox. |

## Prerequisites

- An Azure AI Foundry project with a deployed chat model
- Azure CLI and Azure Developer CLI
- Python 3.12 or later
- `uv`

The provisioning hook can create a Playwright workspace when the current
identity has permission. Set `PLAYWRIGHT_SERVICE_RESOURCE_ID` to reuse an
existing workspace.

## Configuration

```dotenv
FOUNDRY_PROJECT_ENDPOINT=https://<account>.services.ai.azure.com/api/projects/<project>
AZURE_AI_MODEL_DEPLOYMENT_NAME=gpt-4.1
TOOLBOX_NAME=browser-automation-tools
```

Run `az login` before starting locally so `DefaultAzureCredential` can
authenticate model and Toolbox calls.

## Option 1: Azure Developer CLI

Initialize from a directory outside the sample:

```bash
azd ai agent init -t ./browser-automation/azure.yaml
azd provision
```

The `postprovision` hook creates or reuses the Playwright workspace, creates the
GA MCP connection, and publishes the `browser_automation` toolbox.

Run and invoke locally:

```bash
azd ai agent run
azd ai agent invoke --local --new-session "Open https://example.com and report the page title."
azd ai agent invoke --local "Follow the first link using the same browser session."
```

Deploy and invoke:

```bash
azd deploy
azd ai agent invoke --new-session "Open https://example.com and report the page title."
```

The `postdeploy` hook assigns the browser role required by the configured
project or hosted-agent identity.

## Option 2: VS Code

Install the Foundry Toolkit and Python extensions. After initialization, select
the generated virtual environment and press **F5** to run the agent and open
Agent Inspector.

To run manually:

```bash
cd src/bat-python-byo
uv sync --frozen --python 3.12
uv run --no-sync python main.py
```

Invoke the local Responses endpoint:

```bash
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -d '{"input":"Open https://example.com and report the page title."}'
```

Prefix a request with `/verbose` to include tool-call progress in the streamed
response.

## Browser lifecycle

The model calls the discovered tool ending in `create_browser_session`. The
runtime retains its `browserSessionId` and supplies active IDs in later
requests. The model passes the ID to every browser operation and calls the
discovered `close_browser_session` tool when done.

## CI and non-interactive provisioning

Set at least one of the following before `azd provision --no-prompt`:

```bash
azd env set PLAYWRIGHT_SERVICE_RESOURCE_ID "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.LoadTestService/playwrightWorkspaces/<name>"
azd env set PLAYWRIGHT_AUTH_TYPE "ProjectManagedIdentity"
```

To create a workspace instead:

```bash
azd env set PLAYWRIGHT_REGION "eastus"
azd env set PLAYWRIGHT_AUTH_TYPE "ProjectManagedIdentity"
```

Supported authentication values are `ProjectManagedIdentity`,
`AgenticIdentityToken`, and interactive `ApiKey`.

## Customize the sample

- Edit `utils/constants.py` to change the system prompt or local skill tool.
- Add skill documents under `skills/`.
- Adjust MCP forwarding and sanitization in `utils/toolbox.py`.
- Change hosted resources in `azure.yaml`.
