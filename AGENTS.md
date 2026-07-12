# AGENTS.md — goal-flow-device-agent-tizen (coding-session guide)

Context for an AI/coding session in this repo. Read first — especially the warning.

## ⚠️ Status: FROZEN, PRE-v2 SNAPSHOT — do not mistake for current

This is the Tizen port of the GoalFlow device agent, but it is **frozen at an early
(pre-v2-SK-rewrite) architecture generation** (2 commits, never resynced). The
current source of truth for the device brain is
**`../goal-flow-device-agent-ubuntu`**, which has since moved to a completely
different internal shape. Do NOT treat this repo's classes as current.

Concretely, this repo still has the OLD v1 layout — `Harnesses/RulesPlanner.cs`,
`ScriptedPlanner.cs`, `LlmPlanner.cs`, `ApprovalBroker.cs`, `EffectExecutor.cs`,
`ChangeWatcher.cs`, `CapabilityManager.cs`, `Pipeline.cs`, `WorldState.cs` — **none
of which exist in the ubuntu repo anymore**. The ubuntu repo now uses
`Agent/GoalAgent.cs` + `Modules/Capabilities/*Plugin.cs` (SK plugins) +
`Modules/Steering/*` (`SafetyFilter`, `ApprovalCoordinator`, `MonitorAdapt`), is
**LLM-only** (no `GOALFLOW_PLANNER=rules|scripted|llm` — that model was removed), and
never hardcodes a clock date (this repo's docs/`CODE_GUIDE.md` mention a hardcoded
`VirtualClock anchor` — that violates the v2 "generic clock" invariant).

## What this repo is (intended role)

The eventual **Tizen Family Hub** deployment of the device agent. The plan: port the
`.NET 8 + Semantic Kernel` core from `goal-flow-device-agent-ubuntu` unchanged, and
swap only the platform edges — `ServiceApplication` host (`Program.cs`), a
`PipelineFactory` selected by `GOALFLOW_ADAPTERS=mock|tizen`, `TizenApis.cs` adapter
stubs, `tizen-manifest.xml`, and `GoalFlow.Device.Tizen.csproj` (net8.0 + Tizen.NET +
Semantic Kernel). The Ubuntu build stays as the demo fallback (one-line endpoint swap).

## Stack & build

- .NET 8 + Tizen.NET 12.x + Semantic Kernel. The Tizen SDK is NOT in this dev
  environment; the on-`.tpk`/on-Hub build is done manually by the human.
- Spike verified previously: `dotnet restore` + `dotnet build` succeed together.
  `GOALFLOW_ADAPTERS=mock` runs the portable core against mock adapters.

## If you are asked to work here

The main outstanding task is **(b) re-port from the current ubuntu `Agent/`/`Modules/`
tree** — bring this repo up to the v2 SK design, then re-swap the Tizen edges. Until
then, keep changes minimal and read `../goal-flow-device-agent-ubuntu/AGENTS.md` +
`docs/HARNESSES.md` for the real architecture.

## Conventions

- **Commit identity:** author as `ashuksingh11`
  (`31301999+ashuksingh11@users.noreply.github.com`). **Push only when asked.**
- **Workflow:** plan=Opus · design=Fable · coding=Codex CLI · browsing=Sonnet.
- The canonical wire contract lives in `../goal-flow-cloud-agent/CONTRACT.md`.
