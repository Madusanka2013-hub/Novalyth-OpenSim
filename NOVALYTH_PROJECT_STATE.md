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

## DEV-RUNTIME STATUS

Die isolierte Novalyth-Dev-Runtime ist eingerichtet:

- Runtime: `/nvme/novalyth-opensim-dev`
- Runtime-Benutzer: `opensimdev`
- Robust-Datenbank: `novalyth_robust_dev`
- Region-Datenbank: `novalyth_region_dev`
- DB-Benutzer: `novalyth_dev`
- DB-Secret: `/root/.novalyth-opensim-dev-db.env` (`root:root`, `0600`)
- Robust Public: `8102`
- Robust Private: `8103`
- Region-Port: `9100`
- Testregion: `Novalyth Dev Lab`
- Grid-Position: `1100,1100`
- Region-Größe: `256x256`
- Dev-Konfiguration verwendet `GridHypergrid.ini`.
- Firewall und nginx wurden nicht verändert.
- LIVE `/nvme/opensim` wurde nicht verändert.
- Robust und Region wurden noch nicht gestartet.
- Auskommentierte OpenSim-Beispiel-ConnectionStrings mit `Database=opensim`
  sind weiterhin in den offiziellen Example-basierten INIs vorhanden, aber
  **nicht aktiv**. Die aktiven ConnectionStrings zeigen ausschließlich auf
  `novalyth_robust_dev` bzw. `novalyth_region_dev`.

## ROBUST BASELINE STATUS

**Robust-Baseline erfolgreich gestartet und geprüft (2026-08-14).**

- Prozess läuft als Dev-Runtime aus `/nvme/novalyth-opensim-dev/bin`.
- OpenSim/Robust-Version: `OpenSim 0.9.3.1 Nessie Dev`.
- Aktive Hypergrid-Werte:
  - `HomeURI = "${Const|BaseURL}:${Const|PublicPort}"`
  - `GatekeeperURI = "${Const|BaseURL}:${Const|PublicPort}"`
- Robust Public: `8102`
- Robust Private: `8103`
- Beide Robust-Listener binden aktuell auf `0.0.0.0`.
- **Sicherheitsregel: Port 8103 niemals öffentlich in der Firewall freigeben.**
- `novalyth_robust_dev` wurde erfolgreich migriert.
- Gatekeeper, UserAgent, HGFriends, HG Inventory und HG Asset wurden im aktuellen Start erfolgreich geladen.
- Der erste Startfehler durch fehlende `HomeURI`/`GatekeeperURI` wurde ausschließlich in der Dev-Runtime-Konfiguration korrigiert.
- LIVE `/nvme/opensim` blieb unverändert.

## PUBLIC DEV ACCESS STATUS

Öffentlicher DEV-Viewer-Zugang ist vorbereitet:

- Public DEV Robust: `http://23.88.2.228:8102`
- Robust Private: `8103` — **explizit per UFW DENY und niemals öffentlich**
- Region `Novalyth Dev Lab`: `23.88.2.228:9100`
- Region `InternalAddress = 0.0.0.0` für externes UDP
- Region `ExternalHostName = 23.88.2.228`
- Region-Prozess benutzt für private Grid-Dienste weiterhin `http://127.0.0.1:8103`
- UFW erlaubt nur:
  - `8102/tcp`
  - `9100/tcp`
  - `9100/udp`
- LIVE `/nvme/opensim` bleibt unverändert.

## 8. CURRENT NEXT ACTION

**Öffentlichen DEV-Baseline-Test durchführen.**

1. DEV-Robust starten: `novalyth-dev-robust`
2. DEV-Region starten: `novalyth-dev-region`
3. Listener prüfen:
   - TCP `8102`
   - TCP `8103`
   - TCP/UDP `9100`
4. Sicherstellen, dass UDP `9100` nicht mehr nur auf `127.0.0.1` gebunden ist.
5. Von extern prüfen:
   - Login/Grid URI: `http://23.88.2.228:8102`
6. Mit dem DEV-Account in Firestorm einloggen.
7. Region `Novalyth Dev Lab` betreten und Basisfunktionen testen.
8. Verifizieren, dass `8103` extern nicht erreichbar ist.
9. Erst nach erfolgreichem externem Baseline-Test Messwerte erfassen.
10. Danach **PERFORMANCE R1 – Asset Pipeline** beginnen.

## 9. Pflegepflicht

Bei jedem relevanten Commit prüfen und aktualisieren:

1. Was wurde geändert?
2. Welche Entscheidung wurde getroffen?
3. Was ist jetzt abgeschlossen?
4. Welche neuen Fehler/Risiken gibt es?
5. Hat sich die Architektur geändert?
6. Was ist CURRENT NEXT ACTION?

Diese Datei muss den Zustand wiedergeben, den ein neuer Chat tatsächlich vorfindet.


## INFRASTRUKTUR-ÄNDERUNG 2026-08-14 – PHASE 2

- Phase 2 abgeschlossen: isolierte Dev-Runtime, getrennte Robust-/Region-DBs, dedizierte Ports und Testregion vorbereitet; erster kontrollierter Baseline-Start steht aus.


## BASELINE-MEILENSTEIN 2026-08-14 – ROBUST

- Robust-Dev-Baseline erfolgreich validiert; HG-Dienste laden fehlerfrei, Listener 8102/8103 binden auf 0.0.0.0; 8103 bleibt strikt privat.

## PERFORMANCE R1 – ASSET PIPELINE

### R1 Stage 1 – Safety and configurability

Implemented in source, pending DEV deployment/test:

- `GetAssetsHandler` no longer performs an unlimited `ManualResetEventSlim.Wait()`.
- Asset fetch timeout is configurable through `[ClientStack.LindenCaps] Cap_AssetFetchTimeoutMs`.
- Default timeout is deliberately conservative at 15000 ms for the first DEV measurement.
- Timeout returns HTTP 504 instead of permanently consuming a CAPS asset worker.
- Synchronous asset-service exceptions return HTTP 503 and are logged.
- CAPS asset worker concurrency is configurable with `Cap_AssetWorkers`; default remains upstream-compatible at 3.
- Region local asset worker concurrency is configurable with `[AssetService] LocalAssetWorkers`; default remains 2.
- Region HG/remote asset worker concurrency is configurable with `[AssetService] RemoteAssetWorkers`; default remains 2.
- `RegionAssetConnector.AssetRequestProcessor` now resolves queued callbacks with `null` even when the underlying asset fetch throws, rather than silently swallowing the exception and leaving duplicate/coalesced requests stuck forever.
- No worker count has been increased yet. We measure first.
- Important corrected finding: ObjectJobEngine constructor values 1000/2000 are thread hold times in milliseconds, not queue limits. The underlying `BlockingCollection` is currently unbounded.
- LIVE `/nvme/opensim` is not modified by this source patch.

### R1 next

1. Build/commit through `novalyth-save`.
2. Deploy this exact commit only to `/nvme/novalyth-opensim-dev`.
3. Keep defaults 3 CAPS / 2 local / 2 remote for first A/B comparison.
4. Add queue-depth, timeout, latency and in-flight metrics.
5. Add controlled backpressure/bounded pending work after baseline data.
6. Only then test higher worker concurrency.

### Service separation roadmap

OpenSim/Robust already exposes independent service connectors and per-service URIs. Novalyth will use those boundaries rather than inventing incompatible protocols.

Planned coarse-grained services after R1 baseline stability:

1. **Novalyth Asset Service / Asset Edge**
2. **Novalyth Inventory Service**
3. **Novalyth Login Edge** — LLLogin/GridInfo public entry point
4. **Novalyth Identity/Core** — Authentication, UserAccount, GridUser, Presence, Friends, AgentPreferences
5. **Novalyth Grid/HG** — Grid, map, Gatekeeper, UserAgent and HG-facing connectors
6. Regions remain independent simulator processes.

Initial split stays on the same physical server over loopback/private ports so process isolation is gained without adding unnecessary network latency.

### R1 Stage 2 – Asset Pipeline Instrumentation

Implemented in source:

- Added thread-safe `NovalythAssetPipelineMetrics`.
- Added region console command `show asset pipeline`.
- Added region console command `reset asset pipeline`.
- Measures CAPS requests, current/peak pending queue and queue-wait p50/p95/p99 buckets.
- Measures end-to-end CAPS backend fetch in-flight/peak, p50/p95/p99, timeouts, errors and not-found.
- Measures RegionAssetConnector memory hits and duplicate-request coalescing.
- Measures local and remote/HG pending/peak, in-flight/peak, completed, errors, not-found and fetch p50/p95/p99.
- Worker defaults remain CAPS=3, local=2, remote=2.
- No concurrency tuning or backpressure change is part of Stage 2.

Measurement procedure after DEV deployment:

1. `reset asset pipeline`
2. Perform a controlled Firestorm cold-load test.
3. Let scene/avatar rez settle.
4. `show asset pipeline`
5. Record results.
6. Repeat as warm-load test.
7. Increase concurrency only if queue wait/peak proves it is the bottleneck.
