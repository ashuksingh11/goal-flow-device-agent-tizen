# AGENTS.md — goal-flow-device-agent-tizen (coding-session guide)

Context for an AI/coding session in this repo. Read first.

## Status: RE-PORTED TO v2 (2026-07-13) — Tizen edge of the current device brain

This is the **Tizen Family Hub** deployment of the GoalFlow device agent. As of
2026-07-13 it has been **re-ported to the v2 Semantic-Kernel design** and is in
sync with the source of truth, **`../goal-flow-device-agent-ubuntu`**. (It was
previously frozen at the retired v1 harness/pipeline architecture.)

The port is deliberately thin: the **portable v2 core is byte-for-byte identical**
to the Ubuntu build, and only the platform edges differ. Future re-syncs are a
plain copy of the core + a `dotnet build`.

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
    `OnTerminate` cancels + disposes.
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

A headless Tizen `ServiceApplication` is not a normal console process. Three
platform edges differ from the Ubuntu build and are handled ONLY in the Tizen
edge files (the portable core is untouched):

1. **No console → dlog.** The service has no stdout; `Microsoft.Extensions.Logging`'s
   `AddConsole()` crashes. `DlogLoggerProvider` routes logs to `Tizen.Log` instead
   (`DeviceHost` wires it, never `AddConsole`). Tail with `dlogutil GOALFLOW`.
2. **No environment variables.** A Tizen service is not launched with the shell
   environment, and its CWD is not the app dir — so `Environment.GetEnvironmentVariable`
   and a CWD-relative `.env` both fail (this is why `OPENROUTER_API_KEY` was null →
   throw). Config comes from `DeviceConfig` reading a bundled `goalflow.conf`
   (copy `goalflow.conf.example` → `goalflow.conf`, gitignored, holds the API key).
   Env vars still win off-device (desktop parity).
3. **Read-only resource dir.** `MockWorldStore` mutates `data/*.json`, but the .tpk
   bundles `data/` read-only under `Application.Current.DirectoryInfo.Resource`.
   `DeviceConfig.ResolveDataDir()` seeds a writable copy into the app `Data` dir on
   first run and points the store there.

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
