# Baut index.html aus der Wiki-Seite "Valuables" (repo-2025horror.fandom.com).
# Aufruf: python build.py   (lädt Wikitext + Bilder neu, schreibt eine eigenständige HTML)
import base64, json, os, re, subprocess, time, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
API = 'https://repo-2025horror.fandom.com/api.php?'
UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130 Safari/537.36'
CACHE = os.path.join(HERE, 'cache')

# Reihenfolge = Sortierreihenfolge im Dropdown
VALUE_TYPES = [
    ('Very Cheap', 250, 500), ('Cheap-', 300, 450), ('Cheap', 500, 650), ('Cheap+', 850, 1100),
    ('Cheap++', 1200, 2000), ('Medium', 2000, 3000), ('Medium+', 3500, 4500), ('Medium++', 4500, 5000),
    ('High', 5500, 7500), ('High+', 9500, 12000), ('Expensive', 18000, 25000),
    ('Expensive+', 30000, 45000), ('Expensive++', 45000, 50000),
]
# Laut Wiki-Trivia erreicht das Spiel bei Cheap nur $500-600 und bei Cheap+ beginnt es real bei $900
ALIASES = {(500, 600): 'Cheap', (900, 1100): 'Cheap+'}


def api(params):
    q = urllib.parse.urlencode({**params, 'format': 'json'})
    return json.load(urllib.request.urlopen(urllib.request.Request(API + q, headers={'User-Agent': UA})))


def wikitext(page):
    return api({'action': 'parse', 'page': page, 'prop': 'wikitext'})['parse']['wikitext']['*']


def parse_rows(t):
    rows = []
    for block in re.findall(r'\{\{ValuablesTableRow(.*?)\}\}', t, re.S):
        d = {m.group(1): m.group(2).strip() for m in re.finditer(r'\|(\w+)=([^\n|]*)', block)}
        rows.append(d)
    return rows


def parse_traps(t):
    sec = t[t.find('=== Traps Valuables'):]
    sec = sec[:sec.find('|}')]
    traps = {}
    for cell in sec.split('|-')[1:]:
        lines = [l[1:].strip() for l in cell.strip().split('\n') if l.startswith('|')]
        if len(lines) < 5:
            continue
        name = re.sub(r'\[\[|\]\]', '', lines[0]).strip()
        img = re.search(r'File:([^|\]]+)', lines[1])
        traps[name] = {'noise': lines[2], 'trigger': lines[3], 'desc': lines[4], 'img': img and img.group(1).strip()}
    return traps


def money(s):
    s = s.replace(',', '').replace('$', '')
    return int(s) if s.isdigit() else None


def classify(value):
    v = re.sub(r'[^\d$+\-]', '-', value)  # kaputte Gedankenstriche im Wiki reparieren
    m = re.match(r'\$?([\d,]+)\+$', v)
    if m:
        return money(m.group(1)), None, None
    a, b = [money(x) for x in v.split('-') if x.strip('$')]
    vt = ALIASES.get((a, b))
    for name, lo, hi in VALUE_TYPES:
        if (lo, hi) == (a, b):
            vt = name
    return a, b, vt


def thumbs(names):
    os.makedirs(CACHE, exist_ok=True)
    urls = {}
    names = sorted(names)
    for i in range(0, len(names), 50):
        d = api({'action': 'query', 'titles': '|'.join('File:' + n for n in names[i:i + 50]),
                 'prop': 'imageinfo', 'iiprop': 'url', 'iiurlwidth': '96'})
        norm = {n['to']: n['from'] for n in d['query'].get('normalized', [])}
        for p in d['query']['pages'].values():
            if 'imageinfo' in p:
                urls[norm.get(p['title'], p['title'])[5:]] = p['imageinfo'][0].get('thumburl') or p['imageinfo'][0]['url']
    out = {}
    for n, u in urls.items():
        path = os.path.join(CACHE, re.sub(r'[^\w.]+', '_', n) + '.bin')
        if not os.path.exists(path):
            # urllib wird vom CDN nach ein paar Bildern geblockt, curl nicht
            for _ in range(3):
                data = subprocess.run(['curl', '-s', '-f', '-A', UA, '-H', 'Accept: image/webp,image/*', u],
                                      capture_output=True).stdout
                if data:
                    open(path, 'wb').write(data)
                    break
                time.sleep(2)
        if os.path.exists(path):
            data = open(path, 'rb').read()
            ct = 'image/webp' if data[:4] == b'RIFF' else 'image/jpeg' if data[:3] == b'\xff\xd8\xff' else 'image/png'
            out[n] = 'data:%s;base64,%s' % (ct, base64.b64encode(data).decode())
    return out


def main():
    t = wikitext('Valuables')
    rows, traps = parse_rows(t), parse_traps(t)
    imgs = thumbs({r['image'] for r in rows})
    items = []
    for r in rows:
        lo, hi, vt = classify(r['value'])
        name = r['item']
        mass = r.get('massValue') or r['mass']
        dmg = [int(x) for x in re.findall(r'\d+', r['damage'])]
        page = r.get('itemLink', name)
        # Trap-Tabelle nennt manche Items anders ("Clown" statt "Clown Doll"), das Bild passt aber
        trap = traps.get(name) or traps.get(page) or next((t for t in traps.values() if t['img'] == r['image']), None)
        items.append({
            'name': name, 'map': r['location'], 'size': r['size'],
            'lo': lo, 'hi': hi, 'vt': vt, 'raw': r['value'].replace('�', '-'),
            'mass': float(mass), 'massText': r['mass'], 'dmg': r['damage'],
            'maxDmg': max(dmg) if dmg else 0, 'img': imgs.get(r['image'], ''),
            'url': 'https://repo-2025horror.fandom.com/wiki/' + urllib.parse.quote(page.replace(' ', '_')),
            'trap': trap,
        })
    missing = [n for n in traps if not any(i['trap'] is traps[n] for i in items)]
    if missing:
        print('Traps ohne passendes Item:', missing)
    data = {'items': items, 'types': [{'name': n, 'lo': lo, 'hi': hi} for n, lo, hi in VALUE_TYPES]}
    html = open(os.path.join(HERE, 'template.html'), encoding='utf-8').read()
    html = html.replace('/*DATA*/null', json.dumps(data, ensure_ascii=False))
    open(os.path.join(HERE, 'index.html'), 'w', encoding='utf-8').write(html)
    print(len(items), 'Valuables,', sum(1 for i in items if i['img']), 'Bilder,',
          sum(1 for i in items if i['trap']), 'Traps')


if __name__ == '__main__':
    main()
