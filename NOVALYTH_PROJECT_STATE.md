# NOVALYTH PROJECT STATE


## PERMANENT ARCHITECTURE CHARTER — NOVALYTH IS THE PRODUCT

**Status: PERMANENT / applies to every future chat and every architecture decision.**

Novalyth is intended to become a **standalone grid platform**, not an "OpenSim grid with patches".
OpenSimulator is only the current bootstrap/runtime kernel that lets the project start from a
working SL-compatible base. Its responsibilities are to be replaced piece by piece until the
executable grid system, services, process names, configuration surface, APIs and Novalyth-owned
code are no longer OpenSim-owned.

### Non-negotiable architecture rules

1. **OpenSim is bootstrap, not target architecture.** New systems must not deepen dependency on
   legacy OpenSim service architecture when a clean Novalyth boundary can be introduced.
2. **Second Life is the primary behavioral/protocol reference.** For every major subsystem,
   inspect documented Second Life behavior/protocols and the open viewer/Firestorm implementation
   first. Preserve native Firestorm/SL interoperability wherever practical.
3. **Match SL, then improve it.** Novalyth should reproduce the useful SL behavior, then improve
   performance, batching, caching, concurrency, fairness, availability, operability and scaling
   where modern architecture allows.
4. **New ownership uses `Novalyth.*`.** New services/protocol layers should use Novalyth namespaces
   and concepts. Existing `OpenSim.*` code is compatibility/bootstrap code scheduled for gradual
   retirement; do not merely rename old classes and call that migration.
5. **Regions do not own central state.** Inventory, assets, appearance/SSA, identity, presence,
   groups, messaging, map and similar grid-wide state belong in dedicated Novalyth services.
6. **Public SL/Firestorm compatibility, private Novalyth internals.** Viewer-facing protocol and
   capability semantics remain SL-compatible; internal APIs, queues, caches and storage are
   Novalyth-owned and may be better than Linden Lab/OpenSim implementations.
7. **Core ports stay private.** Public traffic terminates at controlled edge/CAPS/login endpoints.
   Raw service ports are internal only.
8. **No fake migration by branding.** `OpenSim.dll`, `Robust.dll` and `OpenSim.*` assemblies are
   removed/replaced only as their responsibilities genuinely move into Novalyth-owned hosts.
9. **End-state process model:** `Novalyth.RegionHost`, `Novalyth.GridHost`,
   `Novalyth.AssetCore`, `Novalyth.InventoryCore`, `Novalyth.AppearanceCore`,
   `Novalyth.LoginCore`, `Novalyth.PresenceCore`, `Novalyth.GridCore`,
   `Novalyth.Caps`, `Novalyth.Protocols`, `Novalyth.Storage` (names may evolve,
   ownership principle does not).
10. **Licensing remains correct.** Existing upstream copyright/license notices remain where
    required. Product identity and runtime ownership can become fully Novalyth without erasing
    third-party attribution obligations.

### Migration direction

`OpenSim bootstrap -> service extraction -> Novalyth-owned protocol/service boundaries ->
Novalyth hosts -> legacy OpenSim compatibility island shrinks -> standalone Novalyth grid`.

This charter must be carried into the generated chat handoff and treated as a hard constraint
unless the project owner explicitly changes it.


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

### R1 Stage 3 – Event-driven Asset CAPS Response Wake

Status: **DEV-VALIDIERT / ERFOLGREICH**

Source commit:

`76c3b568873edfa63fb9648e1587d1d7e316395b`

Implemented:

- Added opt-in `PollServiceEventArgs.UseResponseReadyNotification`.
- Added an event-driven request state machine to prevent duplicate scheduling during response-ready/worker races.
- Added immediate `ResponseReady` wake-up support in `PollServiceRequestManager`.
- Existing non-opt-in poll services retain the legacy 100 ms retry cadence unchanged.
- The 100 ms watcher remains only as timeout/disconnect/lost-notification fallback for opt-in requests.
- `GetAssetsModule` opts asset CAPS into the wake-up path only when `[ClientStack.LindenCaps] Cap_AssetResponseWake = true`.
- `Cap_AssetResponseWake` defaults to `false` in source for upstream-compatible behavior.
- DEV is explicitly configured with `Cap_AssetResponseWake = true`.
- CAPS asset worker count remains `3`.
- LIVE `/nvme/opensim` was not modified.

DEV startup validation:

- `[GETASSETS]: CAPS asset workers=3, fetch timeout=15000 ms`
- `[GETASSETS]: asset response wake=enabled`

Fixed benchmark workload:

- 500 distinct textures
- 500 distinct meshes
- 1000 requests total
- client concurrency 32
- identical asset list before/after Stage 3

Stage-2 baseline, 3 workers, legacy PollService response polling:

- total: 3.224 s
- throughput: 310.15 req/s
- client p50: 100.20 ms
- client p95: 111.13 ms
- client p99: 200.68 ms
- client max: 217.20 ms

Stage-3 result, 3 workers, event-driven response wake:

- total: 0.506 s
- throughput: 1977.64 req/s
- client p50: 12.12 ms
- client p95: 49.98 ms
- client p99: 77.95 ms
- client max: 88.84 ms
- 997 HTTP 200, 3 HTTP 404
- 0 exceptions

Stage-3 server metrics:

- caps requests: 1000
- caps peak pending: 29
- caps peak inflight: 3
- backend started/completed: 1000/1000
- backend timeouts/errors/notfound: 0/0/0
- backend p50/p95/p99: <=5 / <=5 / <=5 ms
- region-local peak pending: 3
- region-local peak inflight: 2
- region-local completed: 999
- region-local errors/notfound: 0/0
- region-local p50/p95/p99: <=5 / <=5 / <=5 ms
- remote/HG path unused in this benchmark

Measured improvement versus the Stage-2 3-worker baseline:

- total wall time reduced by ~84.3%
- throughput improved by ~6.38x
- client p50 reduced by ~87.9%
- client p95 reduced by ~55.0%
- client p99 reduced by ~61.2%
- client max reduced by ~59.1%

Conclusion:

The legacy PollService response-retry cadence was a real Asset CAPS latency floor.
The Stage-3 event-driven wake path removes that floor without changing the global
PollService behavior and without introducing observed request errors or timeouts.
Raising CAPS workers from 3 to 8 is still rejected based on the previous A/B result.

CURRENT NEXT ACTION:

R1 Stage 4 – Asset queue safety and fairness.

Goals:

1. Keep `Cap_AssetWorkers = 3` and `Cap_AssetResponseWake = true` in DEV.
2. Do not globally modify PollService retry timing.
3. Add bounded capacity/backpressure to the Asset CAPS work queue instead of allowing unbounded accumulation.
4. Add queue-full/rejected/backpressure metrics.
5. Preserve timeout/error response guarantees.
6. Design request fairness so one viewer/user cannot monopolize the entire Asset CAPS queue.
7. Benchmark Stage 4 using the same fixed 1000-asset workload plus a higher-concurrency stress test.
8. Only after measurements decide whether RegionAsset local workers need separate tuning.
9. LIVE remains untouched until DEV validation is complete.

### R1 Stage 4 – Asset Queue Safety + Per-Agent Fairness

Status: **DEV VALIDATED**

Design decision:

Stage 4 does not modify `ObjectJobEngine` globally. Asset CAPS receives a
Novalyth-specific admission guard before requests enter the existing worker pool.
This bounds the effective Asset CAPS queue while avoiding behavioral changes in
unrelated OpenSim modules that also use `ObjectJobEngine`.

New source configuration:

- `Cap_AssetMaxOutstanding = 0`
- `Cap_AssetMaxOutstandingPerAgent = 0`

`0` means disabled, preserving upstream-compatible behavior by default.
DEV validation will explicitly use:

- `Cap_AssetMaxOutstanding = 256`
- `Cap_AssetMaxOutstandingPerAgent = 64`

The outstanding count includes queued + currently processing Asset CAPS jobs.
With 3 asset workers, this also bounds the underlying ObjectJobEngine queue.

Behavior:

- Requests inside capacity continue through the existing 3-worker path.
- A single agent cannot occupy more than the configured per-agent limit.
- Global capacity prevents unbounded aggregate Asset CAPS accumulation.
- Rejected requests receive immediate HTTP 503 with `Retry-After: 1`.
- Stage-3 event-driven response wake remains unchanged.
- Existing PollService behavior remains unchanged.
- No worker-count increase.
- No RegionAsset local-worker change.

New console commands:

- `show asset queue`
- `reset asset queue`

Metrics include:

- current / peak outstanding
- active agents
- peak active agents
- peak per-agent outstanding
- accepted / released
- global capacity rejects
- per-agent fairness rejects
- worker enqueue failures

CURRENT NEXT ACTION:

1. Deploy the exact Stage-4 commit to DEV only.
2. Keep `Cap_AssetWorkers = 3`.
3. Keep `Cap_AssetResponseWake = true`.
4. Enable DEV guard at 256 global / 64 per agent.
5. Run the fixed 1000-asset benchmark at concurrency 32; expected: zero guard rejects and no material Stage-3 regression.
6. Run a stress benchmark at concurrency 256; expected: controlled per-agent HTTP 503 backpressure instead of unbounded queue growth.
7. Inspect `show asset pipeline` and `show asset queue`.
8. Only after these measurements decide whether additional fairness scheduling or RegionAsset local-worker tuning is justified.
9. LIVE `/nvme/opensim` remains untouched.

### R1 Stage 4.1 – Work-Conserving Fair Asset Dispatch

Status: **SOURCE IMPLEMENTATION – DEV TEST PENDING**

Reason for Stage 4.1:

Stage-4 stress validation proved the hard per-agent guard works exactly:
`peak_per_agent=64`, `reject_agent=935`, `enqueue_fail=0`, final
`outstanding=0`.

However, official Firestorm texture-fetch code treats ordinary HTTP 503 asset
responses as a failed texture fetch. Its explicit Retry-After retry policy is
used for server-bake fetches, not ordinary texture fetches. Therefore a hard
per-agent 503 limit is useful as an emergency/abuse control but is not the
desired normal fairness mechanism.

Stage 4.1 keeps the bounded admission guard and adds a work-conserving
round-robin dispatcher in front of the existing 3 Asset CAPS workers.

Behavior:

- Global outstanding capacity remains a hard emergency bound.
- `Cap_AssetMaxOutstandingPerAgent` remains available as an optional hard
  emergency/abuse limit, but DEV validation will set it to `0`.
- Normal per-agent fairness is scheduling-based, not rejection-based.
- Each active agent has one round-robin token regardless of queue length.
- With one active agent, that agent may use all 3 Asset CAPS workers.
- With multiple active agents, dispatch rotates across active agent queues.
- At most `Cap_AssetWorkers` requests are dispatched into ObjectJobEngine at
  once, so its internal unbounded collection no longer accumulates the entire
  Asset CAPS backlog.
- Stage-3 event-driven PollService response wake remains unchanged.

DEV target config for Stage 4.1:

- `Cap_AssetWorkers = 3`
- `Cap_AssetResponseWake = true`
- `Cap_AssetMaxOutstanding = 256`
- `Cap_AssetMaxOutstandingPerAgent = 0`

Validation plan:

1. Deploy Stage 4.1 to DEV only.
2. Run fixed 1000-asset benchmark at concurrency 32.
3. Run fixed 1000-asset benchmark at concurrency 256.
4. At c256, expect no per-agent 503 rejections and fair-dispatch
   `peak_inflight <= 3`; global outstanding must never exceed 256.
5. Run a separate c512 emergency-cap test only to prove the global hard bound.
6. Perform a multi-agent fairness test before calling Stage 4 production-ready.
7. LIVE remains untouched.

## R1 Stage 4.1 DEV Validation — 2026-08-14

Status: **DEV VALIDATED**

Validated source commit:
`1ac837b3a9ba5814aa02beab7a5e9dd45d85027b`

DEV configuration used:

```ini
Cap_AssetWorkers = 3
Cap_AssetFetchTimeoutMs = 15000
Cap_AssetResponseWake = true
Cap_AssetMaxOutstanding = 256
Cap_AssetMaxOutstandingPerAgent = 0
```

### C32 normal-load benchmark

- Requests: 1000
- HTTP 200/206: 997
- HTTP 404: 3
- HTTP 503: 0
- Exceptions: 0
- Total: 0.371 s
- Throughput: 2693.45 req/s
- Client p50: 4.37 ms
- Client p95: 14.75 ms
- Client p99: 21.39 ms
- Client max: 57.35 ms
- Guard: accepted=1000, released=1000, reject_global=0, reject_agent=0, enqueue_fail=0
- Fair dispatch: peak_queued=17, peak_inflight=3, dispatched=1000, worker_enqueue_fail=0
- Final gauges: outstanding=0, queued=0, inflight=0

Verdict: normal-load path is transparent and faster than the previous Stage 3 reference run.

### C256 bounded-queue benchmark

- Requests: 1000
- HTTP 200/206: 997
- HTTP 404: 3
- HTTP 503: 0
- Exceptions: 0
- Total: 1.147 s
- Throughput: 871.75 req/s
- Client p50: 105.84 ms
- Client p95: 828.36 ms
- Client p99: 882.59 ms
- Client max: 954.66 ms
- Guard: peak_outstanding=256, accepted=1000, released=1000, reject_global=0, reject_agent=0, enqueue_fail=0
- Fair dispatch: peak_queued=253, peak_inflight=3, dispatched=1000, worker_enqueue_fail=0
- Final gauges: outstanding=0, queued=0, inflight=0
- Backend and region-local p50/p95/p99 remained <=5 ms; the long tail is controlled queue wait.

Verdict: a single agent may queue up to the global bound without routine per-agent 503 rejection, while only three jobs are admitted to the legacy ObjectJobEngine at once.

### C512 emergency-global-cap benchmark

- Requests: 1000
- HTTP 200/206: 356
- HTTP 404: 1
- HTTP 503: 643
- Exceptions: 0
- Total: 1.282 s
- Throughput: 780.05 req/s
- Client p50: 77.31 ms
- Client p95: 1030.81 ms
- Client p99: 1050.49 ms
- Client max: 1074.27 ms
- Guard: peak_outstanding=256, accepted=357, released=357, reject_global=643, reject_agent=0, enqueue_fail=0
- Fair dispatch: peak_queued=253, peak_inflight=3, dispatched=357, worker_enqueue_fail=0
- Final gauges: outstanding=0, queued=0, inflight=0

Verdict: the global emergency cap is effective and prevents unbounded asset backlog growth. HTTP 503 is reserved for this emergency-overload path, not routine per-agent fairness.

### Two-agent fairness benchmark

Two distinct logged-in CAPS agents were loaded concurrently with 500 requests each and client concurrency 64 per agent.

Agent 1:
- HTTP 200/206: 499
- HTTP 404: 1
- Exceptions: 0
- p50: 53.88 ms
- p95: 524.51 ms
- p99: 546.93 ms
- max: 626.92 ms

Agent 2:
- HTTP 200/206: 498
- HTTP 404: 2
- Exceptions: 0
- p50: 55.31 ms
- p95: 482.77 ms
- p99: 517.52 ms
- max: 535.67 ms

Combined:
- Total time: 0.919 s
- HTTP 503: 0
- Exceptions: 0
- P95 fairness ratio (max/min): **1.086x**

Verdict: external two-agent behavior is balanced and work-conserving; neither client showed starvation or routine overload rejection.

Note: the post-run `show asset queue` server-side `peak_agents=2` counter was not captured before the DEV process was shut down. The two distinct agent UUIDs/CAPS endpoints and their concurrent benchmark results were captured, so the functional multi-agent fairness result is retained without restarting DEV solely to recover an ephemeral diagnostic counter.

### Stage 4.1 overall verdict

**DEV VALIDATED.**

Stage 4.1 now provides:

1. event-driven CAPS completion from Stage 3;
2. a hard global outstanding-request safety bound;
3. no routine hard per-agent rejection;
4. work-conserving per-agent round-robin dispatch;
5. at most `Cap_AssetWorkers` jobs admitted into the legacy asset ObjectJobEngine;
6. zero leaked queue/guard slots in C32, C256 and C512 validation runs;
7. balanced external behavior under simultaneous two-agent load.

Before any LIVE rollout, perform the remaining shutdown/reload release-path audit so a region close/reload cannot leave static fair-dispatch/guard accounting stale inside a still-running process.

Next R1 architectural step after that safety audit: split the Asset Service / Asset Edge into its own coarse-grained service boundary.

### R1 Stage 4.1 – Shutdown/Reload Lifecycle Hardening

Status: **SOURCE HARDENED – BUILD/VERIFY THROUGH `novalyth-save`**

Audit finding:

OpenSim upstream `GetAssetsModule.DoAssetRequests()` returns immediately when
`m_NumberScenes <= 0`. After Stage 4/4.1 introduced guard and fair-dispatch
accounting, that upstream early return could bypass the request-finally release
path if the last region disappeared after a request was dispatched but before
its worker callback started.

A second lifecycle risk existed because the Stage-4.1 per-agent fair queues are
static process state. Queued-but-not-dispatched requests therefore needed an
explicit last-region drain before `ObjectJobEngine.Dispose()`.

Hardening:

- fair admission is gated while the last region is closing;
- fair dispatch stops feeding new work into `ObjectJobEngine` during shutdown;
- queued-but-not-dispatched requests are explicitly drained;
- drained requests release their `NovalythAssetQueueGuard` slot;
- drained PollService requests receive a lifecycle `503 region-shutdown`
  completion instead of remaining parked;
- `DoAssetRequests()` no longer has a pre-finally `m_NumberScenes <= 0` return;
- every dispatched request reaches guard release and
  `CompleteFairAssetRequest()` in `finally`;
- last-region `Close()` waits for the at-most-3 already-dispatched requests to
  finish before disposing `ObjectJobEngine`;
- the wait is bounded by the configured asset fetch timeout plus a safety margin;
- if that defensive wait times out, the workerpool is retained rather than
  disposed, because upstream `ObjectJobEngine.Dispose()` cancels its internal
  queue and could otherwise drop a tracked request before its callback runs;
- when a region becomes active again, fair admission is reopened and dispatch
  slots are refreshed from `Cap_AssetWorkers`.

This does not change normal scheduling, worker counts, queue limits or Stage-3
response wake behavior.

No LIVE deployment is performed by this source patch.

## R1 Asset Service Split – Phase A

Status: **DEV RUNTIME CUTOVER READY**

Date: 2026-08-14

Architecture:

- dedicated Asset Core process: `Robust.dll -inifile Robust.Asset.ini`;
- Asset Core backend endpoint: `http://127.0.0.1:8110`;
- service: `OpenSim.Services.AssetService.dll:AssetService`;
- storage: existing `novalyth_robust_dev.assets`;
- DEV region `AssetServerURI`: `http://127.0.0.1:8110`;
- viewer traffic still enters through region CAPS;
- Stage 3 response wake and Stage 4.1 fair dispatch remain unchanged;
- existing DEV Robust keeps its own AssetServiceConnector in Phase A for
  Login/HG/Map compatibility;
- TCP 8110 is denied externally by UFW;
- LIVE `/nvme/opensim` is unchanged.

Validation:

- Asset Core starts independently and loads `AssetServiceConnector`;
- deterministic benchmark asset `01060176-57ff-4df0-8885-035c15895efa` was fetched directly from Asset Core;
- HTTP status: 200;
- returned bytes: 32443;
- returned byte count exactly matched `novalyth_robust_dev.assets.data`;
- the original installer smoke selected a raw largest DB row rather than a
  known-good OpenSim asset and was rejected with HTTP 404; Phase-A validation
  now uses deterministic assets already exercised by the Stage-3/4 benchmark.

Screen helper correction:

- GNU Screen mode changed from `-D -m` (no fork, caller blocks) to
  `-d -m` (detached child, caller returns immediately).

Next validation:

1. start DEV Robust;
2. start DEV Region;
3. reset asset metrics;
4. run deterministic C32 through region CAPS;
5. confirm the region backend now reaches Asset Core on 8110 and compare
   latency/throughput against the Stage-4.1 baseline.

## R1 Asset Service Split – Phase B

Status: **DEV VALIDATED**

Date: 2026-08-14

Goal:

- make the dedicated Asset Core the only OpenSim AssetService instance that
  owns/opens the asset database;
- keep public HG asset compatibility through the existing Robust public port.

DEV architecture after Phase B:

- Region asset backend -> `http://127.0.0.1:8110`;
- Asset Core -> `novalyth_robust_dev.assets`;
- main Robust normal private `AssetServiceConnector` on 8103: disabled;
- main Robust `[AssetService] LocalServiceModule`: disabled;
- main Robust `[AssetService] AssetServerURI`: `http://127.0.0.1:8110`;
- `[HGAssetService] BackingService`:
  `OpenSim.Services.Connectors.dll:AssetServicesConnector`;
- `[GridService] AssetService`:
  `OpenSim.Services.Connectors.dll:AssetServicesConnector`;
- public `HGAssetServiceConnector` remains on 8102 and fronts the remote
  Asset Core through the HG permission/identifier wrapper;
- LIVE `/nvme/opensim` unchanged.

Validation:

- known texture `01060176-57ff-4df0-8885-035c15895efa` direct Asset Core bytes: 32443;
- public HG endpoint `8102/assets/<uuid>/data`: HTTP 200, exact same byte count;
- private main-Robust endpoint `8103/assets/<uuid>/data`: HTTP 404;
- startup log confirms HGAssetService remote backing connector;
- startup log contains no new local AssetService enable marker and no normal
  AssetServiceConnector load on 8103;
- DEV region restarted against the Phase-B topology.

Deferred:

- Stage-2 `caps pending` and fair-queue wait telemetry cleanup is recorded as a
  metrics TODO and is not a blocker for the service split.

## R2 Inventory Service Split – Phase A

Status: **DEV VALIDATED + VIEWER LOGIN VALIDATED**

Date: 2026-08-14

Architecture:

- dedicated Inventory Core process on `127.0.0.1:8120`;
- endpoint: `http://127.0.0.1:8120/xinventory`;
- local service: `OpenSim.Services.InventoryService.dll:XInventoryService`;
- storage: existing `novalyth_robust_dev` inventory tables;
- DEV Region InventoryServerURI -> `http://127.0.0.1:8120`;
- main DEV Robust/HG inventory remains unchanged in Phase A;
- Asset Core Phase B remains unchanged;
- LIVE `/nvme/opensim` unchanged.

Validation:

- test principal `bc699768-fb51-4dc7-a9b5-3b1edef31e4f`;
- existing Robust 8103/xinventory and Inventory Core 8120/xinventory
  return byte-identical GETROOTFOLDER responses;
- XInventoryInConnector startup is explicitly verified by
  `XInventoryInConnector loaded successfully`;
- DEV Region runs with TCP+UDP 9100 after cutover;
- Firestorm login succeeded after the InventoryServerURI cutover;
- avatar left cloud state immediately and scene textures were immediately visible;
- the optional RegionReady log marker is not used as a hard readiness condition.

Installer fixes learned:

- port LISTEN is not equivalent to service ready;
- wait for the connector-specific loaded marker before service smoke tests;
- `INITIALIZATION COMPLETE ... LOGINS ENABLED` belongs to optional RegionReady
  behavior and must not be mandatory for generic region readiness.

Next:

- practical inventory open/move/rez sanity check;
- then Inventory Phase B for exclusive Inventory DB ownership.

## R2 Inventory Service Split – Phase B

Status: **DEV RUNTIME VALIDATED / VIEWER SANITY PENDING**

Date: 2026-08-14

Architecture:

- dedicated Inventory Core `127.0.0.1:8120` is the exclusive Inventory DB owner;
- DEV Region inventory -> Inventory Core 8120;
- main Robust `[InventoryService]` is a remote XInventoryServicesConnector to 8120;
- private Robust `8103/xinventory` remains as a compatibility proxy to 8120;
- LoginService inventory -> remote XInventoryServicesConnector;
- UserAccountService inventory -> remote XInventoryServicesConnector;
- public HG `8102/xinventory` keeps suitcase policy through
  `RemoteHGSuitcaseInventoryService` in OpenSim.Services.Connectors.dll;
- RemoteHGSuitcaseInventoryService delegates storage to 8120 and contains no
  IXInventoryData/database provider;
- Asset Core Phase B remains unchanged on 8110;
- LIVE `/nvme/opensim` unchanged.

Validation:

- test principal `0f81b41d-75f2-4e6d-b751-0fd0ef64da2d`;
- Inventory Core 8120 GETROOTFOLDER: HTTP 200;
- private Robust proxy 8103 GETROOTFOLDER: byte-identical to 8120;
- public HG 8102 GETROOTFOLDER: HTTP 200 and rooted at `My Suitcase`;
- fresh main Robust startup contains the Novalyth remote-suitcase backing marker;
- fresh main Robust startup contains no InventoryStore migration marker;
- main Robust config contains zero active direct
  `OpenSim.Services.InventoryService.dll:XInventoryService` references;
- DEV Region restarted with TCP+UDP 9100;
- optional RegionReady marker is not used as a readiness condition.

Next:

- Firestorm login + inventory read/write/rez sanity check;
- then continue R2 inventory performance work: true batching, worker tuning, cache.

## Deferred – Public Service URLs / Edge Architecture

Status: **PLANNED / NOT NOW**

- Public service URL separation will be implemented later.
- Currently available domain: `db-rg.de`.
- DNS/proxy provider: Cloudflare.
- Do not expose internal Asset Core `127.0.0.1:8110` directly.
- Do not expose internal Inventory Core `127.0.0.1:8120` directly.
- Planned public edge/subdomain layout may use:
  - `login.db-rg.de`
  - `hg.db-rg.de`
  - `assets.db-rg.de`
  - `inventory.db-rg.de`
  - `map.db-rg.de`
- Firestorm should ultimately only require the public login/grid URI.
- Public edges/proxies will route internally to the separated Novalyth cores.
- This work is deferred until the current core/service architecture and performance work is further completed.

## R2 Inventory Performance – Native DB Batching

Status: **DEV RUNTIME VALIDATED**

Date: 2026-08-14

Implementation:

- added optional `IXInventoryDataBatch` capability without breaking the legacy `IXInventoryData` contract;
- MySQL/MariaDB exposes the existing generic parameterized `IN (...)` query path;
- `XInventoryService.GetMultipleFoldersContent()` uses native DB batching when the capability exists;
- one multi-folder request now performs three backend selects regardless of the number of requested folders:
  - child folders by `parentFolderID IN (...)`;
  - items by `parentFolderID IN (...)`;
  - requested folder metadata by `folderID IN (...)`;
- non-batch database providers and runtime batch failures automatically fall back to the legacy implementation;
- only the dedicated Inventory Core was restarted for deployment;
- DEV Robust, DEV Region, Asset Core and LIVE remained running.

Validation:

- pre-patch multi-folder request passed;
- patched solution built successfully before runtime deployment;
- Inventory Core loaded `IXInventoryDataBatch` for the MySQL/MariaDB provider;
- a real `GETMULTIPLEFOLDERSCONTENT` request executed the native batch path;
- no native-batch fallback was observed;
- all requested folder IDs were returned;
- Inventory Core remained service-ready on 8120;
- Asset Core 8110, Robust 8102/8103 and Region 9100 stayed healthy.

Next:

- inventory request coalescing / short-lived folder cache;
- bounded concurrency and worker/fairness controls only after reducing duplicate work.

## R2 Inventory Performance – Remote Folder Cache + Coalescing

Status: **DEV RUNTIME VALIDATED**

Date: 2026-08-14

Implementation:

- short-lived folder-content cache in `XInventoryServicesConnector`;
- default TTL `0.5s`, configurable with `FolderContentCacheSeconds` and clamped to `0..5s`;
- identical concurrent single-folder reads use single-flight request coalescing;
- identical concurrent missing subsets of multi-folder reads use single-flight coalescing;
- multi-folder calls satisfy cached folders first and send only misses to Inventory Core;
- cached `InventoryCollection` results are defensively cloned;
- successful inventory writes centrally invalidate the current user's cache generation;
- writes without a resolvable principal invalidate a process-wide generation;
- cross-process staleness remains bounded by the deliberately short TTL;
- existing 30-second item cache remains unchanged;
- Inventory Core native DB batching remains unchanged.

Validation:

- build succeeded before runtime deployment;
- new connector loaded in DEV Robust and DEV Region;
- two real identical `GETFOLDERCONTENT` requests through `8103` returned identical responses;
- runtime folder cache hit observed;
- 64 parallel functional requests returned HTTP 200;
- runtime single-flight observation: `YES`;
- Inventory Core 8120 and Asset Core 8110 were not restarted;
- DEV Robust and DEV Region returned healthy;
- LIVE `/nvme/opensim` remained untouched.

Next:

- bounded remote Inventory request concurrency and fairness;
- tune workers only after batching/cache/coalescing have removed duplicate work.

## R2 Inventory Performance – Bounded Concurrency + Fairness

Status: **DEV RUNTIME VALIDATED**

Date: 2026-08-14

Implementation:

- remote Inventory HTTP calls are bounded to `32` concurrent
  requests per process/endpoint gate;
- connectors targeting the same endpoint and using the same gate settings share
  one gate inside a process;
- no per-user hard cap and no request rejection path exists;
- when all active slots are occupied, requests wait instead of being rejected;
- queued requests are grouped by principal and dispatched round-robin;
- requests without a resolvable principal use a system lane;
- new arrivals do not jump ahead of already queued work;
- folder-cache hits and coalesced reads bypass remote HTTP and therefore consume
  no remote gate slot;
- native Inventory Core DB batching remains unchanged.

Configuration:

- `RemoteMaxConcurrentRequests = 32`;
- `RemoteFairQueue = true`;
- configured for Robust `InventoryService`, Robust `HGInventoryService`, and
  Region `InventoryService`.

Validation:

- full solution build passed before runtime deployment;
- bounded gate and round-robin dispatcher are present in built Connectors DLL;
- DEV Robust and DEV Region loaded the new gate configuration;
- functional parallel GETROOTFOLDER probe returned all HTTP 200 responses;
- runtime fair-queue observation: `NOT_OBSERVED_NONBLOCKING`;
- Inventory Core 8120 and Asset Core 8110 were not restarted;
- LIVE `/nvme/opensim` remained untouched.

Next:

- inspect Inventory Core incoming execution model before changing worker counts;
- add queue/latency telemetry only where it can guide tuning without changing
  request semantics.

## R2 Inventory Performance – Core Execution Model + Admission

Status: **DEV RUNTIME VALIDATED**

Date: 2026-08-14

Execution-model audit:

- `XInventoryInConnector` is a synchronous `BaseStreamHandler`;
- OpenSim's HTTP listener accepts connections asynchronously;
- each `HttpClientContext` starts an async receive task;
- completed requests invoke the registered HTTP handler directly in that
  connection request context;
- there is no dedicated Inventory worker-pool setting that should simply be
  increased;
- adding another Inventory worker pool would duplicate scheduling layers and
  was deliberately rejected.

Core admission implementation:

- Inventory Core `8120` now has a central admission gate before Inventory
  service dispatch;
- global maximum active Inventory requests: `32`;
- queued requests are grouped by principal and dispatched round-robin;
- requests with no resolvable principal use a system lane;
- existing queued work cannot be bypassed by new arrivals;
- no request rejection path exists;
- the gate is disabled by default and enabled only in dedicated
  `Robust.Inventory.ini`;
- main Robust/private/public proxy handlers therefore retain normal handler
  semantics unless explicitly configured.

Validation:

- full solution build passed before runtime deployment;
- `OpenSim.Server.Handlers.dll` contains the core admission implementation;
- only Inventory Core was restarted;
- startup confirmed central admission configuration;
- pre/post `GETROOTFOLDER` response was byte-identical;
- 128 parallel direct-core functional requests returned HTTP 200;
- runtime core queue observation: `NOT_OBSERVED_NONBLOCKING`;
- Asset Core, DEV Robust and DEV Region were not restarted;
- LIVE `/nvme/opensim` remained untouched.

Current R2 Inventory layers:

1. exclusive Inventory Core DB ownership;
2. native multi-folder DB batching;
3. short-lived remote folder cache;
4. remote request coalescing;
5. per-process remote bounded concurrency + principal fairness;
6. central Inventory Core bounded admission + global principal fairness.

Next:

- no blind worker-count tuning;
- perform targeted Inventory method/path audit for remaining N+1 DB patterns,
  especially skeleton/type/item lookup and mutation-side redundant reads.

## R2 Inventory Performance – Native Multi-Item Batching

Status: **DEV RUNTIME VALIDATED**

Date: 2026-08-14

Audit finding:

- `GetInventorySkeleton()` already performs one database read for all folders;
- `GetFolderForType()` performs targeted root + system-folder lookups and is not
  an N+1 path;
- `GetActiveGestures()` and `GetAssetPermissions()` already use single targeted
  database queries;
- `AddFolder()` / `UpdateFolder()` contain consistency and version checks whose
  prerequisite reads were deliberately preserved;
- `GetMultipleItems()` was a true N+1 path: the service looped over requested
  IDs and called `GetItem()` once per item.

Implementation:

- optional `IXInventoryDataBatch` now exposes `GetItemsByIDs`;
- MySQL/MariaDB maps it to the existing parameterized generic
  `inventoryID IN (...)` query implementation;
- `XInventoryService.GetMultipleItems()` now performs one backend query for any
  number of item IDs when the batch capability exists;
- requested item order and missing-item null positions are preserved;
- duplicate requested IDs remain supported;
- non-batch providers and runtime batch failures keep the legacy implementation.

Validation:

- solution built successfully before runtime deployment;
- only Inventory Core was restarted;
- native DB batching and central Core Admission remained active;
- real direct-core `GETMULTIPLEITEMS` request executed the new native path;
- pre/post HTTP responses were byte-identical;
- no legacy fallback occurred;
- Asset Core, DEV Robust and DEV Region were not restarted;
- LIVE `/nvme/opensim` remained untouched.

Current read-path reductions:

- multi-folder content: approximately `3 × N` database reads -> `3` reads;
- multi-item lookup: `N` database reads -> `1` read.

Next:

- audit mutation-side database handlers for repeated connection opens and
  version increments;
- prioritize transaction/connection reuse only where semantics stay identical.

## R2 Inventory Performance – MySQL Mutation Transaction Reuse

Status: **DEV RUNTIME VALIDATED**

Date: 2026-08-14

Audit finding:

- MySqlFramework instances created from a connection string open a database
  connection for each data-layer call;
- Inventory mutations commonly consist of multiple related data-layer calls;
- examples before this change:
  - Store item/folder: write + parent folder version increment on separate
    connections;
  - Move item/folder: pre-read + move update + old-parent version increment +
    new-parent version increment across multiple connection cycles;
  - Delete item: pre-read + delete + one or more folder-version increments;
- the single-field item delete path also performed a redundant pre-read before
  dispatching into the array overload, which performed the same read again.

Implementation:

- logical MySQL inventory mutations now execute on one connection and one short
  transaction;
- transactional temporary handlers use the existing MySqlTransaction-aware
  generic data layer;
- folder-version increments now route through `ExecuteNonQuery`, so they reuse
  the active transaction instead of unconditionally opening another connection;
- StoreItem, StoreFolder, MoveItem, MoveFolder and DeleteItems use transactional
  entry points;
- the duplicate single-item delete pre-read was removed;
- query semantics and folder-version update semantics were preserved.

Validation:

- solution built successfully before runtime deployment;
- only Inventory Core was restarted;
- native read batching and central Core Admission remained active;
- an isolated temporary folder was created through the real `/xinventory`
  ADDFOLDER path;
- the temporary folder was moved to a second parent and back through the real
  MOVEFOLDER path;
- the transactional MySQL runtime marker was observed;
- temporary test data was deleted and original parent folder versions restored;
- Asset Core, DEV Robust and DEV Region were not restarted;
- LIVE `/nvme/opensim` remained untouched.

Next:

- introduce a bulk mutation capability for MoveItems/DeleteItems so multi-item
  operations share one transaction instead of one transaction per item;
- preserve AllowDelete/link-only behavior when batching deletes.

## Appearance – Automatic Login Rebake for Incomplete Bakes

Status: **DEV DEPLOYED – VIEWER VALIDATION PENDING**

Date: 2026-08-14

Observed real-world symptom:

- a local user could remain a cloud for roughly 20 seconds after login;
- the manual `appearance rebake <first> <last>` command immediately repaired the appearance;
- normal inventory operations and the separated Inventory/Asset cores remained functional.

Upstream behavior:

- during `ScenePresence.CompleteMovement`, OpenSim validates the baked texture cache;
- when validation fails, upstream only queues an appearance save;
- it does not proactively request a viewer rebake at that login point.

NOVALYTH change:

- preserve the existing baked-cache validation and appearance-save behavior;
- if validation is incomplete and the arrival has the `ViaLogin` flag, request
  a rebake for **missing textures only** through the existing
  `IAvatarFactoryModule.RequestRebake()` path;
- ordinary region teleports are not changed;
- Hypergrid TP behavior remains excluded by the existing `!isHGTP` condition;
- no new worker pool or background service was introduced.

Runtime marker:

`[NOVALYTH APPEARANCE]: Incomplete baked texture cache ... requested ... missing-texture rebake(s) on login`

Deployment:

- only `OpenSim.Region.Framework.dll` in the DEV region runtime is replaced;
- only DEV Region is restarted;
- DEV Robust, Inventory Core and Asset Core remain untouched;
- LIVE `/nvme/opensim` remains untouched.

Viewer validation:

- log in normally with Firestorm without running a manual appearance command;
- compare cloud duration with the previous approximately 20-second baseline;
- confirm the NOVALYTH APPEARANCE runtime marker if the login cache is incomplete.

## Appearance R2 – event-driven login bake recovery (20260814-081640)

- Base commit: `bb3db4408c3f1c835fd2e28850567d27bc3969b6`
- Trigger: invalid baked-texture cache detected during real `ViaLogin`.
- CompleteMovement no longer depends on texture IDs being available immediately.
- A one-shot pending recovery flag is armed on the ScenePresence.
- The flag is atomically consumed by the first viewer `SetAppearance` update.
- Bake cache is revalidated after the viewer supplied current texture IDs.
- If still incomplete, Novalyth requests one full viewer rebake, equivalent to the working `appearance rebake` recovery path.
- If already valid, no rebake is requested.
- No sleeping worker/thread was added.
- Ordinary teleports do not arm this recovery.
- DEV Region only was restarted.
- Asset Core, Inventory Core, DEV Robust and LIVE remained untouched.

## Texture Pipeline R1 – remove undecoded head-of-line blocking (20260814-082240)

- Base commit: `a608fc38fbd2265c4d3451c3d032217f3090b0f7`
- OpenSim LLImageManager upstream behavior stopped an entire client's texture send cycle when the highest-priority image had not completed asset fetch/J2K decode.
- Novalyth keeps undecoded requests queued at their original priority but selects the highest-priority decoded request when the queue head is not ready.
- A blocked high-priority request becomes eligible normally as soon as its asynchronous decode completes.
- No request is dropped and no extra worker pool is introduced.
- One informational marker per client confirms a real runtime bypass without per-texture log spam.
- DEV Region only was restarted.
- Asset Core, Inventory Core, DEV Robust and LIVE remained untouched.

## Appearance Core Phase A – central bake persistence (20260814-083330)

- Base commit: `8c0985d0c759e8e629b3519ff5ca9036907660ab`
- Dedicated internal Appearance Core started on `http://127.0.0.1:8130`.
- OpenSim's existing XBakes service is used as the first storage layer for central baked-texture persistence.
- DEV Region uses `[XBakes] URL = http://127.0.0.1:8130`.
- Bake storage directory: `/nvme/novalyth-opensim-dev/bakes`.
- Raw port 8130 is internal and denied by UFW when UFW is active.
- This phase deliberately does NOT advertise Second Life server-side baking yet.
- `RegionProtocols` CentralBakeVersion bit remains unchanged until `UpdateAvatarAppearance` and `IncrementCOFVersion` exist.
- Next architecture phase: SL-compatible appearance capabilities + persistent COF/appearance version state.
- Final architecture phase after that: true server-side bake compositor and only then enable CentralBakeVersion.
- DEV Region only was restarted.
- DEV Robust, Asset Core, Inventory Core and LIVE remained untouched.

## Appearance Core Phase B – SL SSA protocol surface (20260814-121303)

- Base commit: `2cd3beefa5295c1f67085820e213b1773bab7025`
- Novalyth-owned server component: `Novalyth.Server.Appearance.NovalythAppearanceStateConnector`.
- Novalyth-owned region protocol component: `Novalyth.Region.Appearance.NovalythServerSideAppearanceModule`.
- Appearance Core :8130 owns persistent COF/appearance protocol state.
- State storage is service-owned and sharded under `/nvme/novalyth-opensim-dev/appearance-state`; regions do not persist SSA state.
- Internal service calls use a dedicated random service token.
- Viewer-facing SL capability names implemented: `UpdateAvatarAppearance` and `IncrementCOFVersion`.
- `UpdateAvatarAppearance` accepts the viewer's `cof_version` and implements stale-version semantics with `expected`.
- `IncrementCOFVersion` returns the current incremented `version`, matching the SL/Firestorm synchronization model.
- Phase B deliberately reports `server_bake_not_active` for bake requests because the compositor is not built yet.
- **CentralBakeVersion remains disabled.** Firestorm must not be switched into SSA until Phase C can really bake.
- Phase C: server bake compositor, appearance-version advancement, persistent bake manifest, Asset Core integration.
- Only Appearance Core and DEV Region are restarted for this phase.
- DEV Robust, Asset Core, Inventory Core and LIVE remain untouched.

## Appearance Core Phase C1 – authoritative COF + SL bake contract + manifest (20260814-123124)

- Base commit: `e03528dddb72342a0e778cbb541abb8a2c572641`.
- **C1 is intentionally not the pixel compositor. CentralBakeVersion remains disabled.**
- Current Outfit Folder authority moved to Inventory Core :8120.
- Appearance Core no longer invents an independent COF version; it reads `FolderType.CurrentOutfit` and its folder version from Inventory Core.
- `IncrementCofVersion` is the primary current-SL capability spelling; historical `IncrementCOFVersion` remains as an alias.
- Appearance Core builds a deterministic, persistent bake recipe from COF links and resolved inventory items.
- Wearable asset existence is checked against Asset Core :8110 before the recipe is marked complete.
- Persistent manifests live under `/nvme/novalyth-opensim-dev/appearance-manifests`.
- Bake contract `sl-current-11-v1` defines 11 current SL bake slots:
  `head, upper, lower, eyes, skirt, hair, leftarm, leftleg, aux1, aux2, aux3`.
- Each manifest records COF folder/version, recipe hash, ordered wearable contributors, attachments, broken links, missing assets, and all 11 pending bake slots.
- Recipe hash is deterministic SHA-256 over the authoritative COF recipe.
- Phase C2 will parse wearable assets/source texture IDs, decode source JPEG2000, composite layers, encode 11 bake outputs as needed, and store them in Asset Core.
- Phase C3 will validate real Firestorm SSA end-to-end and only then advertise RegionProtocols bit 0 / CentralBakeVersion.
- Only Appearance Core and DEV Region are restarted for C1.
- DEV Robust, Asset Core, Inventory Core and LIVE remain untouched.

## Appearance Core Phase C2A – wearable parser + source J2K audit (20260814-130800)

- Base commit: `11f2b757d8909449ee2bcc13d3f1a9ca1b0bc34b`.
- C2 is deliberately split so Novalyth does not claim a fake SL compositor.
- Appearance Core parses wearable asset payloads: wearable type, visual parameters and texture entries.
- Inventory wearable type and asset-declared wearable type are both recorded; mismatches are explicit.
- Current SL local texture indices are mapped to the 11 `sl-current-11-v1` bake slots.
- Deterministic recipe format advances to `novalyth-ssa-recipe-v2` and includes parsed wearable payload hashes.
- Manifests record parsed wearable parameters/textures, source texture references, parse errors, missing wearable assets and missing source assets.
- Internal `GET /novalythappearance/sourceaudit/<agent>` fetches unique source textures from Asset Core :8110 and verifies JPEG2000 decoding.
- C2A source reference sequence is deterministic audit order only; it is **not** claimed as final SL compositor layer order.
- Correct tint/color, alpha, masks and avatar layer semantics remain C2B work.
- No bake J2K output is generated or stored in C2A.
- `CentralBakeVersion` / `AdvertiseCentralBake` remains disabled.
- Only Appearance Core is restarted for C2A.
- DEV Region, DEV Robust, Asset Core, Inventory Core and LIVE remain untouched.
- C2B: SL/avatar_lad-compatible layer semantics + real compositor + J2K encode/store.
- C3: real Firestorm SSA validation and only then CentralBakeVersion advertisement.

## Appearance Core Phase C2B1 – 11-slot compositor foundation + J2K Asset Core store (20260814-133011)

- Base commit: `808ae4a07ad600498e808db9e8711e4ce409846f`.
- C2A real-avatar validation passed before this phase:
  deterministic recipe, zero broken links, zero missing wearable/source assets,
  zero wearable parse/type errors and all unique source J2Ks decoded.
- Runtime audit confirmed OpenMetaverse 0.9.4 exposes all 11 modern bake enums,
  `ManagedImage`, `AssetTexture.Encode()` and OpenJPEG encode/decode APIs.
- The earlier C2B runtime resource failure was a probe EntryAssembly-path artifact;
  the actual Appearance Core runtime owns `/nvme/novalyth-opensim-dev/bin/openmetaverse_data`.
- C2B1 adds internal `POST /novalythappearance/bake/<agent>`.
- Bake execution is bounded to two concurrent 2K compositors per Appearance Core process.
- Multiple COF wearables are preserved as independent bake-layer inputs instead of
  collapsing the outfit into the historical one-wearable-per-type dictionary.
- All 11 `sl-current-11-v1` bake slots are generated on demand:
  head, upper, lower, eyes, skirt, hair, leftarm, leftleg, aux1, aux2, aux3.
- Current layer-set output size is 2048x2048, with eyes at 512x512.
- Per-layer tint uses wearable visual parameters; Universal/tattoo and dual jacket
  texture channels use texture-specific RGB triplets.
- OpenMetaverse visual-param decoding supplies classic tint and alpha-mask/driver data.
- Visibility alpha textures are applied after color compositing.
- J2K output is encoded through the existing OpenMetaverse/OpenJPEG codec.
- Baked texture asset IDs are deterministic from recipe hash + slot + J2K hash.
- J2Ks are stored through the authoritative Asset Core :8110 `IAssetService.Store`.
- Successful repeated requests with the same recipe reuse existing immutable bake assets.
- C2B1 compositor profile is `novalyth-c2b1-modern-foundation-v1`.
- C2B1 is intentionally marked as a foundation, not final SL pixel parity:
  the complete static avatar_lad.xml makeup/detail layer interpreter remains required
  before C3 can claim full Second Life server-side appearance parity.
- The five auxiliary slots use the avatar_lad neutral fixed-color base internally;
  C2B1 does not add an untracked external aux_base.tga dependency.
- `CentralBakeVersion` / region protocol advertisement remains disabled.
- C2B1 changes only Appearance Core runtime. DEV Region, DEV Robust, Asset Core process,
  Inventory Core process and LIVE are not restarted.
- C2B2: complete static avatar_lad.xml layer semantics / parity audit.
- C3: real Firestorm SSA protocol validation; only then enable CentralBakeVersion.

## Appearance Core Phase C2B2 – official avatar_lad semantics (20260814-142356)

- Base commit: `20db566fb56d148312db84670e96ce37cdd55e33`.
- C2B1 real-avatar bake audit passed with 11/11 stored J2Ks,
  deterministic output and Asset Core reuse proof.
- Pinned Second Life viewer commit: `dcff8c97ea5448f0acaff76fa0a74a89889be678`.
- Pinned official avatar_lad download SHA256: `ace7a7aebac5bee593d2ec2f5a487404cf53859e54537d00e53173c8fa1ee2cd`.
- Repository-normalized avatar_lad SHA256: `83380dcc2cbc3bce0e74b9429a2da3f7b701756c74248b6edeef6b61c96c20da`.
- Normalization is whitespace-only (tabs -> spaces, trailing whitespace removed);
  XML element/attribute content is unchanged.
- The normalized pinned definition is versioned at `OpenSim/Server/Handlers/BakedTextures/Resources/novalyth_avatar_lad.xml`.
- Runtime uses a Novalyth-specific copy `openmetaverse_data/novalyth_avatar_lad.xml`;
  the existing OpenMetaverse avatar_lad.xml is not overwritten.
- All 11 current layer sets are required at startup.
- Output dimensions and layer order come from the pinned official definition.
- Static TGA layers, fixed/global color layers, param-color ramps,
  param-alpha masks, visibility masks and bump-pass layers are interpreted.
- Per-wearable local textures still use C2B1 decoded tint and alpha masks.
- Multiple COF wearables remain independent ordered inputs.
- `aux_base.tga` is generated as the neutral universal base when absent.
- C2B2 profile is `novalyth-c2b2-avatar-lad-v1`.
- C2B2 uses a new deterministic bake-asset namespace.
- C2B1 manifests are explicitly rejected by the C2B2 reuse path.
- Bake assets remain authoritative in Asset Core :8110.
- `CentralBakeVersion` remains disabled.
- Only Appearance Core is restarted.
- DEV Region, DEV Robust, Asset Core process, Inventory Core process and LIVE remain untouched.
- Next: C2B2 real-avatar bake/reuse audit, then C3 Firestorm SSA protocol validation.

## Appearance Core Phase C2B2.1 – reuse manifest metadata consistency (20260814-143350)

- Base commit: `3c67d43a40fff66a21f4ed109b7432fa49e661ab`.
- C2B2 real-avatar audit passed:
  - C2B1 -> C2B2 reuse barrier PASS.
  - C2B1/C2B2 deterministic asset namespace separation PASS.
  - C2B2 second-run Asset Core reuse PASS.
  - 11/11 bakes stored and deterministic.
- Audit exposed one metadata-only issue: a reused C2B2 manifest did not repopulate
  `compositor_semantics`, so the field became empty after the second bake.
- Reuse now explicitly restores the canonical C2B2 compositor semantics string and
  reasserts `central_bake_advertised=false`.
- No bake recipe, J2K compositor, Asset Core ID namespace, output dimensions,
  COF handling or viewer protocol surface changes.
- CentralBakeVersion remains disabled.

## Appearance Core C3 – Firestorm SSA advertisement source (20260814-150823)

- Base commit: `c8e6301abfd4927f516bb74d558abb65f6d0712a`.
- C2B2.1 remains the bake/compositor baseline.
- C3 adds viewer-visible `CentralBakeVersion` through OpenSim's existing
  `ISimulatorFeaturesModule` only when `[NovalythSSA] AdvertiseCentralBake=true`.
- `CentralBakeVersion` is configurable and defaults to `1`.
- Existing viewer CAPS remain:
  - `UpdateAvatarAppearance`
  - `IncrementCofVersion`
  - legacy `IncrementCOFVersion`
- With advertisement disabled, behavior remains the existing C2B2.1/C1 protocol surface.
- This source-only step performs no DEV runtime deployment, no INI mutation and no restart.
- LIVE `/nvme/opensim` is not touched.

## Appearance Core C4A – SL-style atomic server-bake publish bridge (20260814-154521)

- Base commit: `ebbfa50f9a19f7913a10d6bfdfbf10c00aee8a99`.
- C3 Firestorm advertisement remains enabled through `CentralBakeVersion=1`.
- C4A closes the missing wire-to-runtime gap: `UpdateAvatarAppearance` now queues
  an asynchronous server bake and publishes the completed 11 bake asset IDs into
  the live region `AvatarAppearance` only after the complete bake set is ready.
- Second-Life-style last-known-good behavior: the currently visible appearance is
  retained while the next generation is baking; failed/incomplete/stale generations
  are never published.
- Rapid outfit changes are coalesced (default 75 ms) and generation-gated. If a newer
  UpdateAvatarAppearance arrives while a bake is running, the stale completed bake is
  suppressed and the worker proceeds to the newest generation.
- The final server appearance is sent immediately to the owning viewer and all other
  agents after atomic publication, bypassing OpenSim's legacy queued appearance-send
  delay after the bake is already complete.
- Modern 11-slot mapping is preserved: head=8, upper=9, lower=10, eyes=11, skirt=19,
  hair=20, leftarm=40, leftleg=41, aux1=42, aux2=43, aux3=44.
- `IncrementCofVersion` remains a COF synchronization capability; it does not itself
  start a bake. `UpdateAvatarAppearance` is the bake trigger, matching SL semantics.
- New optional region setting: `[NovalythSSA] BakeCoalesceMilliseconds` (default 75,
  range 0..2000). No INI change is required for the default.
- This step is source/build/commit only. No DEV runtime deployment, no restart, no
  runtime INI mutation and no LIVE `/nvme/opensim` changes are performed.
