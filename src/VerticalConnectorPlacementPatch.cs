using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Prototypes;
using Mafi.Core.Terrain;
using Mafi.Localization;

namespace ElevationPP;

/// <summary>
/// Lets a pipe connector be placed directly onto the corner (elbow) tile of a pipe riser, cutting it
/// into the pipe without removing the pipe first — completing the vertical-connection feature of
/// <see cref="VerticalConnectorPortsPatch"/>.
///
/// Vanilla refuses this with "Not flat": <c>CanPlaceMiniZipperAt</c> requires all trajectory pivots
/// around the cut tile to be at the same height. That guard exists because the vanilla connector has
/// no vertical ports, so a cut next to a vertical segment would leave the riser side dangling. The
/// underlying cut machinery (<c>TransportTrajectory.CanCutOut</c>) already handles cuts at pivots
/// adjacent to vertical segments correctly: it produces a flat sub-pipe on one side and a riser
/// sub-pipe on the other, re-anchored to the tile next to the cut with its start/end direction
/// pointing at the cut tile — exactly what the connector's new vertical ports connect to.
///
/// This prefix takes over only when (a) the transport is a vertical-capable one (ZStepLength == 0,
/// i.e. pipes), (b) the position is NOT flat-around (vanilla handles all flat placements untouched),
/// and (c) the vertical ports patch is active. It then mirrors the vanilla checks minus the flatness
/// requirement: the position must be exactly at a trajectory pivot whose non-flat neighbors are
/// strictly vertical (never a ramp), the tile must be on-map, the cut must succeed, and no same-shape
/// connector may be adjacent. Placement execution (cut + build + port auto-connect) is untouched
/// vanilla code. Mid-riser tiles (not at a pivot) still report "Not flat".
///
/// This covers both placement paths: the connector building from the toolbar and the 1-tile pipe
/// click on an existing pipe (both funnel through CanPlaceMiniZipperAt).
/// </summary>
internal static class VerticalConnectorPlacementPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.verticalplacement";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_protosDbField;        // TransportsConstructionHelper.m_protosDb
    private static FieldInfo s_terrainField;         // TransportsConstructionHelper.m_terrainManager
    private static MethodInfo s_zippersAroundMethod; // TransportsConstructionHelper.areAnySameMiniZippersAround

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        if (!VerticalConnectorPortsPatch.IsActive)
        {
            Log.Info("Elevation++: vertical connector ports are not active; "
                + "elbow connector placement patch skipped.");
            return;
        }

        MethodBase target = AccessTools.Method(typeof(TransportsConstructionHelper), "CanPlaceMiniZipperAt");
        s_protosDbField = AccessTools.Field(typeof(TransportsConstructionHelper), "m_protosDb");
        s_terrainField = AccessTools.Field(typeof(TransportsConstructionHelper), "m_terrainManager");
        s_zippersAroundMethod = AccessTools.Method(typeof(TransportsConstructionHelper), "areAnySameMiniZippersAround");
        if (target == null || s_protosDbField == null || s_terrainField == null || s_zippersAroundMethod == null)
        {
            Log.Error("Elevation++: CanPlaceMiniZipperAt internals not resolved; "
                + "elbow connector placement patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(VerticalConnectorPlacementPatch), nameof(CanPlaceMiniZipperAtPrefix)));
            Log.Info("Elevation++: elbow connector placement patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply elbow connector placement patch: {ex}");
        }
    }

    private static bool CanPlaceMiniZipperAtPrefix(TransportsConstructionHelper __instance,
        Transport transport, Tile3i miniZipperPosition, ref CanPlaceMiniZipperAtResult result,
        ref LocStrFormatted error, Direction903d? bannedMiniZipperConnection, ref bool __result)
    {
        try
        {
            // Only vertical-capable transports (pipes); belts/molten keep full vanilla behavior.
            if (transport.Prototype.ZStepLength.Value != 0)
            {
                return true;
            }
            // Flat placements are handled by the untouched vanilla method.
            if (transport.IsFlatAround(miniZipperPosition))
            {
                return true;
            }
            __result = canPlaceAtVerticalPivot(__instance, transport, miniZipperPosition,
                ref result, ref error, bannedMiniZipperConnection);
            return false;
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Elevation++: elbow connector placement check failed (logged once): {ex}");
            }
            return true;
        }
    }

    /// <summary>
    /// Vanilla CanPlaceMiniZipperAt minus the flatness requirement: allows the cut when the position
    /// sits exactly on a trajectory pivot and every height-changing neighbor segment is strictly
    /// vertical (the connector's top/bottom ports will serve it).
    /// </summary>
    private static bool canPlaceAtVerticalPivot(TransportsConstructionHelper helper,
        Transport transport, Tile3i position, ref CanPlaceMiniZipperAtResult result,
        ref LocStrFormatted error, Direction903d? bannedConnection)
    {
        result = default(CanPlaceMiniZipperAtResult);

        if (!transport.Trajectory.TryGetLowPivotIndexFor(position, out int index, out bool isAtPivot))
        {
            error = Tr.TrAdditionError__NotFlat;
            return false;
        }
        ImmutableArray<Tile3i> pivots = transport.Trajectory.Pivots;
        if (isAtPivot)
        {
            // Corner (elbow) tile: every height-changing neighbor segment must be strictly vertical.
            if (index > 0 && pivots[index - 1].Z != position.Z && pivots[index - 1].Xy != position.Xy)
            {
                error = Tr.TrAdditionError__NotFlat;
                return false;
            }
            if (index < pivots.LastIndex && pivots[index + 1].Z != position.Z && pivots[index + 1].Xy != position.Xy)
            {
                error = Tr.TrAdditionError__NotFlat;
                return false;
            }
        }
        else if (pivots[index].Xy != pivots[index + 1].Xy || pivots[index].Z == pivots[index + 1].Z)
        {
            // Interior of a non-vertical segment: flat interiors are handled by vanilla (never reach
            // here); anything else (a belt ramp) stays refused.
            error = Tr.TrAdditionError__NotFlat;
            return false;
        }

        var protosDb = (ProtosDb)s_protosDbField.GetValue(helper);
        Option<MiniZipperProto> zipperProto = protosDb.Get<MiniZipperProto>(
            IdsCore.Transports.GetMiniZipperIdFor(transport.Prototype.PortsShape.Id));
        if (zipperProto.IsNone)
        {
            error = Tr.TrAdditionError__NoMiniZipper;
            return false;
        }

        var terrain = (TerrainManager)s_terrainField.GetValue(helper);
        if (terrain.IsOffLimitsOrInvalid(position.Tile2i))
        {
            error = Tr.AdditionError__OutsideOfMap;
            return false;
        }

        CanCutOutTransportAtResult cutResult;
        if (isAtPivot)
        {
            // The vanilla cut machinery handles pivot cuts adjacent to vertical segments correctly.
            if (!TransportsConstructionHelper.CanCutOutTransportAt(transport, position, out cutResult, out error))
            {
                return false;
            }
        }
        else if (!tryCutMidRiser(transport, position, index, out cutResult, out error))
        {
            return false;
        }

        if (bannedConnection.HasValue)
        {
            if (cutResult.StartSubTransport.HasValue
                && -cutResult.StartSubTransport.Value.EndDirection.ToDirection903d() == bannedConnection.Value)
            {
                error = Tr.TrAdditionError__InvalidConnection;
                return false;
            }
            if (cutResult.EndSubTransport.HasValue
                && -cutResult.EndSubTransport.Value.StartDirection.ToDirection903d() == bannedConnection.Value)
            {
                error = Tr.TrAdditionError__InvalidConnection;
                return false;
            }
        }

        object[] args = { position, zipperProto.Value, null };
        if ((bool)s_zippersAroundMethod.Invoke(helper, args))
        {
            var neighbor = (MiniZipper)args[2];
            error = Tr.TrAdditionError__TooCloseToOtherMiniZipper.Format(
                neighbor.Prototype.Strings.Name.TranslatedString);
            return false;
        }

        error = LocStrFormatted.Empty;
        result = new CanPlaceMiniZipperAtResult(cutResult, zipperProto.Value);
        return true;
    }

    /// <summary>
    /// Cuts one tile out of the interior of a vertical segment (pivots[index] -> pivots[index + 1]),
    /// something the vanilla cut cannot express. Produces a lower and an upper sub-pipe, each
    /// re-anchored to the tile adjacent to the cut with its start/end direction pointing at the cut
    /// tile — which is exactly where the connector's bottom/top ports will connect. Mirrors the pivot
    /// handling of TransportTrajectory.CanCutOut (directions point away from each trajectory).
    /// </summary>
    private static bool tryCutMidRiser(Transport transport, Tile3i position, int index,
        out CanCutOutTransportAtResult cutResult, out LocStrFormatted error)
    {
        cutResult = default(CanCutOutTransportAtResult);
        TransportTrajectory trajectory = transport.Trajectory;
        ImmutableArray<Tile3i> pivots = trajectory.Pivots;
        int dir = Math.Sign(pivots[index + 1].Z - pivots[index].Z);
        var step = new RelTile3i(0, 0, dir);
        Tile3i beforeCut = position - step;
        Tile3i afterCut = position + step;

        // Trajectory part before the cut: pivots[0..index], extended down/up to the tile adjacent
        // to the cut (unless that pivot already is adjacent), ending pointed at the cut tile.
        bool appendBefore = pivots[index] != beforeCut;
        var startPivots = new Tile3i[index + 1 + (appendBefore ? 1 : 0)];
        for (int i = 0; i <= index; i++)
        {
            startPivots[i] = pivots[i];
        }
        if (appendBefore)
        {
            startPivots[startPivots.Length - 1] = beforeCut;
        }
        if (!TransportTrajectory.TryCreateFromPivots(transport.Prototype,
            new ImmutableArray<Tile3i>(startPivots), trajectory.StartDirection, step,
            out TransportTrajectory startSub, out string _))
        {
            error = Tr.TrAdditionError__InvalidTransportCut;
            return false;
        }

        // Trajectory part after the cut: the tile adjacent to the cut (unless pivots[index + 1]
        // already is adjacent) plus pivots[index + 1..], starting pointed at the cut tile.
        bool prependAfter = pivots[index + 1] != afterCut;
        int tailCount = pivots.Length - (index + 1);
        var endPivots = new Tile3i[tailCount + (prependAfter ? 1 : 0)];
        int offset = 0;
        if (prependAfter)
        {
            endPivots[0] = afterCut;
            offset = 1;
        }
        for (int i = 0; i < tailCount; i++)
        {
            endPivots[offset + i] = pivots[index + 1 + i];
        }
        if (!TransportTrajectory.TryCreateFromPivots(transport.Prototype,
            new ImmutableArray<Tile3i>(endPivots), -step, trajectory.EndDirection,
            out TransportTrajectory endSub, out string _))
        {
            error = Tr.TrAdditionError__InvalidTransportCut;
            return false;
        }

        error = LocStrFormatted.Empty;
        cutResult = new CanCutOutTransportAtResult(position, transport, startSub, endSub);
        return true;
    }
}
