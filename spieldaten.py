# Liest die Werte, die build.py für Rechner und Korrekturen braucht, direkt aus den R.E.P.O.-Spieldateien
# und schreibt sie nach spieldaten.json:
#   - pro Map die Valuable-Liste mit Haltbarkeit, Value-Preset, Masse und Größe
#   - Monster (Gewichtsklasse, Leben, Treffer-Multiplikator)
#   - die Kurven aus RoundDirector/ValuableDirector (Haul Goal, Max Value, Mengen pro Größe)
# Braucht: pip install UnityPy TypeTreeGeneratorAPI   (Laufzeit ein paar Minuten)
# Aufruf:  python spieldaten.py ["C:/Pfad/zu/REPO"]
import contextlib, io, json, os, re, sys

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

GAME = sys.argv[1] if len(sys.argv) > 1 else r'C:/Program Files (x86)/Steam/steamapps/common/REPO'
DATA = os.path.join(GAME, 'REPO_Data')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'spieldaten.json')

WANT = {'Durability', 'Value', 'PhysAttribute', 'ValuableObject', 'Level', 'LevelValuables',
        'RoundDirector', 'ValuableDirector', 'EnemyParent', 'Enemy', 'EnemyHealth',
        'EnemyDirector', 'EnemySetup', 'RarityPreset'}
ENEMY = {'EnemyParent', 'Enemy', 'EnemyHealth'}
SIZES = ['tiny', 'small', 'medium', 'big', 'wide', 'tall', 'veryTall']
SIZE_NAMES = ['Tiny', 'Small', 'Medium', 'Big', 'Wide', 'Tall', 'Very Tall']
CURVES = ['haulGoalCurve1', 'haulGoalCurve2', 'totalMaxValueCurve1', 'totalMaxValueCurve2'] + \
         [s + 'MaxAmountCurve' + n for s in SIZES for n in '12'] + \
         [f'amountCurve{t}_{n}' for t in '123' for n in '12']


def quiet(fn, *a):
    # Der Generator meldet jede Klasse, die er nicht auflösen kann (u.a. HotReload) auf stdout
    with contextlib.redirect_stdout(io.StringIO()):
        return fn(*a)


def prefab_root(base):
    go = base.m_GameObject.deref().read()
    tr = next((c.component.deref().read() for c in go.m_Component
               if c.component.deref().type.name in ('Transform', 'RectTransform')), None)
    while tr is not None and tr.m_Father.m_PathID != 0:
        tr = tr.m_Father.deref().read()
    return tr.m_GameObject.deref().read().m_Name if tr else go.m_Name


def main():
    env = UnityPy.load(*[os.path.join(DATA, f) for f in
                         ('globalgamemanagers.assets', 'resources.assets', 'sharedassets0.assets', 'level0')])
    version = next(iter(env.files.values())).unity_version

    # Durchgang 1 ohne Typtree-Generator: mit Generator scheitert schon das Lesen des Skriptnamens
    picked, names = [], {}
    for o in env.objects:
        if o.type.name != 'MonoBehaviour' or o.assets_file.name == 'globalgamemanagers.assets':
            continue
        try:
            base = o.read(check_read=False)
            key = (o.assets_file.name, base.m_Script.m_FileID, base.m_Script.m_PathID)
            if key not in names:
                names[key] = base.m_Script.deref().read().m_ClassName
        except Exception:
            continue
        cls = names[key]
        if cls not in WANT:
            continue
        extra = {}
        if cls == 'ValuableObject':
            extra['go'] = base.m_GameObject.deref().read().m_Name
        if cls in ENEMY:
            extra['root'] = prefab_root(base)
        picked.append((o, cls, extra))
    print(len(picked), 'Objekte gefunden')

    # Durchgang 2: nur die Treffer mit Typtrees aus den Spiel-DLLs lesen
    gen = TypeTreeGenerator(version)
    quiet(gen.load_local_game, GAME)
    env.typetree_generator = gen
    objs = {}
    for o, cls, extra in picked:
        d = quiet(o.read_typetree)
        d.update(extra)
        d['_file'] = o.assets_file
        objs.setdefault(cls, {})[(o.assets_file, o.path_id)] = d

    def deref(src_file, ptr, cls):
        if ptr['m_PathID'] == 0:
            return None
        f = src_file if ptr['m_FileID'] == 0 else src_file.externals[ptr['m_FileID'] - 1].path
        f = f if isinstance(f, str) else f.name
        return next((d for (af, pid), d in objs[cls].items()
                     if af.name == os.path.basename(f) and pid == ptr['m_PathID']), None)

    def preset_name(d, prefix):
        return d['m_Name'].replace(prefix, '').strip()

    # Valuables: Name des Prefabs -> Werte (Prefab-Namen sind in den Level-Listen eindeutig)
    valuables = {}
    for (af, _), d in objs['ValuableObject'].items():
        du = deref(af, d['durabilityPreset'], 'Durability')
        va = deref(af, d['valuePreset'], 'Value')
        pa = deref(af, d['physAttributePreset'], 'PhysAttribute')
        entry = {
            'durability': preset_name(du, 'Durability'), 'fragility': du['fragility'], 'durabilityValue': du['durability'],
            'valueType': preset_name(va, 'Value'), 'valueMin': va['valueMin'], 'valueMax': va['valueMax'],
            'mass': round(pa['mass'], 3), 'volumeType': SIZE_NAMES[d['volumeType']],
        }
        valuables.setdefault(d['go'], []).append(entry)

    def one(prefab):
        variants = {json.dumps(v, sort_keys=True) for v in valuables[prefab]}
        if len(variants) > 1:
            raise SystemExit(f'{prefab}: mehrere Prefabs mit unterschiedlichen Werten')
        return dict(valuables[prefab][0])

    level_names = {}
    for (af, _), d in objs['Level'].items():
        if 'Arena' in d['m_Name']:  # die Arena mischt die Listen aller Maps
            continue
        for p in d.get('ValuablePresets', []):
            lv = deref(af, p, 'LevelValuables')
            if lv:
                level_names.setdefault(lv['m_Name'], []).append(d['NarrativeName'])
    maps = {}
    for d in objs['LevelValuables'].values():
        used = level_names.get(d['m_Name'], [])
        if len(used) != 1:  # "Generic" gehört nur zur Arena, taucht hier also nicht auf
            continue
        items = []
        for size, size_name in zip(SIZES, SIZE_NAMES):
            for ref in d[size]:
                e = one(ref['prefabName'])
                e.update(prefab=ref['prefabName'], size=size_name)
                items.append(e)
        maps[used[0]] = items

    special = {}
    for prefab in ('Enemy Valuable - Small', 'Enemy Valuable - Medium', 'Enemy Valuable - Big',
                   'Surplus Valuable - Small', 'Surplus Valuable - Medium', 'Surplus Valuable - Big'):
        special[prefab] = one(prefab)

    enemies = {}
    for cls in ENEMY:
        for d in objs[cls].values():
            e = enemies.setdefault(d['root'], {})
            if cls == 'EnemyParent':
                e.update(name=d['enemyName'], difficulty=d['difficulty'] + 1)
            elif cls == 'Enemy':
                e['weight'] = ['VeryLight', 'Light', 'Medium', 'Heavy', 'VeryHeavy'][d['Type']]
            else:
                e.update(health=d['health'], objectHurt=bool(d['objectHurt']),
                         objectHurtMultiplier=d['objectHurtMultiplier'], objectHurtStun=bool(d['objectHurtStun']))
    for root, e in enemies.items():
        e['prefab'] = root
    enemies = sorted((e for root, e in enemies.items() if root.startswith('Enemy - ') and 'name' in e),
                     key=lambda e: e['name'])

    # Welche Monster-Setups der EnemyDirector pro Schwierigkeitsstufe ziehen kann
    (director_key, director), = [(k, d) for k, d in objs['EnemyDirector'].items()]
    spawn_tiers = []
    for tier in '123':
        setups = []
        for ptr in director[f'enemiesDifficulty{tier}']:
            s = deref(director_key[0], ptr, 'EnemySetup')
            if s is None:
                raise SystemExit(f'EnemySetup {ptr} nicht gefunden')
            rarity = deref(s['_file'], s['rarityPreset'], 'RarityPreset') if s['rarityPreset']['m_PathID'] else None
            setups.append({
                'name': s['m_Name'].replace('Enemy - ', '').strip(),
                'spawns': [p['prefabName'] for p in s['spawnObjects']],
                'levelsCompletedCondition': bool(s['levelsCompletedCondition']),
                'levelsCompletedMin': s['levelsCompletedMin'], 'levelsCompletedMax': s['levelsCompletedMax'],
                'chance': rarity['chance'] if rarity else 100.0,
                'rarity': rarity['m_Name'].replace('Rarity - ', '').strip() if rarity else None,
                'runsPlayed': s['runsPlayed'],
            })
        spawn_tiers.append(setups)

    curves = {}
    for cls in ('RoundDirector', 'ValuableDirector', 'EnemyDirector'):
        (d,) = objs[cls].values()
        for k, v in d.items():
            if k in CURVES:
                curves[k] = [{x: kf[x] for x in ('time', 'value', 'inSlope', 'outSlope', 'weightedMode', 'inWeight', 'outWeight')}
                             for kf in v['m_Curve']]
            elif re.fullmatch(r'(tiny|small|medium|big|wide|tall|veryTall)Chance', k):
                curves[k] = v

    out = {'unityVersion': version, 'maps': maps, 'special': special, 'enemies': enemies,
           'enemySpawnTiers': spawn_tiers, 'curves': curves}
    json.dump(out, open(OUT, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print(OUT, '-', {m: len(v) for m, v in maps.items()}, len(enemies), 'Monster,',
          [len(t) for t in spawn_tiers], 'Spawn-Setups je Stufe,', len(curves), 'Kurven/Werte')


if __name__ == '__main__':
    main()
