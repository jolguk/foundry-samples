---
name: azure-playwright-browser-automation
description: Automates browser interactions with the Azure Playwright browser tools discovered from Foundry Toolbox.
---

# Azure Playwright browser automation

## Start and reuse a session

1. Call the tool ending in `create_browser_session`.
2. Keep the returned `browserSessionId`.
3. Pass that ID to every browser tool until the session is closed.
4. Reuse the active session for follow-up work.

The live-view URL is delivered directly to the user. Do not repeat or modify it.

## Typical workflow

1. `browser_navigate` to the target URL.
2. `browser_snapshot` to inspect the accessibility tree.
3. Use refs or unique selectors with interaction tools such as:
   - `browser_click`
   - `browser_type`
   - `browser_fill_form`
   - `browser_select_option`
   - `browser_press_key`
4. Take a fresh snapshot after page-changing actions.
5. Use `browser_evaluate` only when the task genuinely requires page-side
   JavaScript.

Use `browser_tabs` for tab management, `browser_take_screenshot` for visual
evidence, and `browser_network_requests` or `browser_console_messages` for
diagnostics.

## Cleanup

Keep healthy sessions open for follow-up work. Call `close_browser_session` only
when explicitly requested, when the session is broken, or before replacing it.
