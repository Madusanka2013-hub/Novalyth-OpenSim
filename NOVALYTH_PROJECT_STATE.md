# NOVALYTH PROJECT STATE

> **KANONISCHE PROJEKTÜBERGABE**
>
> Diese Datei ist die dauerhafte fachliche Wahrheit des Novalyth-OpenSim-Projekts.
> Jeder relevante Code-, Architektur-, Infrastruktur- oder Workflow-Commit muss
> diese Datei im selben Commit aktualisieren.
>
> Ein neuer Chat soll zuerst die automatisch erzeugte
> `NOVALYTH_CHAT_HANDOFF.txt` vollständig lesen und anschließend am Abschnitt
> **CURRENT NEXT ACTION** fortsetzen.

## 1. Harte Projektregeln

- LIVE OpenSim: `/nvme/opensim`
- LIVE darf durch Development-, Build-, Patch- oder Setup-Skripte niemals automatisch verändert werden.
- Kein Live-Install und kein Live-Restart ohne ausdrückliche Anweisung.
- Novalyth wird als eigener OpenSimulator-Source-Fork entwickelt.
- Source, Dev-Runtime und Live-Runtime bleiben strikt getrennt.
- Secrets, private SSH-Keys, Datenbankpasswörter, Runtime-Caches, Logs und Produktionsdaten werden nicht committed.
- Änderungen müssen Git-nachvollziehbar bleiben.
- Veröffentlichung nur nach erfolgreichem Release-Build.
- Bereits erledigte Schritte werden in einem neuen Chat nicht erneut durchgeführt, außer eine Prüfung zeigt, dass sie fehlen oder defekt sind.

## 2. Server-/Git-Struktur

### LIVE
```text
/nvme/opensim
```

### SOURCE
```text
/nvme/novalyth-opensim-src
```

### SETUP / HANDOFF
```text
/nvme/opensimsetup
/nvme/opensimsetup/NOVALYTH_CHAT_HANDOFF.txt
```

### Git
- Hauptbranch: `novalyth-main`
- Upstream: `https://github.com/opensim/opensim.git`
- Origin: `Madusanka2013-hub/Novalyth-OpenSim`
- Baseline-Tag: `novalyth-upstream-baseline-20260814-021756`
- Baseline-SHA: `78cb44c0c93dcb2af2b73b46f8cbdb56d2d7e0b7`
- GitHub SSH auf diesem Server: `ssh.github.com:443`
- Grund: ausgehendes `github.com:22` ist auf dem Server nicht erreichbar.
- Repository-spezifischer Deploy-Key; kein persönlicher GitHub-Token im Source.
- Entwicklergruppe: `novalyth-dev`
- Build-Serviceaccount: `novalythbuild` (besitzt KEINEN GitHub-Key)
- Push-Serviceaccount: `novalythgit` (besitzt den repository-spezifischen Deploy-Key)

## 3. Verifizierter Entwicklungsworkflow

```text
Source ändern
    ↓
NOVALYTH_PROJECT_STATE.md aktualisieren
    ↓
novalyth-save "NOVALYTH: Beschreibung"
    ↓
Pre-Commit:
exakter STAGED Git-Tree wird in TEMP exportiert
    ↓
./runprebuild.sh
dotnet build --configuration Release OpenSim.sln
    ↓
Build OK → Commit
Build Fehler → kein Commit
    ↓
NOVALYTH_CHAT_HANDOFF.txt aktualisieren
    ↓
zweiter isolierter Build des exakten Commits als novalythbuild
    ↓
Build OK
    ↓
novalythgit pusht exakt diesen Commit nach GitHub
    ↓
NOVALYTH_CHAT_HANDOFF.txt erneut aktualisieren
    ↓
GitHub Actions baut den Commit erneut
```

Wichtig:
- Build-Code läuft niemals mit dem GitHub-Key.
- Der Push-Serviceaccount führt keinen OpenSim-Buildcode aus.
- Linux-Buildoutputs werden nicht committed.

## 4. Chat-Kontinuität

### A. NOVALYTH_PROJECT_STATE.md
Liegt im GitHub-Repository und enthält:
- Regeln
- Architektur
- Entscheidungen
- erledigte Arbeit
- bekannte Probleme
- Roadmap
- CURRENT NEXT ACTION

### B. NOVALYTH_CHAT_HANDOFF.txt
Wird automatisch erzeugt und enthält zusätzlich:
- Timestamp
- Branch und HEAD-SHA
- Remotes
- Git-Status
- Baseline-Tags
- letzte 30 Commits
- Toolchain
- Read-only Prozesssnapshot
- vollständige PROJECT_STATE-Datei

Pfad:
```text
/nvme/opensimsetup/NOVALYTH_CHAT_HANDOFF.txt
```

Neuer Chat:
```text
Lies die Übergabe vollständig. Das ist der aktuelle Novalyth-Stand.
Prüfe zuerst CURRENT NEXT ACTION und setze genau dort weiter fort.
```

Manuelle Aktualisierung:
```bash
novalyth-handoff
```

## 5. Bereits erledigt

- GitHub-Repository für Novalyth eingerichtet.
- Repository-spezifischer Deploy-Key erzeugt.
- GitHub SSH auf Port 443 umgestellt und erfolgreich getestet.
- OpenSimulator Source nach `/nvme/novalyth-opensim-src` geklont.
- Branch `novalyth-main` erstellt.
- Remotes auf `upstream` und `origin` getrennt.
- Unveränderte OpenSim-Upstream-Baseline getaggt.
- Erster OpenSim-Release-Build auf der Baseline erfolgreich:
  - 0 Errors
  - 4 Warnings (CS9193)
- V3 stoppte danach beim ersten Commit, weil Linux-Buildoutputs unversioniert im Working Tree lagen.
- Ursache behoben: gezielte Ignore-Regeln + Build aus isoliertem staged tree.
- Permanentes Chat-/Projekt-Handoff eingeführt.

## 6. OpenSim Performance – bisherige Code-Audit-Erkenntnisse

### PERFORMANCE R1 – Asset Pipeline
- Viewer-Asset-CAPS haben im Standardcode eine kleine gemeinsame Worker-Pipeline.
- `GetAssetsModule` hat einen sehr kleinen gemeinsamen Asset-CAPS-Workerpool.
- `RegionAssetConnectorModule` hat ebenfalls kleine Local-/Remote-Asset-Workerqueues.
- Im CAPS-Assetpfad existiert eine blockierende Wait-Konstruktion ohne ausreichend robusten Timeout.
- Direkte Robust-/Asset-Backend-Tests waren schnell; ein wichtiger Engpass liegt daher wahrscheinlich im Region/CAPS/Queue-Pfad.
- R1-Ziele:
  - Workerzahlen konfigurierbar
  - garantierte Callbacks auch auf Fehlerpfaden
  - Timeout-/Cancellation-Schutz
  - Queue-/Latency-/Cache-Metriken
  - erst messen, dann Defaultwerte festlegen

### PERFORMANCE R2 – Inventory
- Inventory-Descendent-CAPS besitzen eine kleine gemeinsame Workerzahl.
- Mehrfach-Item-/Folder-Abfragen werden teilweise seriell statt als echte DB-Batches ausgeführt.
- Ziele:
  - echte Batch-Queries
  - konfigurierbare Worker
  - Skeleton-/Item-Caching
  - Metriken

### PERFORMANCE R3 – Appearance
- Im Appearance/Wearables-Pfad existiert ein fester Delay.
- Ziele:
  - Delay konfigurierbar/reduzieren
  - Bakes stärker persistieren/wiederverwenden
  - langfristig stärker Second-Life-artige zentrale Appearance-/Bake-Strategie

### PERFORMANCE R4 – Asset Edge
Langfristig spezialisierte Texture/Mesh/Asset-Auslieferung außerhalb des
Regionsprozesses, ohne Viewer-/Hypergrid-Protokolle zu brechen.

### PERFORMANCE R5 – Interest Management
Kamera-/Sichtbarkeits-/Prioritäts-orientierte Relevanz statt nur grober
Avatar-Distanz, um wahrgenommene Rez-Zeit und Netzlast zu verbessern.

## 7. Aktuelle Entwicklungsphasen

1. Fork / Git / Build-Sicherheit / Chat-Handoff
2. Isolierte Novalyth Dev-Runtime
3. PERFORMANCE R1 Asset Pipeline
4. PERFORMANCE R2 Inventory
5. PERFORMANCE R3 Appearance
6. R4 Asset Edge
7. R5 Interest Management
8. Scripts/Physics/Persistence nur nach Messdaten

## 8. CURRENT NEXT ACTION

Nach Abschluss dieses V5-Resume-Setups:

**Eine komplett getrennte Novalyth-Dev-Runtime aufbauen.**

Geplant:
- `/nvme/novalyth-opensim-dev`
- eigene Robust-Instanz
- eigene Region
- eigene MariaDB-Dev-Datenbanken
- eigene Ports ohne Kollision mit LIVE
- keine Produktionsdatenbank verwenden
- Baseline zuerst starten und messen
- danach PERFORMANCE R1 im Source beginnen

## 9. Pflegepflicht

Bei jedem relevanten Commit prüfen und aktualisieren:

1. Was wurde geändert?
2. Welche Entscheidung wurde getroffen?
3. Was ist jetzt abgeschlossen?
4. Welche neuen Fehler/Risiken gibt es?
5. Hat sich die Architektur geändert?
6. Was ist CURRENT NEXT ACTION?

Diese Datei muss den Zustand wiedergeben, den ein neuer Chat tatsächlich vorfindet.
