# Form Filler Skill

1. Create or reuse a browser session and preserve its `browserSessionId`.
2. Use the discovered navigation tool to open the form.
3. Take a browser snapshot and identify every required field.
4. Prefer the discovered form-fill tool when filling several fields; otherwise
   use the discovered type, select, check, and click tools as appropriate.
5. Take a fresh snapshot after navigation, dialogs, or any page-changing action.
6. Submit only after all required values are present.
7. Verify the result with a final snapshot. Report OTP, CAPTCHA, authentication,
   or missing user data as a blocker without fabricating values.

Pass `browserSessionId` to every browser tool. Use only names and schemas
returned by MCP discovery.
