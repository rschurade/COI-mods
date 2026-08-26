using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mafi;
using Mafi.Core.Vehicles;
using Mafi.Core.Vehicles.Excavators;
using Mafi.Core.Vehicles.Jobs;

namespace ExcavatorPP;

/// <summary>
/// Lets excavators keep mining with a partially filled bucket instead of idling until a truck
/// has taken the leftover. Six patches that only work together (all-or-nothing):
///
/// 1. Transpiler on <c>MiningJob.handleDecideMiningOnReachableTile</c>: the vanilla
///    "unload whenever the bucket is not empty" gate becomes "unload when the bucket is full".
///    A scoop that came up short is topped up by the next scoop — <c>Excavator.MineMixedAt</c>
///    already caps every scoop at the remaining bucket capacity, and the bucket carries mixed
///    products fine. The same transpiler defuses the handler's now-wrong "bucket is empty
///    before mining" assert.
///
/// 2. Transpiler on <c>Excavator.handleLoadTruck</c>: vanilla ends an unload session only once
///    the bucket is EMPTY, which would re-create the stall this mod removes (dump 15 into the
///    partial truck, then wait idle for another truck to take the last 5). With
///    <see cref="ShouldEndUnloadSession"/> a session ends after every dump while a true job is
///    active: the excavator goes straight back to digging and the remainder rides along in the
///    bucket. With no job (cancel flows, the final flush of patch 4) the session drains the
///    bucket like vanilla.
///
/// 3. Transpiler on <c>MiningJob.cleanup</c>: vanilla destroys whatever is still in the bucket
///    whenever a mining job ends — harmless in vanilla (the bucket is always empty), fatal
///    here. The cleanup keeps the cargo instead, so the bucket content simply carries over into
///    the next mining job (e.g. 2 leftover coal ride along into the limestone designation next
///    door and the next scoop tops the bucket up to a full mixed load).
///
/// 4. Prefix on <c>ExcavatorJobProvider.tryEnqueueCleaningJobIfNotClean</c>: when no mining job
///    is available at all, vanilla enqueues a <c>CleanExcavatorJob</c>, whose first act is to
///    destroy the bucket content. With cargo aboard and a mine tower assigned, the prefix
///    orders a normal unload to a truck instead and postpones the cleaning until the bucket is
///    empty, so the last partial bucket of a finished mining area reaches a truck.
///
/// 5. Prefix on <c>Excavator.handleWaitingForTruck</c>: safety net for the unload flows above —
///    if the excavator waits for a truck while its truck queue got disabled on some path (e.g.
///    the refuel-self flow disables it), no truck would ever be dispatched; the prefix
///    re-enables the queue so the wait always resolves.
///
/// 6. Postfix on <c>Excavator.initState</c> (runs after save-load): a save can contain an
///    excavator frozen mid vanilla-desync — waiting for a truck to take a partial bucket with
///    the serialized force-unload flag set. The postfix clears the flag for partial (not full)
///    buckets and sends the excavator back to Idle, so it resumes mining immediately instead of
///    waiting for a truck to collect a few leftover units.
///
/// Known edge (vanilla parity): a mining job cancelled outright — mine tower destroyed or the
/// excavator unassigned — can still lose the bucket content to the cleaning job, exactly as
/// vanilla loses a full bucket when cancelled right after a scoop.
/// </summary>
internal static class ContinuousMiningPatch
{
    private const string HARMONY_ID = "com.roest.excavatorpp.continuousmining";

    private static bool s_applied;

    private static MethodInfo s_keepTruckQueueEnabled;
    private static AccessTools.FieldRef<Excavator, bool> s_forceUnloadToTruck;
    private static AccessTools.FieldRef<Excavator, ExcavatorState> s_state;
    private static AccessTools.FieldRef<Excavator, ExcavatorState> s_previousState;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodInfo decideMining = AccessTools.Method(typeof(MiningJob),
            "handleDecideMiningOnReachableTile");
        MethodInfo loadTruck = AccessTools.Method(typeof(Excavator), "handleLoadTruck");
        MethodInfo cleanup = AccessTools.Method(typeof(MiningJob), "cleanup");
        MethodInfo tryEnqueueCleaning = AccessTools.Method(typeof(ExcavatorJobProvider),
            "tryEnqueueCleaningJobIfNotClean");
        MethodInfo waitingForTruck = AccessTools.Method(typeof(Excavator),
            "handleWaitingForTruck");
        MethodInfo initState = AccessTools.Method(typeof(Excavator), "initState");
        s_keepTruckQueueEnabled = AccessTools.Method(typeof(Excavator),
            "KeepTruckQueueEnabled");
        try
        {
            s_forceUnloadToTruck = AccessTools.FieldRefAccess<Excavator, bool>(
                "m_forceUnloadToTruck");
            s_state = AccessTools.FieldRefAccess<Excavator, ExcavatorState>("m_state");
            s_previousState = AccessTools.FieldRefAccess<Excavator, ExcavatorState>(
                "m_previousState");
        }
        catch (Exception ex)
        {
            Log.Error("Excavator++: excavator fields not resolved; continuous mining disabled "
                + $"(vanilla behavior kept): {ex.Message}");
            return;
        }
        if (decideMining == null || loadTruck == null || cleanup == null
            || tryEnqueueCleaning == null || waitingForTruck == null || initState == null
            || s_keepTruckQueueEnabled == null)
        {
            Log.Error("Excavator++: excavator/mining job methods not resolved; continuous "
                + "mining disabled (vanilla behavior kept).");
            return;
        }

        var harmony = new Harmony(HARMONY_ID);
        try
        {
            harmony.Patch(decideMining, transpiler: new HarmonyMethod(
                typeof(ContinuousMiningPatch), nameof(DecideMiningTranspiler)));
            harmony.Patch(loadTruck, transpiler: new HarmonyMethod(
                typeof(ContinuousMiningPatch), nameof(LoadTruckTranspiler)));
            harmony.Patch(cleanup, transpiler: new HarmonyMethod(
                typeof(ContinuousMiningPatch), nameof(CleanupTranspiler)));
            harmony.Patch(tryEnqueueCleaning, prefix: new HarmonyMethod(
                typeof(ContinuousMiningPatch), nameof(CleaningJobPrefix)));
            harmony.Patch(waitingForTruck, prefix: new HarmonyMethod(
                typeof(ContinuousMiningPatch), nameof(WaitingForTruckPrefix)));
            harmony.Patch(initState, postfix: new HarmonyMethod(
                typeof(ContinuousMiningPatch), nameof(InitStatePostfix)));
            Log.Info("Excavator++: continuous mining patch applied.");
        }
        catch (Exception ex)
        {
            // The patches are only correct together: a partial application either does nothing
            // or destroys cargo at job end. Roll back everything.
            harmony.UnpatchAll(HARMONY_ID);
            Log.Error("Excavator++: failed to apply continuous mining patch, all patches "
                + $"rolled back (vanilla behavior kept): {ex.Message}");
        }
    }

    /// <summary>
    /// In <c>handleDecideMiningOnReachableTile</c>: <c>m_excavator.IsNotEmpty</c> (the unload
    /// gate) becomes <see cref="UnloadOnlyWhenFull"/>, and <c>m_excavator.IsEmpty</c> (only
    /// used by the pre-mining assert) becomes constant true. Both must match exactly once —
    /// the truck's IsNotEmpty further down is a different method and is left alone.
    /// </summary>
    private static IEnumerable<CodeInstruction> DecideMiningTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        int unloadGates = ReplaceCall(code,
            AccessTools.DeclaredPropertyGetter(typeof(Excavator), nameof(Excavator.IsNotEmpty)),
            AccessTools.Method(typeof(ContinuousMiningPatch), nameof(UnloadOnlyWhenFull)));
        int asserts = ReplaceCall(code,
            AccessTools.DeclaredPropertyGetter(typeof(Excavator), nameof(Excavator.IsEmpty)),
            AccessTools.Method(typeof(ContinuousMiningPatch), nameof(SkipIsEmptyAssert)));
        if (unloadGates != 1 || asserts != 1)
        {
            throw new InvalidOperationException("handleDecideMiningOnReachableTile has "
                + $"{unloadGates}x Excavator.IsNotEmpty and {asserts}x Excavator.IsEmpty "
                + "(expected 1 and 1); the game code changed.");
        }
        return code;
    }

    /// <summary>
    /// In <c>handleLoadTruck</c>: the second <c>m_cargo.IsEmpty</c> — the one deciding whether
    /// the force-unload session is over after a dump — becomes
    /// <see cref="ShouldEndUnloadSession"/>. The first one (the entry bail-out when the bucket
    /// is already empty) stays vanilla.
    /// </summary>
    private static IEnumerable<CodeInstruction> LoadTruckTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo cargoIsEmpty = AccessTools.DeclaredPropertyGetter(typeof(VehicleCargo),
            nameof(VehicleCargo.IsEmpty));
        int total = CountCalls(code, cargoIsEmpty);
        if (total != 2)
        {
            throw new InvalidOperationException("handleLoadTruck has "
                + $"{total}x VehicleCargo.IsEmpty (expected 2); the game code changed.");
        }
        ReplaceCallAddingThis(code, cargoIsEmpty,
            AccessTools.Method(typeof(ContinuousMiningPatch), nameof(ShouldEndUnloadSession)),
            skipOccurrences: 1);
        return code;
    }

    /// <summary>
    /// In <c>MiningJob.cleanup</c>: <c>m_excavator.ClearCargoImmediately()</c> becomes a no-op
    /// so the bucket content survives the end of a mining job and carries over into the next.
    /// </summary>
    private static IEnumerable<CodeInstruction> CleanupTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        int cleared = ReplaceCall(code,
            AccessTools.Method(typeof(Excavator), nameof(Excavator.ClearCargoImmediately)),
            AccessTools.Method(typeof(ContinuousMiningPatch), nameof(KeepCargoOnJobEnd)));
        if (cleared != 1)
        {
            throw new InvalidOperationException("MiningJob.cleanup has "
                + $"{cleared}x Excavator.ClearCargoImmediately (expected 1); the game code "
                + "changed.");
        }
        return code;
    }

    /// <summary>Replaces the vanilla unload gate ("bucket not empty") in the decide step.</summary>
    private static bool UnloadOnlyWhenFull(Excavator excavator)
    {
        return excavator.IsFull;
    }

    private static bool SkipIsEmptyAssert(Excavator excavator)
    {
        return true;
    }

    private static void KeepCargoOnJobEnd(Excavator excavator)
    {
    }

    /// <summary>
    /// Replaces the vanilla "unload session over?" check ("bucket empty") after a dump. With a
    /// true job (mining) active the session ends immediately so the excavator digs on with the
    /// remainder in the bucket; the decide gate or the final flush re-arm the unload whenever
    /// it is actually needed. Without a true job the bucket drains fully, as vanilla.
    /// </summary>
    private static bool ShouldEndUnloadSession(VehicleCargo cargo, Excavator excavator)
    {
        return cargo.IsEmpty || excavator.HasTrueJob;
    }

    /// <summary>
    /// Runs instead of enqueueing the cargo-destroying <c>CleanExcavatorJob</c> while the
    /// bucket still holds material and a mine tower can send trucks: unload to a truck first,
    /// clean later (the provider re-runs this once the bucket is empty).
    /// </summary>
    private static bool CleaningJobPrefix(Excavator excavator, ref bool __result)
    {
        if (excavator.IsEmpty || !excavator.MineTower.HasValue)
        {
            return true;
        }
        KeepTruckQueueEnabled(excavator);
        excavator.UnloadToTruck();
        __result = false;
        return false;
    }

    /// <summary>
    /// While waiting to hand cargo to a truck, make sure trucks can actually be dispatched —
    /// some vanilla paths (e.g. refuel-self) disable the truck queue after an unload was
    /// already ordered, which would leave the excavator waiting forever.
    /// </summary>
    private static void WaitingForTruckPrefix(Excavator __instance)
    {
        if (__instance.IsNotEmpty && __instance.MineTower.HasValue
            && !__instance.TruckQueue.IsEnabled)
        {
            KeepTruckQueueEnabled(__instance);
        }
    }

    /// <summary>
    /// After save-load: an excavator frozen waiting for a truck with a partial (not full)
    /// bucket — the vanilla desync this mod removes, or an unload ordered by an older version
    /// of this mod — resumes mining instead; its leftover cargo rides along in the bucket.
    /// </summary>
    private static void InitStatePostfix(Excavator __instance)
    {
        if (!s_forceUnloadToTruck(__instance) || __instance.IsFull || __instance.IsEmpty)
        {
            return;
        }
        s_forceUnloadToTruck(__instance) = false;
        if (s_state(__instance) == ExcavatorState.WaitingForTruck)
        {
            s_state(__instance) = ExcavatorState.Idle;
            s_previousState(__instance) = ExcavatorState.Idle;
        }
    }

    private static void KeepTruckQueueEnabled(Excavator excavator)
    {
        // Internal on Excavator, hence the reflection call: enables the truck queue and gives
        // it a fresh keep-alive duration so trucks get dispatched to this excavator.
        s_keepTruckQueueEnabled.Invoke(excavator, new object[] { 30.Seconds() });
    }

    private static int CountCalls(List<CodeInstruction> code, MethodInfo target)
    {
        int count = 0;
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.Calls(target))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Replaces every call to <paramref name="target"/> (after skipping
    /// <paramref name="skipOccurrences"/> of them) with `ldarg.0` + a call to the static
    /// <paramref name="replacement"/>, which receives the original call's instance as its first
    /// argument and the patched method's `this` as its second. Returns the replacement count.
    /// </summary>
    private static int ReplaceCallAddingThis(List<CodeInstruction> code, MethodInfo target,
        MethodInfo replacement, int skipOccurrences = 0)
    {
        int seen = 0;
        int replaced = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(target))
            {
                continue;
            }
            if (seen++ < skipOccurrences)
            {
                continue;
            }
            var loadThis = new CodeInstruction(OpCodes.Ldarg_0);
            loadThis.labels.AddRange(code[i].labels);
            loadThis.blocks.AddRange(code[i].blocks);
            code[i] = loadThis;
            code.Insert(i + 1, new CodeInstruction(OpCodes.Call, replacement));
            i++;
            replaced++;
        }
        return replaced;
    }

    /// <summary>
    /// Replaces every call to <paramref name="target"/> with a call to the static
    /// <paramref name="replacement"/> taking the original call's instance as its only
    /// argument. Returns the replacement count.
    /// </summary>
    private static int ReplaceCall(List<CodeInstruction> code, MethodInfo target,
        MethodInfo replacement)
    {
        int replaced = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (!code[i].Calls(target))
            {
                continue;
            }
            var call = new CodeInstruction(OpCodes.Call, replacement);
            call.labels.AddRange(code[i].labels);
            call.blocks.AddRange(code[i].blocks);
            code[i] = call;
            replaced++;
        }
        return replaced;
    }
}
