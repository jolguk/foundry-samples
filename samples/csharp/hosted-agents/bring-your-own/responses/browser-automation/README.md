# Browser Automation Agent (C#, Bring Your Own Responses)

This sample implements the Responses agent loop directly with the .NET SDK and
uses the GA Azure Playwright MCP tools published through a Foundry Toolbox.

## What it demonstrates

- MCP initialization and dynamic browser tool discovery with `tools/list`
- Conversion of MCP `inputSchema` values into Responses function tools
- Generic forwarding of every browser call through MCP `tools/call`
- Cross-request tracking of active `browserSessionId` values
- Exact live-view link injection outside model-generated text
- Embedded skills for form filling and data extraction

The application hardcodes only the local `load_skill` function. Browser tool
names and schemas always come from the Toolbox MCP server.

## Key files

| Path | Purpose |
| --- | --- |
| `src/bat-csharp-byo/Program.cs` | Responses handler, dynamic tool loop, session tracking, and live-view streaming. |
| `src/bat-csharp-byo/utils/ToolboxClient.cs` | MCP initialize, `tools/list`, and `tools/call` client. |
| `src/bat-csharp-byo/utils/Constants.cs` | Local skill schema and system prompt. |
| `src/bat-csharp-byo/skills/` | Embedded task procedures loaded by the model. |
| `hooks/setup-toolbox.*` | Publishes the GA browser automation toolbox. |

## Configuration

```dotenv
FOUNDRY_PROJECT_ENDPOINT=https://<account>.services.ai.azure.com/api/projects/<project>
AZURE_AI_MODEL_DEPLOYMENT_NAME=gpt-4.1
TOOLBOX_NAME=browser-automation-tools
```

For local use, run `az login` so `DefaultAzureCredential` can authenticate the
model and Toolbox calls.

## Run locally

```bash
cd src/bat-csharp-byo
dotnet run
```

Invoke `http://localhost:8088/responses` with a Responses request containing an
`input` value. Prefix a request with `/verbose` to include tool-call progress.

## Deploy

Run initialization from outside the sample directory:

```bash
azd ai agent init -t ./browser-automation/azure.yaml
azd provision
azd deploy
```

Provisioning creates or reuses an Azure Playwright workspace, creates its GA MCP
connection, and publishes a `browser_automation` toolbox. Deployment assigns
the required browser role to the hosted agent identity.

## Browser lifecycle

The model calls the discovered tool ending in `create_browser_session`. The
runtime retains its `browserSessionId` and supplies active IDs in later
requests. The model passes the ID to every browser operation and calls the
discovered `close_browser_session` tool when done. The runtime removes the
corresponding session from its active state.
