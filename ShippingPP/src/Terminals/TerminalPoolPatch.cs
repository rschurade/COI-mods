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
            // First priority: a bool prefix is skipped when an earlier prefix already returned
            // false, so another mod's skip-original prefix on SimUpdate could otherwise bypass
            // the terminal simulation entirely. Running first also keeps other mods' depot
            // prefixes out of OUR terminals (the false return below short-circuits them).
            harmony.Patch(simUpdate,
                prefix: new HarmonyMethod(typeof(TerminalPoolPatch), nameof(SimUpdatePrefix))
                    { priority = Priority.First });
            harmony.Patch(releaseShip,
                prefix: new HarmonyMethod(typeof(TerminalPoolPatch), nameof(ReleaseShipPrefix)));
            Log.Info("Shipping++: terminal simulation patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply terminal simulation patch: {ex}");
        }
    }

    /// <summary>Set when the sim replacement ran for a real terminal at least once; the
    /// manager's scan tick uses it to detect a foreign mod starving the prefix.</summary>
    internal static bool SimHasRun;

    private static bool SimUpdatePrefix(CargoDepot __instance)
    {
        if (!(__instance is LocalTerminal terminal))
        {
            return true;
        }
        if (!SimHasRun)
        {
            SimHasRun = true;
            Log.Info($"Shipping++: local terminal simulation active (terminal {terminal.Id}).");
        }
        // False (skip vanilla) once our replacement ran; vanilla fallback on init failure.
        return !LocalTerminalSim.Update(terminal);
    }

    /// <summary>
    /// Support diagnostic, called from the manager's scan tick while local terminals exist but
    /// <see cref="SimHasRun"/> is still false — a state that means some other mod's Harmony
    /// prefix on <c>CargoDepot.SimUpdate</c> returns false before ours runs. Logs every patch
    /// owner on the method once, so a user's log names the conflicting mod.
    /// </summary>
    internal static void LogSimUpdatePatchOwners()
    {
        try
        {
            MethodInfo simUpdate = AccessTools.Method(typeof(CargoDepot),
                "Mafi.Core.Entities.IEntityWithSimUpdate.SimUpdate");
            Patches patches = simUpdate != null ? Harmony.GetPatchInfo(simUpdate) : null;
            if (patches == null)
            {
                Log.Error("Shipping++: terminals exist but the terminal simulation never ran "
                    + "and no patch info is available.");
                return;
            }
            string owners = "";
            foreach (Patch patch in patches.Prefixes)
            {
                owners += (owners.Length == 0 ? "" : ", ")
                    + $"{patch.owner} (prio {patch.priority})";
            }
            Log.Error("Shipping++: terminals exist but the terminal simulation never ran — "
                + "another mod's prefix on CargoDepot.SimUpdate is likely skipping it. "
                + $"Prefix owners: {owners}");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to inspect SimUpdate patches: {ex}");
        }
    }

    private static bool ReleaseShipPrefix(Mafi.Core.Buildings.Cargo.Ships.CargoShipV2 ship)
    {
        return !ShippingManager.IsLocalShip(ship);
    }
}
