# Code Guide — GoalFlow Device Agent Tizen

This repo is a Tizen.NET host shell around the **copied** GoalFlow device-agent core. The SK
agent, contracts, the generic `Harness/`, the `Products/FamilyHub/` pack, transport and seed data
are the same code copied from `../goal-flow-device-agent-ubuntu` — which is the source of truth.
The Tizen port adds only the service host, the DI wiring, the packaging manifest, and this file.

**The trap this file exists to prevent:** DI resolves at RUNTIME, so a host missing a
registration compiles perfectly and dies on the first goal. That has happened twice. A green
`dotnet build` is not evidence the port works — compare `DeviceHost.cs` against Ubuntu's
`Program.cs` line by line after every re-sync.

## File Map

```text
GoalFlow.Device.Tizen.csproj   # net8.0 + Tizen.NET + Semantic Kernel + Microsoft.Extensions.{Logging,DI}
Program.cs                     # GoalFlowService : ServiceApplication — the headless Tizen host
DeviceHost.cs                  # DI container + SK kernel builder, mirrors Ubuntu Program.cs wiring
tizen-manifest.xml             # background service package manifest + privileges
Agent/GoalAgent.cs             # copied: SK kernel host — BuildKernel / RunAsync / ApplyApprovalAsync / HandleControlAsync
Contracts/*.cs                 # copied: C# mirror of CONTRACT.md (Dispatch, PlanReady, Proposal, Approval,
                                #         Status, Control, AgentEvent, Capabilities, Hello, ContractJson)
Harness/                       # copied: THE GENERIC CORE — no product literals
  CapabilityManager/             # discovery, resolution, availability, the grounding set
  SafetyPolicyEngine/            # ArmedPolicies (store) + SafetyFilter (SK filter seam) + rules
  PrecheckEngine/                # probes + the two gates
  TaskManager/                   # task DAG, lifecycle, retries, IDomainObserver
  ProductApiAdapter/             # the seam plugins call instead of the world
  Approval/ Grounding/ Clock/ Trace/
Products/FamilyHub/            # copied: ALL fridge specifics
  Adapter/ Plugins/ Probes/ Observers/ config/{policy,prechecks}.json
Transport/WsClient.cs          # copied: outbound BCL ClientWebSocket to the cloud hub
data/*.json                    # copied: mock world and sample contracts
```

## Host Shape (`Program.cs`)

`GoalFlowService` subclasses `Tizen.Applications.ServiceApplication`. It owns
**only** platform lifecycle + transport wiring; the portable SK agent (DI
container, capability plugins, steering modules) is built by `DeviceHost` and
runs unchanged from the Ubuntu build. It does not create a window or use NUI.

Lifecycle:

- `OnCreate()` loads `.env`, calls `DeviceHost.Build(dataDir)` to construct
  the DI container + kernel, resolves `WS_URL` (default
  `ws://localhost:8000/ws`), and starts the connect/receive loop
  (`RunAsync`) on a background `Task` — `OnCreate` must return promptly, so
  the long-running loop cannot block it.
- `RunAsync` opens the `WsClient`, builds a `Trace` that emits `AgentEvent`
  frames back over the socket, creates the `GoalAgent` via
  `host.CreateAgent(trace)`, sends the `capabilities` advertisement, and
  wires `WsClient.FrameReceived` to route `dispatch` → `agent.RunAsync`,
  `approval` → `agent.ApplyApprovalAsync`, and `control` →
  `agent.HandleControlAsync`. Each inbound frame is handled on its own
  background `Task` so the receive loop keeps answering pings while a plan
  is being built (30-60s of LLM calls) — blocking the loop would let the
  cloud's keepalive close the socket mid-plan.
- `OnTerminate()` cancels the token, waits briefly for the loop to exit, and
  disposes the `DeviceHost` (which disposes the DI `ServiceProvider`).
- `Main(string[] args)` calls `new GoalFlowService().Run(args)`.

## DI Wiring (`DeviceHost.cs`)

`DeviceHost.Build(dataDir)` mirrors the Ubuntu `Program.cs` composition root
so the copied core runs byte-for-byte unchanged:

- **Clock**: `IClock` is a `SimulatedClock` anchored at real today, or at
  `$GOALFLOW_DATE` if set. No hardcoded anchor date.
- **The product pack**: `services.AddFamilyHub(dataDir)` — one line, and the only line here
  that knows what product this is. It brings the mock world (behind `IProductApiAdapter`), the
  eleven capability plugins, the `CapabilityManager`, the domain observers, the prechecks and
  the proactive suggester.
- **Harness components** (generic, no product types): `ArmedPolicies` **then**
  `IActivePolicy → ArmedPolicies`, `SafetyFilter`, `ApprovalCoordinator`, `Grounding`,
  `MonitorAdapt`, `PrecheckEngine`. The order matters and the first two are not optional:
  `ArmedPolicies` is the policy *store* and depends on nothing, split out of the *enforcer* so
  that a plugin reading the armed cap cannot close a dependency cycle back through the filter.
  Omit them and `BudgetPlugin` cannot be constructed — at runtime, on the first goal.
- **Kernel**: `AgentSettings` is populated from `OPENROUTER_API_KEY`
  (required — throws if missing), `OPENROUTER_BASE_URL`, and
  `OPENROUTER_MODEL`, then `GoalAgent.BuildKernel(settings, provider)` builds
  the `Kernel`.

`DeviceHost.CreateAgent(trace)` constructs a `GoalAgent` bound to a
caller-supplied `Trace` (kept off the container because the trace's `emit`
callback depends on the live WebSocket), pulling `Grounding`, `SafetyFilter`,
`ApprovalCoordinator`, `MonitorAdapt`, and `Clock` from the container.

`DeviceHost` also owns the minimal BCL-only `.env` loader (`LoadDotEnv`) and
log-level parsing (`LOG_LEVEL`).

## No Adapter Seam

There is no `IInventoryApi` / `GOALFLOW_ADAPTERS=mock|tizen` switch. The
world is the concrete `MockWorldStore` reading bundled `data/*.json` — that
works as-is on the Hub since it's just JSON. **Wiring real Tizen actuators is
future work behind the capability plugins**: change what a plugin *does*
(e.g. `NotifyPlugin` / `ReminderPlugin` / `ApplianceControlPlugin` calling
real `Tizen.Applications` APIs instead of writing to `MockWorldStore`), not a
separate adapter set.

## Manifest

`tizen-manifest.xml` declares a background service application:

```xml
<service-application appid="org.goalflow.deviceagent"
                     exec="GoalFlow.Device.Tizen.dll"
                     type="dotnet"
                     multiple="false"
                     taskmanage="false"
                     nodisplay="true">
```

Privileges:

- `http://tizen.org/privilege/internet` — the outbound WebSocket to the
  cloud hub.
- `http://tizen.org/privilege/mediastorage` — reserved for future real
  adapters that persist synced agent state or cache local recipe/device
  data.
- `http://tizen.org/privilege/alarm` — reserved for future real reminder
  actuators.
- `http://tizen.org/privilege/notification` — reserved for future real
  user-visible reminder notifications.

## Build And Deploy

Local .NET verification (no Tizen workload required to compile — Tizen.NET
ships `ServiceApplication` as a plain NuGet package):

```bash
cd /home/chachapranu/ashu/git/goal-flow-device-agent-tizen
dotnet restore
dotnet build -c Debug
```

This currently builds clean: 0 errors, 1 benign warning from Tizen's NUI
XAML build target inspecting the assembly.

Manual Tizen packaging and deploy, on a machine with Tizen Studio,
certificates, and a connected emulator or Family Hub:

```bash
dotnet workload install tizen
tizen build-cs -c Release
tizen package -t tpk -s <certificate-profile-name> -- .
sdb devices
tizen install -n org.goalflow.deviceagent-1.0.0.tpk -t <device-name>
```

The Tizen SDK is not installed in this dev environment, so `.tpk` production
and on-device deployment are done manually by a human; the Ubuntu build
(`../goal-flow-device-agent-ubuntu`) is the demo fallback (a one-line `WS_URL`
endpoint swap).

## Code-Sharing Strategy

The core is copied from `../goal-flow-device-agent-ubuntu` rather than
referenced, so this repo stays self-contained for Tizen packaging. That's
simple for a port, but it creates drift risk: fixes to the agent, contracts,
capability plugins, steering modules, or transport must be manually
re-copied into this repo. A production version should extract a shared
`GoalFlow.Core` library and have both the Ubuntu and Tizen hosts reference it
instead of copying files.
