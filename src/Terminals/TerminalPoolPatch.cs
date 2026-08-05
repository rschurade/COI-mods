using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;

namespace ShippingPP.Terminals;

/// <summary>
/// Detaches local terminals from the vanilla cargo-depot simulation and ship pool.
///
/// A prefix on <c>CargoDepot</c>'s explicit <c>IEntityWithSimUpdate.SimUpdate()</c> replaces the
/// whole vanilla update with <see cref="LocalTerminalSim"/> for <see cref="LocalTerminal"/>
/// entities: no ship is pulled from the limited shipwreck pool, no "depot has no ship"
/// notification fires for intentionally ship-less terminals, and the cargo exchange serves
/// whichever local ship is physically docked (vanilla only ever serves the depot's own ship).
/// Vanilla depots are untouched.
///
/// A second prefix keeps destroyed terminal-built ships out of the vanilla pool accounting
/// (they were never part of it — without this, scrapping one would log an accounting error and
/// inject a free world-trade ship into the pool).
/// </summary>
internal static class TerminalPoolPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.terminalpool";

    private static bool s_applied;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodInfo simUpdate = AccessTools.Method(typeof(CargoDepot),
            "Mafi.Core.Entities.IEntityWithSimUpdate.SimUpdate");
        if (simUpdate == null)
        {
            // Fallback: the explicit implementation is the only method named *.SimUpdate.
            foreach (MethodInfo method in typeof(CargoDepot).GetMethods(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name.EndsWith("SimUpdate", StringComparison.Ordinal)
                    && method.GetParameters().Length == 0)
                {
                    simUpdate = method;
                    break;
                }
            }
        }
        MethodInfo releaseShip = AccessTools.Method(typeof(CargoDepotManager),
            nameof(CargoDepotManager.ReleaseShipFromDepot));
        if (simUpdate == null || releaseShip == null)
        {
            Log.Error("Shipping++: CargoDepot.SimUpdate/ReleaseShipFromDepot not resolved; "
                + "terminal simulation patch skipped — terminals would behave like vanilla "
                + "depots.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(simUpdate,
                prefix: new HarmonyMethod(typeof(TerminalPoolPatch), nameof(SimUpdatePrefix)));
            harmony.Patch(releaseShip,
                prefix: new HarmonyMethod(typeof(TerminalPoolPatch), nameof(ReleaseShipPrefix)));
            Log.Info("Shipping++: terminal simulation patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply terminal simulation patch: {ex}");
        }
    }

    private static bool SimUpdatePrefix(CargoDepot __instance)
    {
        if (!(__instance is LocalTerminal terminal))
        {
            return true;
        }
        // False (skip vanilla) once our replacement ran; vanilla fallback on init failure.
        return !LocalTerminalSim.Update(terminal);
    }

    private static bool ReleaseShipPrefix(Mafi.Core.Buildings.Cargo.Ships.CargoShipV2 ship)
    {
        return !ShippingManager.IsLocalShip(ship);
    }
}
