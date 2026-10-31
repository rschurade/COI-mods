---
name: excavatorpp-mod
description: Excavator++ mod (continuous mining with partial buckets) at D:\projects\privat\mods\coi\mods\ExcavatorPP; 0.1.2 released 2026-10-20 (Kayser's ATD compatibility)
metadata: 
  node_type: memory
  type: project
  originSessionId: 519a8531-c89b-4fec-8472-9f0ee2e8aed4
  modified: 2026-10-20T09:05:20.968Z
---

Excavator++ (`ExcavatorPP`, display "Excavator++") lives in the mods monorepo at
D:\projects\privat\mods\coi\mods\ExcavatorPP — see [[git-workflow]]. Built like the other
mods via its own build.ps1 (same MSBuild pattern as [[build-command]]).

What it does: vanilla excavators must empty the bucket before mining again, which
creates a self-perpetuating desync (truck leaves full, leftover units strand the
excavator waiting for another truck). The mod lets excavators keep mining with a
partial bucket and unload only when full; leftover cargo carries across mining-job
boundaries (mixed products are fine — the bucket and trucks both hold mixed cargo).

Implementation: six Harmony patches in src/ContinuousMiningPatch.cs, applied
all-or-nothing with rollback (transpilers on MiningJob.handleDecideMiningOnReachableTile,
Excavator.handleLoadTruck, MiningJob.cleanup; prefixes on
ExcavatorJobProvider.tryEnqueueCleaningJobIfNotClean and Excavator.handleWaitingForTruck;
postfix on Excavator.initState to unstick saved desyncs). Key vanilla traps discovered:
MiningJob.cleanup() and CleanExcavatorJob DESTROY leftover bucket cargo, and the
force-unload flag drains until empty — all three had to be patched or the stall returns.
v0.1.0's flush-at-job-end approach caused waiting at every finished designation; v0.1.1
(2026-08-26, tested in-game by the user, committed+pushed) carries cargo across jobs
instead. Mafi asserts only log (MAFI_ASSERTIONS), they never throw.

0.1.2 RELEASED 2026-10-20 (commit `60875ec`, tag `ExcavatorPP-0.1.2`, pushed; zip in COI_MODS;
tested in-game by the user): compatibility with Kayser's Automatic Terrain Designations
(mod id `AutoTerrainDesignations`) 0.9.3+, whose "Default Mine" feature ALSO transpiles
MiningJob.cleanup (reroutes ClearCargoImmediately via its own ClearMiningCargo, which still
calls ClearCargoImmediately for excavators it doesn't own) and prefixes
Excavator.handleWaitingForTruck / ClearCargoImmediately. ATD patches first → our IL-count check
found 0 calls → whole patch set rolled back silently (user-reported "stops working"). Fix: no
cleanup transpiler any more — prefix+finalizer on MiningJob.cleanup set a depth counter, a prefix
on Excavator.ClearCargoImmediately skips the clear only while it is set. Lesson: prefer
call-scoped flags over transpilers on methods other mods also rewrite; the rollback log now
prints the innermost exception (Harmony wraps transpiler throws in TargetInvocationException).

0.1.3 RELEASED 2026-10-29 (commit `ec904a9`, tag `ExcavatorPP-0.1.3`, pushed; min/max game 0.8.8): 0.8.8 replaced the excavator's `VehicleCargo m_cargo` with a mixed product buffer (`m_cargoBuffer`), so handleLoadTruck's two empty checks are now the excavator's own `IsEmpty`; transpiler retargeted (second call → ShouldEndUnloadSession(Excavator)). Everything else (cleanup → ClearCargoImmediately, decide-gate IsNotEmpty/IsEmpty, fields) unchanged in 0.8.8.
