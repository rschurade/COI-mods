using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Terrain;

namespace ElevationPP;

/// <summary>
/// Lets an elevated belt/pipe be held up by the supported connectors at its ends, so the auto-built
/// pillar in a short segment between two connectors (balancers, mini-zippers, machine ports) is no
/// longer mandatory — and can be removed.
///
/// The game computes transport support per <see cref="TransportTrajectory"/>: a tile counts as
/// supported only if it is on the ground or has a pillar. A connector splits a belt into separate
/// transport entities and is NOT itself a support point, so an elevated segment between two
/// connectors whose only support is one pillar is considered fully unsupported if that pillar is
/// removed — which both blocks removal ("Pillars cannot be removed") and would collapse the segment
/// at runtime. <see cref="TransportsConstructionHelper"/>.FindUnsupportedRegionAround is the single
/// choke point for both the removal-redundancy check and the runtime collapse check, so adjusting it
/// fixes both consistently: the belt stays up after the pillar is removed instead of collapsing.
///
/// This postfix treats a trajectory endpoint as a support boundary when the tile it connects to (the
/// endpoint position plus the trajectory's outward Start/End direction) is occupied by a constructed
/// <see cref="LayoutEntityBase"/> — a balancer, mini-zipper or building. A bare belt
/// (<c>Transport</c>) is NOT a LayoutEntityBase, so belt-to-belt junctions are deliberately left
/// alone; only real, self-supported structures act as anchors, which keeps the change safe (it never
/// marks a genuinely floating belt as supported). Normal span limits still apply between the two
/// connector anchors, so a segment longer than the support distance still needs its own pillar.
///
/// Note: like the rest of the mod, structures that rely on this (a connector-spanning belt with no
/// pillar) become unsupported and may collapse if the mod is removed.
/// </summary>
internal static class ConnectorPillarSupportPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.connectors";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_occupancyField;   // TransportsConstructionHelper.m_occupancyManager

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodBase target = AccessTools.Method(typeof(TransportsConstructionHelper), "FindUnsupportedRegionAround");
        if (target == null)
        {
            Log.Error("Elevation++: TransportsConstructionHelper.FindUnsupportedRegionAround not found; "
                + "connector-supported transport patch skipped.");
            return;
        }

        s_occupancyField = typeof(TransportsConstructionHelper).GetField("m_occupancyManager",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (s_occupancyField == null)
        {
            Log.Error("Elevation++: TransportsConstructionHelper.m_occupancyManager not resolved; "
                + "connector-supported transport patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(ConnectorPillarSupportPatch), nameof(FindUnsupportedRegionPostfix)));
            Log.Info("Elevation++: connector-supported transport patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply connector-supported transport patch: {ex}");
        }
    }

    private static void FindUnsupportedRegionPostfix(object __instance, TransportTrajectory trajectory,
        ref bool positionIsUnsupported, ref int unsupportedStartIndex, ref int unsupportedEndIndex)
    {
        try
        {
            if (!positionIsUnsupported)
            {
                return;
            }
            var support = trajectory.TilesSupportInfo;
            if (support.Length == 0 || !(s_occupancyField.GetValue(__instance) is TerrainOccupancyManager occupancy))
            {
                return;
            }

            // An unsupported region that runs to an endpoint is bounded there by a connector if one is
            // attached — treat that endpoint like a support (move the boundary one tile inward).
            if (unsupportedStartIndex <= 0
                && connectsToSupportedConnector(occupancy, support[0].Position, trajectory.StartDirection))
            {
                unsupportedStartIndex = 1;
            }
            if (unsupportedEndIndex >= support.Length - 1
                && connectsToSupportedConnector(occupancy, support[support.Length - 1].Position, trajectory.EndDirection))
            {
                unsupportedEndIndex = support.Length - 2;
            }

            // Both ends covered by connectors (or a 1-tile segment between them): nothing is unsupported.
            if (unsupportedStartIndex > unsupportedEndIndex)
            {
                positionIsUnsupported = false;
            }
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Elevation++: connector-support postfix failed (logged once): {ex}");
            }
        }
    }

    /// <summary>
    /// True when the tile a trajectory endpoint points into is occupied by a constructed
    /// <see cref="LayoutEntityBase"/> (balancer / mini-zipper / building) — a self-supported anchor.
    /// </summary>
    private static bool connectsToSupportedConnector(TerrainOccupancyManager occupancy, Tile3i endpoint,
        RelTile3i outwardDirection)
    {
        Tile3i connectorTile = endpoint + outwardDirection;
        return occupancy.TryGetAnyEntityAt(connectorTile, out LayoutEntityBase connector)
            && connector.IsConstructed;
    }
}
