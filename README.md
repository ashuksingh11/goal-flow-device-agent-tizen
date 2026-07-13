# GoalFlow Device Agent — Tizen Port

The **Tizen Family Hub** deployment of the GoalFlow on-device agent — the
executor tier of a general goal agent. This repo has been **re-ported to the
v2 Semantic-Kernel design** and is kept in sync with the source of truth,
`../goal-flow-device-agent-ubuntu`.

**v2 core idea: the device IS a Semantic Kernel agent.** There is no
hand-rolled pipeline and no rules/scripted planner. Device capabilities are SK
**plugins** whose methods are `[KernelFunction]`s the LLM calls via auto
function-calling; safety is a deterministic `IFunctionInvocationFilter` that
vets every pending call against the dispatch's hard constraints before the
plugin method runs. Planning is **LLM-only** end to end, via OpenRouter.

The port is deliberately thin: the **portable v2 core is byte-for-byte
identical to the Ubuntu build**, and only the platform edges differ.

## Layout

- **Portable core** (copied unchanged from ubuntu `src/GoalFlow.Device/`):
  - `Agent/GoalAgent.cs` — the SK agent: builds the kernel, drives the
    auto-function-calling planner, applies approvals, runs adaptation.
  - `Contracts/*.cs` — the v2 wire contracts (Dispatch, PlanReady, Proposal,
    Status, Approval, Control, AgentEvent, Capabilities, Hello, ContractJson).
  - `Modules/Capabilities/*Plugin.cs` — SK capability plugins: Inventory,
    Calendar, Recipe, ShoppingList, Reminder, Guests, ApplianceControl,
    FamilyProfiles, Budget, Notify, plus `MockWorldStore` (the concrete world
    over bundled `data/*.json`).
  - `Modules/Steering/*` — deterministic harness modules: `SafetyFilter`,
    `ApprovalCoordinator`, `Grounding`, `MonitorAdapt` (+ `MaterialityPolicy`),
    `Clock` (`IClock` / `SimulatedClock`), `CapabilityRegistry`, `Trace`.
  - `Transport/WsClient.cs` — the outbound WebSocket transport (BCL
    `ClientWebSocket`, connect-retry, reconnect-on-drop, serialized sends).
  - `data/*.json` — the mock world and sample contracts.
- **Tizen platform edge** (the only device-specific code):
  - `Program.cs` — `GoalFlowService : ServiceApplication`, a headless
    Tizen service host.
  - `DeviceHost.cs` — the DI container + SK kernel builder, mirroring the
    Ubuntu `Program.cs` wiring so the core runs unchanged.
  - `GoalFlow.Device.Tizen.csproj` — package manifest.
  - `tizen-manifest.xml` — the Tizen service package descriptor.

See `CODE_GUIDE.md` for the code walkthrough.

## Code-Sharing Strategy

The core is copied from `../goal-flow-device-agent-ubuntu` so this repo stays
self-contained for Tizen packaging. That keeps the port simple, but it
creates drift risk: fixes to the agent, contracts, capability plugins,
steering modules, or transport must be manually re-copied between repos. A
production version should extract a shared `GoalFlow.Core` library and have
both the Ubuntu and Tizen hosts reference it.

## v2 invariants

- **LLM-only.** Planning goes through the SK kernel + OpenRouter — there is
  no planner selection (the old rules/scripted/LLM planner switch is gone).
- **Generic clock.** `SimulatedClock` anchors at real today, or at
  `$GOALFLOW_DATE` if set; `set_date` / `advance_day` control frames drive it
  from there. Nothing hardcodes a date.
- **World is mock JSON.** `MockWorldStore` reads/writes bundled
  `data/*.json`; there is no adapter-interface seam to pick between a mock
  and a real backend. **Real Tizen actuator integration is future work**: it
  means changing what a capability plugin *does* (e.g. `NotifyPlugin`,
  `ReminderPlugin`, `ApplianceControlPlugin` calling real
  `Tizen.Applications` APIs), not adding a parallel adapter set. The manifest
  already reserves the alarm/notification/mediastorage privileges for that.

## Build And Run

`dotnet build -c Debug` succeeds in a plain .NET 8 dev environment — Tizen.NET
ships the `ServiceApplication` types as a NuGet package, so no Tizen workload
install is needed just to compile.

**Running** the service needs the Tizen runtime (the Family Hub or an
emulator): producing and installing a `.tpk` is done manually by a human on a
machine with the Tizen SDK, certificates, and a connected device/emulator —
that step is out of scope for this dev environment. The Ubuntu build
(`../goal-flow-device-agent-ubuntu`) is the demo fallback.

```bash
cd /home/chachapranu/ashu/git/goal-flow-device-agent-tizen
dotnet restore
dotnet build -c Debug
```

Manual Tizen packaging and deploy, on a machine with Tizen Studio:

```bash
tizen build-cs -c Release
tizen package -t tpk -s <certificate-profile-name> -- .
sdb devices
tizen install -n org.goalflow.deviceagent-1.0.0.tpk -t <device-name>
```

The required package identity is in `tizen-manifest.xml`:
`org.goalflow.deviceagent`.

## Environment

| Variable              | Meaning                              | Default                        |
| ---------------------- | ------------------------------------ | ------------------------------- |
| `OPENROUTER_API_KEY`   | OpenRouter API key                   | — (**required**)                |
| `OPENROUTER_BASE_URL`  | OpenAI-compatible base URL           | `https://openrouter.ai/api/v1`  |
| `OPENROUTER_MODEL`     | Model id                             | `openai/gpt-oss-120b`           |
| `WS_URL`               | Cloud hub WebSocket endpoint         | `ws://localhost:8000/ws`        |
| `GOALFLOW_DATA_DIR`    | Mock world directory                 | `data`                          |
| `GOALFLOW_DATE`        | `SimulatedClock` start date (ISO)    | real today                      |
| `LOG_LEVEL`            | Console log level                    | `Information`                   |

Loaded from `.env` in the working directory (plain `KEY=VALUE`) or the
process environment.
