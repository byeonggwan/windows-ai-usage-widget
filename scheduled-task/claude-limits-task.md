# Claude desktop app scheduled task

In the Claude desktop app, open **Scheduled** in the sidebar and create a new task:

- **Name:** AI usage widget — Claude limits
- **Schedule:** every 30 minutes (cron `*/30 * * * *`)
- **Model:** a small model such as Haiku
- **Permission mode:** one that allows a single PowerShell command without asking (for example Auto), so runs don't stop to ask

Paste this as the task prompt:

```text
This is a tiny, mechanical job. Keep it as short as possible: exactly two tool calls, no plan, no task list, no wrap-up, no extra text.

1. Call the tool mcp__ccd_session_mgmt__get_usage with no arguments. (If it is only listed as a deferred tool, load it first with ToolSearch query "select:mcp__ccd_session_mgmt__get_usage".)
2. Save only the "plan" object from its result, verbatim as JSON, with one PowerShell call:

Set-Content -Encoding utf8 -Path "$env:LOCALAPPDATA\AiUsageWidget\claude-app.json" -Value @'
{"plan": <the plan object exactly as returned>}
'@

The closing '@ must be at the start of its own line. Do not read, create, or change any other file. Then reply with the single word: ok
```

After the first run, `%LOCALAPPDATA%\AiUsageWidget\claude-app.json` should look like this:

```json
{"plan": {"status": "ok", "plan": "Team", "windows": [
  {"label": "5-hour limit", "percentUsed": 13, "resetsAt": "2026-11-24T02:30:00.429Z", "resetsIn": "under a minute"},
  {"label": "Weekly · all models", "percentUsed": 67, "resetsAt": "2026-11-25T17:00:00.429Z", "resetsIn": "1d 14h"}
]}}
```

The widget reads the `5-hour limit` and `Weekly · all models` windows.
