# Changelog

## v1.5.0
- **Usage history window** (Full; idea by @Kantorcodes): one card per Codex account with every rate-limit window over **7 / 15 / 30 days** (step lines that drop at each reset), how much of the long window each day used, and the burn rate now (%/h over 6 h, % in the last 24 h, run-out estimate)
- **Claude Code cards** in the same window: tokens per hour and per day over the range, plus 5 h / 24 h / 7 d totals (Claude Code logs have no limit %, so this is tokens, not % left)
- The history window is customisable from right-click: always on top, opacity, its own colour theme and dark / light (or follow the widget), borderless HUD look (drag to move, edges to resize); size and place are remembered
- **Click an account row** on the widget to open its history alone, larger; double-click the history window for all accounts again. Also in the menu, the tray menu, and by clicking an alert
- History works on day one: `collector.py --hist=31` reads the last 31 days of readings from the same local logs (once at start, then hourly); readings are kept 31 days
- Linked with System HUD side by side, only System HUD shows the clock (stacked, the top one still shows it with the date)
- Fixes from a GPT-6.1-sol review: per-day use no longer loses a rise across midnight, the chart catches a reset between stored readings, the history menu is disposed, an older LAP/VPS twin opens its own history, expired readings are pruned on disk
- **Low-quota alerts**: pick the level (30 / 20 / 10 / 5 % left, default 10 %), plus an optional alert when the current pace would run a window out before its reset
## v1.4.0
- **Group mode with System HUD**: snap edge to edge, move and resize together, take the HUD's width when stacked, one clock with the date, same colours + font (optional), Split apart
- Bars in the theme colour option (System HUD look); Frame full / subtle / none
- Row names via `%APPDATA%\QuotaWidget\names.txt`; logged-in homes without logs yet get a row
- Anthropic tab hides unused homes; scrolling moves a whole row at a time; long names fit in compact rows
- Fixes from a GPT-5.5 review: no stale group state on start, group file written atomically, partial reads retried; Sparkline no longer drops frames for accounts without rate data; tooltips no longer blink
- README: "How light is it?" and a Chinese (简体中文) section

## v1.3.0
- **New look**: severity colours that no longer follow the theme (blue fine, amber ≤30 % left, red ≤10 %, green ready) with a symbol on every status (⚠ ✓ ✕ ⏱), so nothing relies on colour alone
- Bars now show what is **left**, matching the number next to them
- Symmetric rows: every account uses the same two columns (5H | 7D/30D), numbers right-aligned in one line
- Fewer chips: LAP/VPS as a small dot, plan as quiet text, twin and "no 5H" details moved to the hover tooltip (now on every row)
- **Font picker** (right-click → Font): Segoe UI (GitHub style, default), Segoe UI Variable, Bahnschrift, Cascadia Mono, Consolas, Arial, Verdana, Inter if installed
- Small sizes stay readable: below ~80 % the text keeps its size and the layout gets narrower, with short labels (GPT / CLAUDE, 2h)
- Provider colour dots on the tabs, data-freshness warning in the footer
- Security: `SECURITY.md` and the HOL plugin-scanner CI

## v1.2.0
- Provider tabs ALL / OPENAI / ANTHROPIC; OpenAI grouped by plan + xKiro + other, Anthropic grouped by model
- "Most used" sort; Codex model read from the same local logs

## v1.1.0
- Lite and Full editions: ★ USE, sorting, folding bonus check; Full adds Windows alerts, compact rows, 7-day sparkline and run-out estimate

## v1.0.1
- Same account on LAP and VPS is linked and the older reading is dimmed (#1, idea by @kartikb753)

## v1.0.0
- First prebuilt release
