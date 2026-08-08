using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.World;

namespace ShippingPP.Ships;

/// <summary>
/// Local ships never pick up cargo out on the world map, so
/// <c>WorldMapCargoManager.GetAvailableWorldCargo</c> reports nothing for them. This is what
/// keeps the vanilla "Available to pick up" panel out of the ship window for local ships —
/// the panel observes exactly this query and shows itself whenever the list is non-empty, so
/// hiding the panel directly would be undone by its own observer.
/// </summary>
internal static class WorldCargoPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.worldcargo";

    private static bool s_applied;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodInfo target = AccessTools.Method(typeof(WorldMapCargoManager),
            "GetAvailableWorldCargo");
        if (target == null)
        {
            Log.Error("Shipping++: WorldMapCargoManager.GetAvailableWorldCargo not resolved; "
                + "the ship window shows world cargo for local ships.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(WorldCargoPatch),
                nameof(GetAvailableWorldCargoPostfix)));
            Log.Info("Shipping++: world cargo panel patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply world cargo panel patch: {ex}");
        }
    }

    private static void GetAvailableWorldCargoPostfix(CargoShipV2 ship,
        Lyst<WorldMapCargoManager.WorldCargoData> result)
    {
        if (ShippingManager.IsLocalShip(ship))
        {
            result.Clear();
        }
    }
}
