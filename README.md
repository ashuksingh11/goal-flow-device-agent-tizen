# GoalFlow Device Agent — Tizen Port

This repo is the Tizen.NET host for the GoalFlow on-device agent. The portable
core has been copied into the repo root and is reused unchanged:

- `Contracts/`
- `Harnesses/`
- `Transport/WsClient.cs`
- `Pipeline.cs`
- `WorldState.cs`
- `data/*.json`

The Tizen-specific surface is deliberately small: `Program.cs` hosts the agent
as a headless `Tizen.Applications.ServiceApplication`, `PipelineFactory.cs`
wires the copied harness pipeline, `tizen-manifest.xml` declares the background
service package, and `Harnesses/Adapters/TizenApis.cs` marks the real Family Hub
integration points.

## Code-Sharing Strategy

The core is copied from `../goal-flow-device-agent-ubuntu` so this repo can be
self-contained for Tizen packaging. That keeps the port simple for the spike,
but it creates drift risk: fixes to contracts, harnesses, planners, or transport
must be manually copied between repos. A production version should extract a
shared `GoalFlow.Core` library and have both Ubuntu and Tizen hosts reference it.

## Adapter Selection

The mock JSON adapters remain the default:

```bash
GOALFLOW_ADAPTERS=mock
```

To exercise the Tizen integration boundary, set:

```bash
GOALFLOW_ADAPTERS=tizen
```

The Tizen adapters are stubs today and throw `NotImplementedException`; they
exist to show where Family Hub, SmartThings, Tizen calendar/alarm/notification,
and local storage APIs plug in behind the existing adapter interfaces.

Other useful environment variables:

```bash
WS_URL=wss://your-cloud.example/ws
GOALFLOW_PLANNER=rules        # rules | scripted | llm
GOALFLOW_DATA_DIR=data
OPENROUTER_API_KEY=...        # only needed for GOALFLOW_PLANNER=llm
```

## Build And Deploy

The local spike validates `dotnet restore` and `dotnet build`. Producing and
installing a `.tpk` requires a real Tizen SDK/workload and a configured emulator
or Family Hub device.

Example setup and build flow:

```bash
# Install Tizen Studio and configure certificates/devices in its UI.
# Install .NET/Tizen support if your SDK setup does not already include it.
dotnet workload install tizen

cd /home/chachapranu/ashu/git/goal-flow-device-agent-tizen
dotnet restore
dotnet build -c Release

# Package with the Tizen CLI from a machine with Tizen Studio installed.
tizen build-cs -c Release
tizen package -t tpk -s <certificate-profile-name> -- .

# Deploy to an emulator or Family Hub visible through sdb.
sdb devices
tizen install -n org.goalflow.deviceagent-1.0.0.tpk -t <device-name>
```

Exact packaging commands vary by Tizen Studio version and certificate profile.
The required package identity is in `tizen-manifest.xml`:
`org.goalflow.deviceagent`.

## Local Spike Result

On this machine, Tizen.NET `12.0.0.18510` and Semantic Kernel `1.*` restored and
built together for `net8.0`.

Key restore line:

```text
Restored /home/chachapranu/ashu/git/goal-flow-device-agent-tizen/GoalFlow.Device.Tizen.csproj (in 3.97 sec).
```

Key build lines:

```text
GoalFlow.Device.Tizen -> /home/chachapranu/ashu/git/goal-flow-device-agent-tizen/bin/Debug/net8.0/GoalFlow.Device.Tizen.dll
Build succeeded.
1 Warning(s)
0 Error(s)
```

The warning came from Tizen's NUI XAML build target inspecting the assembly:

```text
Tizen.NUI.XamlBuild.targets(46,3): warning : Assembly is obj/Debug/net8.0/GoalFlow.Device.Tizen.dll
```

No package-compatibility errors appeared between Tizen.NET and Semantic Kernel
in this local `net8.0` build. Runtime support on an actual Family Hub still
needs device testing. If Semantic Kernel cannot run on-Hub, keep
`GOALFLOW_PLANNER=rules` for deterministic local planning, or move LLM planning
to a paired device-side/cloud service while preserving the same safety gate and
effect execution flow.
