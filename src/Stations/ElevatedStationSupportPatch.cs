using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Trains;

namespace ElevationPP.Stations;

/// <summary>
/// Makes elevated stations count as SUPPORT in the train-track graph, like ground stations do.
///
/// A vanilla station anchors the support chain of adjacent track because its blocks are at
/// ground level (addGroundSupport). An elevated station's blocks are neither grounded nor —
/// for the root/fuel protos, whose track pieces have CanBeElevatedOnSupports == false — even
/// included in the support computation, so a station spliced into an elevated line BREAKS the
/// support chain: spans that used to reach a pillar through the station's stretch suddenly
/// exceed the support distance and flag as unsupported, even though the station itself stands
/// firmly on its ring of transport pillars.
///
/// The graph's anchor model is simply "a block whose SupportDistance is zero": recomputation
/// (clearSupportRecursive) collects zero-blocks as support sources and re-propagates from them
/// rather than clearing them. So after a station's track piece is registered to the graph
/// (RegisterEntityToGraph — also runs for every entity on save load), a postfix sets every
/// block of the mod's station protos to zero via the graph's own addSupportInternal, which also
/// propagates the support to neighbouring pieces. The anchors live exactly as long as the
/// entity's graph registration.
/// </summary>
internal static class ElevatedStationSupportPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.stationsupport";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    private static MethodInfo s_addSupportInternal;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        // The methods live on the generic base; Harmony requires patching the declared method
        // on the constructed base type, not the subclass.
        Type graphBase = typeof(TrainTracksGraphManagerBase<IEntityWithTrainTrack>);
        MethodBase register = AccessTools.Method(graphBase, "RegisterEntityToGraph");
        s_addSupportInternal = AccessTools.Method(graphBase, "addSupportInternal");
        if (register == null || s_addSupportInternal == null)
        {
            Log.Error("Elevation++: track graph internals not resolved; "
                + "elevated station support patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(register, postfix: new HarmonyMethod(
                typeof(ElevatedStationSupportPatch), nameof(RegisterEntityToGraphPostfix)));
            Log.Info("Elevation++: elevated station support patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply elevated station support patch: {ex}");
        }
    }

    private static void RegisterEntityToGraphPostfix(object __instance, object trackEntity,
        Set<TrainTrackId> trackIdsWithChangedSupport, TrainTrackId __result)
    {
        try
        {
            var entity = trackEntity as IEntityWithTrainTrack;
            object proto = (entity as IEntity)?.Prototype;
            if (!(proto is ElevatedStationRootProto || proto is ElevatedStationModuleProto
                || proto is ElevatedStationFuelProto))
            {
                return;
            }
            int blockCount = entity.GetTransformedBlockData().Length;
            for (int i = 0; i < blockCount; i++)
            {
                s_addSupportInternal.Invoke(__instance, new object[]
                {
                    i, __result, RelTile1f.Zero, trackIdsWithChangedSupport,
                    /* isForElectrification: */ false, /* recursionDepth: */ 0,
                    /* ignoreLocalSupportDistanceUnchanged: */ false,
                });
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: elevated station support patch failed (logged once): {ex}");
        }
    }
}
