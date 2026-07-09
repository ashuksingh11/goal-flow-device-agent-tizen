# Code Guide — GoalFlow Device Agent Tizen

This repo is a Tizen.NET port shell around the copied GoalFlow device-agent
core. The harness pipeline, contracts, transport, world model, and seed data are
the same code copied from `../goal-flow-device-agent-ubuntu`; the Tizen port
adds only host, packaging, adapter stubs, and documentation.

## File Map

```text
GoalFlow.Device.Tizen.csproj       # net8.0 Tizen.NET service project
Program.cs                         # headless ServiceApplication host
PipelineFactory.cs                 # Ubuntu BuildPipeline wiring extracted for Tizen
tizen-manifest.xml                 # background service package manifest
Contracts/                         # copied portable wire contracts
Harnesses/                         # copied portable harnesses and planners
Harnesses/Adapters/*Api.cs         # copied mock adapters
Harnesses/Adapters/TizenApis.cs    # Tizen adapter stubs
Transport/WsClient.cs              # copied outbound WebSocket transport
Pipeline.cs, WorldState.cs         # copied portable orchestration core
data/*.json                        # copied mock world and fixtures
```

## Host Shape

`GoalFlowService` in `Program.cs` subclasses
`Tizen.Applications.ServiceApplication`. It does not create a window or use NUI.

Lifecycle:

- `OnCreate()` loads `.env`, builds the pipeline through `PipelineFactory`,
  resolves `WS_URL` with default `ws://localhost:8000/ws`, creates `WsClient`,
  and starts the reconnect loop on a background `Task`.
- `OnTerminate()` cancels the token, waits briefly for the loop to exit, and
  disposes the WebSocket client.
- `Main(string[] args)` calls `new GoalFlowService().Run(args)`.

## Pipeline Wiring

`PipelineFactory.Build()` mirrors the Ubuntu `Program.cs` wiring:

- `VirtualClock` anchor remains `2026-07-12T09:00:00+00:00`.
- Default planner is `rules`.
- `GOALFLOW_PLANNER` can select `rules`, `scripted`, or `llm`.
- `RulesPlanner`, `ScriptedPlanner`, and `LlmPlanner` are wired the same way as
  Ubuntu.
- `Grounding`, `SafetyGate`, `ApprovalBroker`, `EffectExecutor`, `Scheduler`,
  `ChangeWatcher`, and `InMemoryTrace` are constructed from the copied core.
- `DataSeedStore` and `CopyDirectory` are preserved for reset/simulation parity.

The host receives a `PipelineHost` record so it can pass both `Pipeline` and
`ITrace` into `WsClient`.

## Adapter Switch

`GOALFLOW_ADAPTERS` selects the adapter set:

```bash
GOALFLOW_ADAPTERS=mock   # default: copied JSON-backed Mock*Api adapters
GOALFLOW_ADAPTERS=tizen  # Tizen*Api stubs; currently throw NotImplementedException
```

The Tizen stubs implement the same interfaces:

- `TizenInventoryApi : IInventoryApi`
- `TizenCalendarApi : ICalendarApi`
- `TizenRecipeApi : IRecipeApi`
- `TizenShoppingListApi : IShoppingListApi`
- `TizenReminderApi : IReminderApi`

Each method has a TODO naming the likely integration point: SmartThings/Family
Hub device APIs, Tizen calendar/alarm/notification APIs, or a synced GoalFlow
local store.

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

- `http://tizen.org/privilege/internet` for the outbound WebSocket.
- `http://tizen.org/privilege/mediastorage` for future local cache/state
  adapters.
- `http://tizen.org/privilege/alarm` for future reminder actuators.
- `http://tizen.org/privilege/notification` for future user-visible reminder
  notifications.

## Build And Deploy

Local .NET verification:

```bash
cd /home/chachapranu/ashu/git/goal-flow-device-agent-tizen
dotnet restore
dotnet build -c Release
```

Manual Tizen packaging and deploy, on a machine with Tizen Studio, certificates,
and a connected emulator or Family Hub:

```bash
dotnet workload install tizen
tizen build-cs -c Release
tizen package -t tpk -s <certificate-profile-name> -- .
sdb devices
tizen install -n org.goalflow.deviceagent-1.0.0.tpk -t <device-name>
```

The Tizen SDK is not installed in this environment, so `.tpk` production and
device deployment were not attempted here.

## Semantic Kernel Feasibility Spike

Commands run in this repo:

```bash
dotnet restore
dotnet build
```

Restore result:

```text
Determining projects to restore...
Restored /home/chachapranu/ashu/git/goal-flow-device-agent-tizen/GoalFlow.Device.Tizen.csproj (in 3.97 sec).
```

Build result:

```text
Determining projects to restore...
Restored /home/chachapranu/ashu/git/goal-flow-device-agent-tizen/GoalFlow.Device.Tizen.csproj (in 3.97 sec).
Tizen.NUI.XamlBuild.targets(46,3): warning : Assembly is obj/Debug/net8.0/GoalFlow.Device.Tizen.dll
GoalFlow.Device.Tizen -> /home/chachapranu/ashu/git/goal-flow-device-agent-tizen/bin/Debug/net8.0/GoalFlow.Device.Tizen.dll

Build succeeded.
Tizen.NUI.XamlBuild.targets(46,3): warning : Assembly is obj/Debug/net8.0/GoalFlow.Device.Tizen.dll
    1 Warning(s)
    0 Error(s)

Time Elapsed 00:00:07.92
```

Observation: package restore and compile succeeded for Tizen.NET
`12.0.0.18510`, Semantic Kernel `1.*`, and `net8.0`. There were no local
package-compatibility errors.

Risk: compile success does not prove Semantic Kernel can execute correctly on a
Family Hub runtime, especially if runtime networking, reflection, trimming,
cryptography, or HTTP handler behavior differs from desktop .NET.

Fallback: run `GOALFLOW_PLANNER=rules` for deterministic on-device planning, or
keep the deterministic safety/effect pipeline on the Hub while moving LLM
planning to a paired local/cloud service.
