# Kopf-Mods für R.E.P.O.

Drei BepInEx-Mods rund um den eigenen Kopf nach dem Tod. Fertige DLLs liegen jeweils in `fuer_freunde/`, Bauen mit `bauen.bat` (dotnet, Spiel unter Steam, Thunderstore-Profil „Default“).

| Mod | Taste | Host braucht Mod? | Was |
|---|---|---|---|
| **KopfTeleport** | F8 an/aus | nein | Kurz vor dem Tod zum aktiven Extraction Point teleportieren (sonst Truck, wenn alle fertig, sonst Cart) – der Kopf landet gleich dort |
| | F7 | ja | Kopf nachträglich wegteleportieren, wenn man schon tot ist |
| **KopfZieher** (experimentell) | F9 | nein | Zieht den toten Kopf mit dem eigenen Greifer über das NavMesh bis ins Ziel; läuft, bis er da ist (F9 nochmal = abbrechen) |
| **KopfKamera** (Debug) | F10 | nein | 3rd-Person-Kamera hinter dem eigenen Kopf, automatisch während KopfZieher zieht; Debug-Text mit Tempo und Restweg |

## Warum was geht

- `PhysGrabObject.Teleport()` und `SetPositionRPC` sind seit dem letzten Update für Gegenstände **MasterOnly** – Köpfe kann nur der Host direkt versetzen (deshalb braucht F7 die Mod beim Host).
- Den **eigenen Tod** meldet das eigene Spiel, und der Host setzt den Kopf dorthin, wo der Spieler bei ihm steht (`deathHeadPosition`). Die eigene Position schickt man selbst → Teleport kurz vor dem Tod geht ohne Host-Mod.
- `GrabStartedRPC` / `GrabLinkRPC` prüfen nur `OwnerOnlyRPC` des Greifers, nicht Tod, Entfernung oder Sichtlinie. Der Zielpunkt lässt sich über zwei `GrabLinkRPC` mit gleicher/neuer Revision setzen → KopfZieher.
