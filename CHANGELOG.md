# Changelog

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
