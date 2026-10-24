# R.E.P.O. Valuables

Durchsuchbare und sortierbare Liste aller Valuables aus den Leveln von R.E.P.O., dazu ein Schadens-Rechner und ein Rechner für das Geld im Level. Preise, Massen, Haltbarkeit und Formeln stammen direkt aus den Spieldaten; wo das Wiki davon abweicht, ist das auf der Seite markiert.

**Seite öffnen:** https://sentic0mm.github.io/rv/

## Tabs

- **Valuables** – Suche (auch mit Map, z.B. `museum potion`; `/` springt ins Suchfeld, Enter öffnet den ersten Treffer), Filter nach Map, Value Type, Haltbarkeit, Größe und „nur Wiki-Korrekturen“, Sortieren und Gruppieren, Tabelle oder Karten, Detail-Fenster pro Item
- **Schadens-Rechner** – Item wählen und sehen, ab welchem Tempoverlust es bricht, was ein Stoß kostet, wie viel Schaden es Monstern und Spielern macht und ob es ein Monster betäubt; mit Simulation („Leichter Stoß“, „Monster getroffen“ …)
- **Level-Geld** – Level 1 bis 20+ wählen, Ziel des ersten Extraction Points eingeben, und die Seite rechnet wie das Spiel rückwärts aus, wie viel Geld im Level liegt; dazu eine Tabelle aller Level, Spiel gegen Wiki
- **Monster** – wie viele Monster pro Level und Schwierigkeitsstufe spawnen und mit welcher Chance jedes einzelne dabei ist (simulierte komplette Runs mit den Regeln aus `EnemyDirector`)

## Dateien

| Datei | Zweck |
|---|---|
| `index.html` | die fertige Seite (eine Datei, Bilder eingebettet, läuft auch offline) |
| `build.py` | lädt Wiki-Text und Bilder neu, mischt `spieldaten.json` dazu und baut `index.html` aus `template.html` |
| `template.html` | Oberfläche (HTML/CSS/JS), die Daten werden beim Bauen eingesetzt |
| `spieldaten.py` | liest Valuable-Presets, Monster, Monster-Spawnlisten und die Kurven aus den installierten Spieldateien nach `spieldaten.json` |
| `spieldaten.json` | die ausgelesenen Spielwerte |
| `serve.py`, `Server starten.bat` | optional: Seite vom eigenen PC aus teilen (Heimnetz, Radmin, mit `cloudflared.exe` im Ordner auch übers Internet) |

Neu bauen: `python build.py`

Nach einem Spiel-Update erst die Spielwerte neu auslesen (braucht `pip install UnityPy TypeTreeGeneratorAPI`, dauert ein paar Minuten):

```
python spieldaten.py
python build.py
```

## Quelle

Bilder, Namen und Beschreibungen stammen aus dem [R.E.P.O. Wiki](https://repo-2025horror.fandom.com/wiki/Valuables) (Fandom, Inhalte unter [CC BY-SA](https://www.fandom.com/licensing)). Zahlen und Formeln wurden aus dem Spiel ausgelesen (u.a. `PhysGrabObjectImpactDetector`, `RoundDirector`, `ValuableDirector`, `LevelGenerator`). R.E.P.O. gehört semiwork. Dies ist ein inoffizielles Fan-Projekt.
