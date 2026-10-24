# R.E.P.O. Valuables

Durchsuchbare und sortierbare Liste aller Valuables aus den Leveln von R.E.P.O. – mit Map, Value Type, Preisspanne, Größe, Masse, Schaden und Trap-Infos.

**Seite öffnen:** https://sentic0mm.github.io/rv/

## Funktionen

- Suche nach Item-Namen (auch kombiniert mit der Map, z.B. `museum potion`), `/` springt ins Suchfeld, Enter öffnet den ersten Treffer
- Filter nach Map, Value Type und Größe
- Sortieren nach Name, Wert, Value Type, Map, Größe, Masse oder Schaden; Gruppieren nach Map, Value Type oder Größe
- Tabelle oder Karten, Detail-Fenster pro Item
- Eine einzige HTML-Datei, Bilder eingebettet – funktioniert auch offline

## Dateien

| Datei | Zweck |
|---|---|
| `index.html` | die fertige Seite |
| `build.py` | lädt die Daten neu aus dem Wiki und baut `index.html` aus `template.html` |
| `template.html` | Oberfläche (HTML/CSS/JS), die Daten werden beim Bauen eingesetzt |
| `serve.py`, `Server starten.bat` | optional: Seite vom eigenen PC aus teilen (Heimnetz, Radmin, mit `cloudflared.exe` im Ordner auch übers Internet) |

Neu bauen: `python build.py`

## Quelle

Daten und Bilder stammen aus dem [R.E.P.O. Wiki](https://repo-2025horror.fandom.com/wiki/Valuables) (Fandom, Inhalte unter [CC BY-SA](https://www.fandom.com/licensing)). R.E.P.O. gehört semiwork. Dies ist ein inoffizielles Fan-Projekt.
