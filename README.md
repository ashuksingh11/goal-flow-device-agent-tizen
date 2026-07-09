# GoalFlow Device Agent — Tizen (deferred port)

**Status: placeholder.** This repo will hold the Tizen C#/.NET port of the
GoalFlow on-device agent for the Samsung Family Hub. The port is deliberately
deferred; all active development happens in
[`goal-flow-device-agent-ubuntu`](../goal-flow-device-agent-ubuntu).

## Port strategy

The Ubuntu agent was designed so this port is an **adapter swap, not a
rewrite**. The Semantic Kernel / .NET core — the harness pipeline
(TaskManager, PreCheck, CapabilityManager, Grounding, Planner, SafetyGate,
ApprovalBroker, EffectExecutor, Scheduler, ChangeWatcher, Trace), the
CONTRACT v0 C# mirror, and the `Pipeline` orchestrator — carries over
unchanged. Only the implementations behind the injectable interfaces swap:

| Interface (ubuntu repo) | Ubuntu implementation | Tizen implementation (here) |
| --- | --- | --- |
| `IInventoryApi`, `ICalendarApi`, `IRecipeApi`, `IShoppingListApi`, `IReminderApi` | Mock JSON files in `data/` | Tizen / Family Hub device APIs |
| local storage (inside the mock adapters) | flat JSON files | Tizen application local storage |
| `IClock` | `VirtualClock` (demo time-travel) | platform clock adapter |

Transport needs no port at all: the device opens one outbound WebSocket to
the cloud hub via `System.Net.WebSockets.ClientWebSocket`, which runs on both
Linux and Tizen.

## Demo fallback

The Linux (Ubuntu) build remains the demo fallback throughout: pointing it at
the cloud is a **one-line endpoint swap** (the WS URI), so a working demo
never depends on this port landing.

## Open spike

- **Validate that Microsoft Semantic Kernel runs on the Family Hub's
  .NET/Tizen runtime.** If it does not, the `LlmPlanner` stays cloud-side or
  the device runs `RulesPlanner` only (the design already allows either via
  `IPlanner` selection).

Nothing else lives here yet by design.
