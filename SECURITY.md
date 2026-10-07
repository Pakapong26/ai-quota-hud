# Security

## Supported versions
Only the latest release gets fixes.

## What the widget touches
- Reads local log files: `~/.codex*/sessions/**/*.jsonl` and `~/.claude*/projects/**/*.jsonl`. It never opens `auth.json` or any credential file.
- Optional VPS: runs `collector.py` over SSH with your existing key (`BatchMode=yes`), nothing is written on the server.
- Optional xKiro: one `GET /v1/usage` meter read with the key from your own key file; the key is never printed or saved elsewhere.
- Settings and history stay in `%APPDATA%\QuotaWidget`. No telemetry, no other network calls.

## Reporting a vulnerability
Please use GitHub's private **Report a vulnerability** form (Security tab) instead of a public issue. I aim to reply within a few days.
