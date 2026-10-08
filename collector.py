# Quota collector for QuotaWidget: reads the logs Codex and Claude Code already write and prints one JSON line.
# Read only, no network, no AI calls (so it uses no quota). Never opens auth.json or any credential file.
# usage: python collector.py [home_dir]
import os, glob, json, sys, time, datetime

home = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser('~')
now = time.time()
WEEK = 8 * 86400


def iso(s):
    try:
        return datetime.datetime.fromisoformat(s.replace('Z', '+00:00')).timestamp()
    except Exception:
        return None


def recent(pattern, age):
    fs = [f for f in glob.glob(pattern, recursive=True) if now - os.path.getmtime(f) < age]
    return sorted(fs, key=os.path.getmtime, reverse=True)


def codex(d):
    files = recent(os.path.join(d, 'sessions', '**', '*.jsonl'), 400 * 86400)
    rec = {'home': os.path.basename(d), 'kind': 'codex', 'plan': None, 'at': None, 'win': [], 'tok5h': 0, 'tok24h': 0, 'blocked': None, 'model': None}
    found = False
    for i, f in enumerate(files[:40]):
        try:
            lines = open(f, encoding='utf-8', errors='ignore').read().splitlines()
        except Exception:
            continue
        for ln in reversed(lines):
            if rec['model'] is None and '"turn_context"' in ln:   # newest turn's model, from the same log
                try:
                    j = json.loads(ln); rec['model'] = (j.get('payload') or {}).get('model')
                except Exception:
                    pass
                continue
            if '"token_count"' not in ln:
                continue
            try:
                j = json.loads(ln)
            except Exception:
                continue
            p = j.get('payload', j)
            t = iso(j.get('timestamp', '')) or 0
            info = p.get('info') or {}
            last = (info.get('last_token_usage') or {}).get('total_tokens') or 0
            if now - t < 86400: rec['tok24h'] += last
            if now - t < 5 * 3600: rec['tok5h'] += last
            rl = p.get('rate_limits')
            if rl and not found and rl.get('limit_id') == 'codex' and (rl.get('primary') or rl.get('secondary')):
                found = True
                rec['plan'] = rl.get('plan_type'); rec['at'] = t
                for k in ('primary', 'secondary'):
                    w = rl.get(k)
                    if w: rec['win'].append({'used': w.get('used_percent'), 'mins': w.get('window_minutes'), 'reset': w.get('resets_at')})
            if rl and rec['blocked'] is None and rl.get('rate_limit_reached_type'):
                rec['blocked'] = rl.get('rate_limit_reached_type')
        if found and (i > 3 or now - os.path.getmtime(f) > 86400):
            break
    rec['auth'] = os.path.exists(os.path.join(d, 'auth.json'))
    return rec


def claude(d, name):
    rec = {'home': name, 'kind': 'claude', 'at': None, 'tok5h': 0, 'tok24h': 0, 'tok7d': 0, 'msgs24h': 0, 'model': None}
    seen = set()
    for f in recent(os.path.join(d, 'projects', '**', '*.jsonl'), WEEK):
        try:
            fh = open(f, encoding='utf-8', errors='ignore')
        except Exception:
            continue
        for ln in fh:
            if '"usage"' not in ln:
                continue
            try:
                j = json.loads(ln)
            except Exception:
                continue
            m = j.get('message') or {}
            u = m.get('usage')
            if not u:
                continue
            key = (m.get('id'), j.get('requestId'))
            if key in seen:
                continue
            seen.add(key)
            t = iso(j.get('timestamp', '')) or 0
            n = (u.get('input_tokens') or 0) + (u.get('output_tokens') or 0) + (u.get('cache_creation_input_tokens') or 0)
            if now - t < 7 * 86400: rec['tok7d'] += n
            if now - t < 86400: rec['tok24h'] += n; rec['msgs24h'] += 1
            if now - t < 5 * 3600: rec['tok5h'] += n
            if t > (rec['at'] or 0): rec['at'] = t; rec['model'] = m.get('model')
        fh.close()
    return rec


def xkiro():
    # GET /v1/usage only reads the meter (no model call, no cost). The key stays on this machine; only numbers are printed.
    # key file: $XKIRO_KEY_FILE, default ~/.config/xkiro/key (the key itself is never printed)
    kf = os.path.expanduser(os.environ.get('XKIRO_KEY_FILE', os.path.join(home, '.config', 'xkiro', 'key')))
    if not os.path.exists(kf):
        return None
    import re, urllib.request
    m = re.search(r'[A-Za-z0-9_.-]{20,}', open(kf, encoding='utf-8', errors='ignore').read())
    rec = {'home': 'xkiro', 'kind': 'xkiro', 'plan': None, 'at': now, 'win': [], 'free': None, 'wallet': None, 'tok5h': 0, 'tok24h': 0, 'err': None}
    try:
        req = urllib.request.Request('https://api.xkiro.com/v1/usage', headers={'Authorization': 'Bearer ' + m.group(0), 'User-Agent': 'quota-widget'})
        j = json.loads(urllib.request.urlopen(req, timeout=15).read())
        rec['plan'] = j.get('plan')
        for w in j.get('windows') or []:
            cap = float(w.get('cap_usd') or 0); spent = float(w.get('spent_usd') or 0)
            rec['win'].append({'used': 100 * spent / cap if cap else 0, 'mins': int(w.get('window_sec', 0)) // 60,
                               'reset': now + float(w.get('resets_in_sec') or 0), 'spent': spent, 'cap': cap})
        f = j.get('free_tokens') or {}
        rec['free'] = {'used': f.get('used_today'), 'limit': f.get('limit_per_day')}
        rec['wallet'] = float((j.get('wallet') or {}).get('balance_usd') or 0)
    except Exception as e:
        rec['err'] = type(e).__name__
    # token use of the Codex homes routed through xKiro
    # Codex homes that route through xKiro: $XKIRO_CODEX_HOMES (a glob), default ~/.config/xkiro/codex/*
    for d in glob.glob(os.path.expanduser(os.environ.get('XKIRO_CODEX_HOMES', os.path.join(home, '.config', 'xkiro', 'codex', '*')))):
        if os.path.isdir(os.path.join(d, 'sessions')):
            c = codex(d); rec['tok5h'] += c['tok5h']; rec['tok24h'] += c['tok24h']
    return rec


out = []
for d in sorted(glob.glob(os.path.join(home, '.codex*'))):
    # a home that is logged in but not used yet still gets a row ("no rate-limit data"), so a new account shows up at once
    if os.path.isdir(d) and (os.path.isdir(os.path.join(d, 'sessions')) or os.path.exists(os.path.join(d, 'auth.json'))):
        out.append(codex(d))
x = xkiro()
if x: out.append(x)
for d in sorted(glob.glob(os.path.join(home, '.claude*'))):
    if os.path.isdir(d) and os.path.isdir(os.path.join(d, 'projects')):
        out.append(claude(d, os.path.basename(d)))
print(json.dumps({'now': now, 'accounts': out}))
