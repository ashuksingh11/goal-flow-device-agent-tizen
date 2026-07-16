# AGENTS.md — goal-flow-device-agent-tizen (coding-session guide)

Context for an AI/coding session in this repo. Read first.

## Status: v2 + MULTI-SESSION — in sync with ubuntu (last re-synced 2026-07-16)

This is the **Tizen Family Hub** deployment of the GoalFlow device agent. It runs the
v2 Semantic-Kernel design and is **in sync** with the source of truth,
**`../goal-flow-device-agent-ubuntu`** (core re-synced 2026-07-16 to pick up the
multi-session `device_id`/`device_name` handshake).

The port is deliberately thin: the **portable v2 core is byte-for-byte identical**
to the Ubuntu build, and only the platform edges differ. Future re-syncs are a
plain copy of the core + a `dotnet build` — NEVER overwrite the Tizen host files
(`Program.cs`, `DeviceHost.cs`, `DeviceConfig.cs`, `DlogLogger.cs`, `AssemblyResolver.cs`,
`UiChannel.cs`). Verify a re-sync with:

```bash
UB=../goal-flow-device-agent-ubuntu/src/GoalFlow.Device
for d in Agent Contracts Modules Transport; do diff -rq "$d" "$UB/$d"; done   # must print nothing
```

**A core change may need host wiring on BOTH sides.** Example: `device_id` landed in the
core (`Contracts/Hello.cs`, `Transport/WsClient.cs`) but each host resolves it its own
way — ubuntu in `ProgramHelpers`, Tizen in `DeviceConfig` (below). Copying the core alone
would compile but never send an id.

## Multi-session: this Hub's `device_id`

The cloud is multi-session — a **session = this agent + N UIs**, keyed by `device_id`
(see `../goal-flow-cloud-agent/CONTRACT.md` § "Sessions"). `DeviceConfig.ResolveDeviceId`
takes `DEVICE_ID` from `goalflow.conf`/env, else generates a UUID **persisted in the
WRITABLE data dir** (`<data>/device_id`) — so the Hub keeps ONE identity across restarts
with zero configuration. `DeviceConfig.ResolveDeviceName` takes `DEVICE_NAME`, else
`Family Hub (<short-id>)` — NOT `user@machine` (every Hub reports the same Tizen user/host,
so two units would show identical labels); the short id comes from the unique device_id.
**Set `DEVICE_NAME` in `goalflow.conf`** ("Kitchen Hub") when several Hubs
share a cloud — that label is what the UI's device picker shows. `Program.cs` resolves both
in `OnCreate` (dlog-logged) and passes them to `WsClient`.

## Layout

- **Portable core (copied unchanged from ubuntu `src/GoalFlow.Device/`):**
  - `Agent/GoalAgent.cs` — the SK agent: builds the kernel, auto function-calling
    planner, approval/adaptation. `AgentSettings` + `BuildKernel` live here.
  - `Contracts/*.cs` — v2 wire contracts (Dispatch, PlanReady, Proposal, Status,
    Approval, Control, AgentEvent, Capabilities, Hello, ContractJson).
  - `Modules/Capabilities/*Plugin.cs` — SK capability plugins (`KernelFunction`s):
    Inventory, Calendar, Recipe, ShoppingList, Reminder, Guests, ApplianceControl,
    FamilyProfiles, Budget, Notify + `MockWorldStore` (concrete world over `data/*.json`).
  - `Modules/Steering/*` — `SafetyFilter` (IFunctionInvocationFilter), `ApprovalCoordinator`,
    `Grounding`, `MonitorAdapt` (+ `MaterialityPolicy`), `Clock` (`IClock`/`SimulatedClock`),
    `CapabilityRegistry`, `Trace`.
  - `Transport/WsClient.cs` — thin WS transport (connect-retry + reconnect-on-drop,
    `FrameReceived` event, serialized `SendAsync`).
- **Tizen platform edge (the ONLY device-specific code):**
  - `Program.cs` — `GoalFlowService : ServiceApplication`. `OnCreate` builds the host
    and starts the connect/receive loop on a background task (OnCreate must return);
    `OnTerminate` cancels + disposes. Also constructs the `UiChannel` and tees each
    outbound frame to it (a few one-line `_ui?.Forward(...)` calls + the emit wrap) —
    these are the ONLY additions to the frame path and touch NO core file.
  - `UiChannel.cs` — **Tizen host file, NEVER overwrite on re-sync.** Launches the on-Hub
    NUI UI (`org.goalflow.tizenui`, repo `../goal-flow-agent-tizen-ui`) via App Control on
    goal activation and forwards progress frames to it over a PUBLIC Message Port (ports
    `goalflow.agent.control` in / `goalflow.ui.frames` out; buffer + `ui_ready` handshake).
    Serializes via `ContractJson.Serialize` (a call). Best-effort, non-throwing — a UI
    hiccup never affects planning or the cloud path.
  - `DeviceHost.cs` — the DI container + kernel builder. Mirrors the Ubuntu `Program.cs`
    wiring, but reads config via `DeviceConfig` (NOT env vars) and logs via dlog (NOT console).
  - `DeviceConfig.cs` — **env-free config** (see Tizen platform notes). Reads a bundled
    `goalflow.conf` (KEY=VALUE); keys: `OPENROUTER_API_KEY` (required), `OPENROUTER_BASE_URL`,
    `OPENROUTER_MODEL`, `WS_URL`, `GOALFLOW_DATA_DIR`, `GOALFLOW_DATE`, `LOG_LEVEL`. Also
    seeds a writable `data/` copy in the app Data dir.
  - `DlogLogger.cs` — `ILoggerProvider` routing `Microsoft.Extensions.Logging` → `Tizen.Log`
    (dlog). View logs on the Hub with `dlogutil GOALFLOW`.
  - `GoalFlow.Device.Tizen.csproj` — net8.0 + Tizen.NET 12.x + Semantic Kernel +
    Microsoft.Extensions.{Logging,Console,DI}. Keep the package list in sync with the
    Ubuntu csproj. Bundles `data/**/*.json` + (if present) `goalflow.conf`.
  - `tizen-manifest.xml` — `service-application` (headless), internet + alarm +
    notification + mediastorage privileges (reserved for real actuators).

## Tizen platform notes (do NOT regress — these caused real on-Hub crashes)

A headless Tizen `ServiceApplication` is not a normal console process. These
platform edges differ from the Ubuntu build and are handled ONLY in the Tizen
edge files (the portable core is untouched):

1. **No console → dlog.** The service has no stdout; `Microsoft.Extensions.Logging`'s
   `AddConsole()` crashes. `DlogLogger.cs` routes logs to `Tizen.Log` instead
   (`DeviceHost` wires it, never `AddConsole`). **View with `dlogutil GOALFLOW`**
   (or `sdb dlog GOALFLOW:V *:S` to isolate). `Program` also emits dlog breadcrumbs
   at Main / OnCreate / host-built / connecting, and wraps `OnCreate` + the connect
   loop in try/catch that dlog-logs the exception — so a startup failure is never
   silent. (If dlog looks empty, the app likely threw BEFORE logging — see #4.)
2. **No environment variables.** A Tizen service is not launched with the shell
   environment, and its CWD is not the app dir — so `Environment.GetEnvironmentVariable`
   and a CWD-relative `.env` both fail (this is why `OPENROUTER_API_KEY` was null →
   throw). Config comes from `DeviceConfig` reading a bundled `goalflow.conf`
   (copy `goalflow.conf.example` → `goalflow.conf`, gitignored, holds the API key).
   Env vars still win off-device (desktop parity).
3. **Read-only resource dir + folder mapping (bin, not res).** `MockWorldStore`
   mutates `data/*.json`, but the bundled `data/` ships read-only.
   `DeviceConfig.ResolveDataDir()` seeds a writable copy into the app `Data` ROOT on
   first run and points the store there. **CRITICAL:** the csproj bundles `data/**`
   and `goalflow.conf` as MSBuild `Content`, so Tizen packaging drops them next to
   the app assemblies under **`<app>/bin` == `AppContext.BaseDirectory`** (readable),
   NOT under `Application.Current.DirectoryInfo.Resource` (`<app>/res`, which stays
   empty). So `DeviceConfig` reads the bundled `goalflow.conf` and the seed-source
   `data/` from `AppContext.BaseDirectory` first (with `Resource` kept only as a
   fallback), and seeds the writable copy into the `Data` root (not a `Data/data`
   sub-dir). Don't assume `res` — `bin` is where `Content` lands and is app-readable.
4. **Package versions MUST stay on the .NET 8 line.** Tizen 12 runs on .NET 8 and
   ships its OWN `System.Text.Json` (a platform assembly loaded before app-local
   ones). Wildcard versions (`*` / `1.*`) resolved to .NET 10-era packages —
   `System.Text.Json 10.0.9`, `SemanticKernel 1.78` (which requires STJ 10.0.6) —
   and the Tizen runtime refuses to load them (assembly version 10.0.0.0 vs the
   platform's 8.0.0.0). **Pinned:** `Microsoft.SemanticKernel 1.43.0` (newest SK line
   still on STJ 8.0.5; SK ≥ ~1.61 jumps to 10.x), `System.Text.Json 8.0.5`,
   `Microsoft.Extensions.* 8.0.x`. STJ's assembly version is `8.0.0.0` for every
   8.0.x, so this satisfies whatever 8.0.x the device ships. Do NOT reintroduce
   wildcards or bump SK without checking its transitive STJ stays 8.0.x. SK 1.43
   still gates a few APIs behind `SKEXP0001`/`SKEXP0010` (1.78 graduated them), so
   the csproj `NoWarn`s those — Tizen-only; the core has no pragmas.
   `AssemblyResolver.cs` additionally prefers app-local (bin) copies as a fallback,
   but that only helps when the platform did not already load the assembly — the
   version pin is the real fix.

## v2 invariants (do NOT regress)

- **LLM-ONLY.** Planning goes through the SK kernel + OpenRouter. There is NO
  `GOALFLOW_PLANNER=rules|scripted|llm` — that model was removed in v2.
- **Generic clock.** `SimulatedClock` anchored at real today (or `$GOALFLOW_DATE`);
  `set_date` / `advance_day` controls drive it. No hardcoded anchor date anywhere.
- **The v1 adapter seam is gone.** There is no `IInventoryApi` / `GOALFLOW_ADAPTERS=mock|tizen`.
  The world is the concrete `MockWorldStore` reading bundled `data/*.json` (works on the
  Hub — it's just JSON). **Wiring real Tizen actuators is future work behind the capability
  plugins** — change what a plugin *does* (e.g. `NotifyPlugin`/`ReminderPlugin`/
  `ApplianceControlPlugin` → real Tizen.Applications APIs), not a separate adapter set.
  The manifest already reserves the privileges.

## Build & run

- **Builds in this dev env**: `dotnet build -c Debug` succeeds (Tizen.NET ships the
  `ServiceApplication` types as a NuGet package — no Tizen workload needed). 0 errors;
  the one warning is a benign Tizen.NUI Xaml-target notice.
- **Running the service** needs the Tizen runtime (the Family Hub or an emulator) — the
  `.tpk` build + on-Hub deploy/run is done manually by the human; the Tizen SDK is not in
  this dev environment. The **Ubuntu build is the demo fallback** (one-line endpoint swap).
- Needs `OPENROUTER_API_KEY` (via `.env` or env). The device opens an outbound WS to
  `WS_URL` (default `ws://localhost:8000/ws`).

## Conventions

- **Commit identity:** author as `ashuksingh11`
  (`31301999+ashuksingh11@users.noreply.github.com`). **Push only when asked.**
- **Workflow:** plan=Opus · design=Fable (only when asked / for architecture) · coding=Codex CLI · browsing=Sonnet.
- The canonical wire contract lives in `../goal-flow-cloud-agent/CONTRACT.md`.
