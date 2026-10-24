# Baut index.html aus der Wiki-Seite "Valuables" (repo-2025horror.fandom.com) und den echten
# Spielwerten aus spieldaten.json (erzeugt von spieldaten.py). Wo beide sich widersprechen, gewinnt das
# Spiel; der Wiki-Wert wird als Korrektur mit angezeigt.
# Aufruf: python build.py   (lädt Wikitext + Bilder neu, schreibt eine eigenständige HTML)
import base64, json, os, re, subprocess, time, urllib.parse, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
API = 'https://repo-2025horror.fandom.com/api.php?'
UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130 Safari/537.36'
CACHE = os.path.join(HERE, 'cache')
GAME = os.path.join(HERE, 'spieldaten.json')

# Prefab-Name im Spiel -> Name im Wiki, wo sie sich nicht von selbst zuordnen lassen
PREFAB_ALIAS = {
    'Valuable Arctic Bonsai': 'Bonsai Tree', 'Valuable Arctic Sample Cooler Wide': 'Wide Sample Cooler',
    'Valuable Manor Vase Small': 'Small Vase', 'Valuable Manor Clown': 'Clown Doll',
    'Valuable Manor Vase Chunky': 'Chunky Vase', 'Valuable Manor Vase Big': 'Big Vase',
    'Valuable Museum GoldTooth': 'Golden Tooth', 'Valuable Museum Car': 'Toy Car',
    'Valuable Museum Plane': 'Toy Plane', 'Valuable Museum Gumball': 'Gumball Machine',
}
SPECIAL_PREFABS = {
    'Small Enemy Valuable': 'Enemy Valuable - Small', 'Medium Enemy Valuable': 'Enemy Valuable - Medium',
    'Big Enemy Valuable': 'Enemy Valuable - Big', 'Small Surplus Valuable': 'Surplus Valuable - Small',
    'Medium Surplus Valuable': 'Surplus Valuable - Medium', 'Big Surplus Valuable': 'Surplus Valuable - Big',
}

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


def parse_wikitable(block):
    """Zeilen einer Wiki-Tabelle als Listen von Zellen, rowspan aufgelöst."""
    rows, pending = [], {}  # Spalte -> [Wert, verbleibende Zeilen]
    for raw in block.split('|-')[1:]:
        cells = [l[1:].strip() for l in raw.strip().split('\n') if l.startswith('|') and not l.startswith('|}')]
        row, col = [], 0
        for cell in cells:
            while col in pending:
                row.append(pending[col][0])
                pending[col][1] -= 1
                if not pending[col][1]:
                    del pending[col]
                col += 1
            m = re.match(r'rowspan="(\d+)"\s*\|(.*)', cell)
            value = (m.group(2) if m else cell).strip()
            if m and int(m.group(1)) > 1:
                pending[col] = [value, int(m.group(1)) - 1]
            row.append(value)
            col += 1
        while col in pending:
            row.append(pending[col][0])
            pending[col][1] -= 1
            if not pending[col][1]:
                del pending[col]
            col += 1
        rows.append(row)
    return rows


def parse_level_tables(t):
    """Die beiden Level-Tabellen des Wikis, als Vergleich für den Level-Rechner."""
    tables = re.findall(r'\{\|.*?\n\|\}', t, re.S)
    sizes = next(x for x in tables if 'Tiny Max Amount' in x)
    haul = next(x for x in tables if 'Haul Goal Multiplier' in x)
    out = {}
    for r in parse_wikitable(sizes):
        n = 20 if r[0].startswith('20') else int(r[0])
        out[n] = {'maxValue': int(r[1]), 'amounts': [int(x) for x in r[2:9]]}
    for r in parse_wikitable(haul):
        out[int(r[0])].update(extractionPoints=int(r[1]), haulMultiplier=float(r[2]), multiplier=float(r[3]))
    return out


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


def norm_name(s):
    s = re.sub(r'^valuable\s+', '', s.lower())
    s = re.sub(r'\s+valuable.*$', '', s)
    s = re.sub(r'^(arctic|manor|museum|wizard)\s+', '', s)
    return re.sub(r'[^a-z0-9]', '', s)


def game_lookup(game, rows):
    """Wiki-Zeile -> Spieldaten-Eintrag. Bricht ab, wenn ein Item auf einer Seite fehlt."""
    found = {}
    for map_name, entries in game['maps'].items():
        for e in entries:
            alias = PREFAB_ALIAS.get(e['prefab'])
            hits = [r for r in rows if r['location'] == map_name and
                    (r['item'] == alias if alias else norm_name(r['item']) == norm_name(e['prefab']))]
            if len(hits) != 1:
                raise SystemExit(f'{map_name}: {e["prefab"]} passt zu {len(hits)} Wiki-Einträgen')
            found[id(hits[0])] = e
    for wiki_name, prefab in SPECIAL_PREFABS.items():
        (hit,) = [r for r in rows if r['item'] == wiki_name]
        found[id(hit)] = dict(game['special'][prefab], prefab=prefab)
    missing = [r['item'] for r in rows if id(r) not in found]
    if missing:
        raise SystemExit('Wiki-Items ohne Spieldaten: ' + ', '.join(missing))
    return found


def spawn_range(lo, hi):
    """Welche Preise wirklich vorkommen (ValuableObject.DollarValueSetLogic): Random.Range(lo, hi), dann
    Mathf.Round auf ganze Dollar und noch einmal auf 100, beides Banker's Rounding (wie Pythons round).
    Randwerte, die nur ein einzelner Dollarbetrag trifft (bei Cheap+ z.B. $850 -> $800), zählen nicht mit."""
    weights = {}
    for x in range(round(lo), round(hi) + 1):
        w = min(x + 0.5, hi) - max(x - 0.5, lo)
        k = round(x / 100) * 100
        weights[k] = weights.get(k, 0) + max(w, 0)
    real = [k for k, w in weights.items() if w > 1]
    return min(real), max(real)


def main():
    t = wikitext('Valuables')
    rows, traps = parse_rows(t), parse_traps(t)
    game = json.load(open(GAME, encoding='utf-8'))
    by_row = game_lookup(game, rows)
    imgs = thumbs({r['image'] for r in rows})
    items = []
    for r in rows:
        g = by_row[id(r)]
        name = r['item']
        page = r.get('itemLink', name)
        wiki_lo, wiki_hi, _ = classify(r['value'])
        wiki_mass = float(r.get('massValue') or r['mass'])
        surplus = 'Surplus' in g['prefab']
        orb = 'Enemy Valuable' in g['prefab']
        # Trap-Tabelle nennt manche Items anders ("Clown" statt "Clown Doll"), das Bild passt aber
        trap = traps.get(name) or traps.get(page) or next((t for t in traps.values() if t['img'] == r['image']), None)
        item = {
            'name': name, 'map': r['location'], 'img': imgs.get(r['image'], ''),
            'url': 'https://repo-2025horror.fandom.com/wiki/' + urllib.parse.quote(page.replace(' ', '_')),
            'trap': trap, 'prefab': g['prefab'],
            # Orbs und Geldsäcke liegen nicht in Spawn-Plätzen; ihre Größe im Wiki ist nur der Name
            'size': r['size'] if (orb or surplus) else g['size'], 'volume': g['volumeType'],
            'mass': g['mass'], 'dur': g['durability'], 'frag': g['fragility'], 'durab': g['durabilityValue'],
            'wiki': {},
        }
        if surplus:
            # Geldsäcke bekommen ihren Wert vom Extraction Point (Überschuss), das Preset ist Attrappe
            item.update(lo=wiki_lo, hi=wiki_hi, vt=None, presetLo=None, presetHi=None, surplus=True)
        else:
            lo, hi = spawn_range(g['valueMin'], g['valueMax'])
            item.update(lo=lo, hi=hi, vt=g['valueType'], presetLo=g['valueMin'], presetHi=g['valueMax'])
            # Das Wiki nennt mal den Preset-Bereich, mal die real vorkommenden Preise; beides ist richtig
            if (wiki_lo, wiki_hi) not in ((lo, hi), (g['valueMin'], g['valueMax'])):
                item['wiki']['value'] = r['value'].replace('�', '-')
        if abs(wiki_mass - g['mass']) > 0.01:
            item['wiki']['mass'] = r['mass']
        if not (orb or surplus) and r['size'] != g['size']:
            item['wiki']['size'] = r['size']
        if name == 'Egg':
            item['massNote'] = 'Das Ei wechselt beim Ausbrüten die Masse (2 / 1,75 / 1,25 / 1 kg); Betäuben rechnet mit den 3 kg aus dem Preset.'
        items.append(item)
    missing = [n for n in traps if not any(i['trap'] is traps[n] for i in items)]
    if missing:
        print('Traps ohne passendes Item:', missing)
    data = {
        'items': items,
        'types': [{'name': n, 'lo': lo, 'hi': hi, 'spawn': spawn_range(lo, hi)} for n, lo, hi in VALUE_TYPES],
        'enemies': game['enemies'], 'curves': game['curves'], 'wikiLevels': parse_level_tables(t),
    }
    corrected = [i for i in items if i['wiki']]
    print(len(corrected), 'Items mit Korrekturen gegenüber dem Wiki:',
          ', '.join(f"{i['name']} ({'/'.join(i['wiki'])})" for i in corrected))
    html = open(os.path.join(HERE, 'template.html'), encoding='utf-8').read()
    html = html.replace('/*DATA*/null', json.dumps(data, ensure_ascii=False))
    open(os.path.join(HERE, 'index.html'), 'w', encoding='utf-8').write(html)
    print(len(items), 'Valuables,', sum(1 for i in items if i['img']), 'Bilder,',
          sum(1 for i in items if i['trap']), 'Traps')


if __name__ == '__main__':
    main()
