using System;
using HarmonyLib;
using Mafi;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Entities.Static;

namespace ShippingPP.Terminals;

/// <summary>
/// Lets storages accept local-terminal cargo modules as truck-route partners. The vanilla
/// <see cref="Storage.CanBeAssignedWithInput"/>/<c>CanBeAssignedWithOutput</c> whitelist only
/// storages and mine/forestry towers, so a module — even one implementing the route interfaces
/// (<see cref="LocalTerminalModule"/>) — is rejected by the storage side of the mutual-consent
/// check that both the assignment command and the picking tool run. Two postfixes add the module
/// case: the storage agrees whenever the module side agrees (direction + product compatibility,
/// see the module class) and the storage does not already hold that route. Vanilla depots'
/// modules never pass (they are not the subclass), and all other vanilla behavior is untouched.
/// </summary>
internal static class StorageRoutePatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.storageroutes";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        var canBeAssignedWithInput = AccessTools.Method(typeof(Storage), "CanBeAssignedWithInput");
        var canBeAssignedWithOutput = AccessTools.Method(typeof(Storage), "CanBeAssignedWithOutput");
        if (canBeAssignedWithInput == null || canBeAssignedWithOutput == null)
        {
            Log.Error("Shipping++: Storage.CanBeAssignedWith* not resolved; storages cannot "
                + "route to terminal modules.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(canBeAssignedWithInput, postfix: new HarmonyMethod(
                typeof(StorageRoutePatch), nameof(CanBeAssignedWithInputPostfix)));
            harmony.Patch(canBeAssignedWithOutput, postfix: new HarmonyMethod(
                typeof(StorageRoutePatch), nameof(CanBeAssignedWithOutputPostfix)));
            Log.Info("Shipping++: storage route patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply storage route patch: {ex}");
        }
    }

    // Both postfixes delegate to the module's STATELESS route conditions (direction + product),
    // not its full CanBeAssignedWith* — the assignment command adds the two route sides
    // sequentially, so when the storage side validates, the module side may already hold the
    // partner and a full check would refuse with "already assigned", leaving a one-sided route.
    // Duplicate protection for the storage side is its own AssignedInputs/Outputs guard here.

    /// <summary>Storage as SOURCE, module as RECEIVER (storage → export module).</summary>
    private static void CanBeAssignedWithInputPostfix(Storage __instance,
        IEntityAssignedAsInput entity, ref bool __result)
    {
        try
        {
            if (!__result && entity is LocalTerminalModule module
                && !__instance.AssignedInputs.Contains(module))
            {
                __result = module.AcceptsSupplierRoute(__instance, out _);
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    /// <summary>Storage as RECEIVER, module as SOURCE (import module → storage).</summary>
    private static void CanBeAssignedWithOutputPostfix(Storage __instance,
        IEntityAssignedAsOutput entity, ref bool __result)
    {
        try
        {
            if (!__result && entity is LocalTerminalModule module
                && !__instance.AssignedOutputs.Contains(module))
            {
                __result = module.AcceptsReceiverRoute(__instance, out _);
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
            Log.Error($"Shipping++: storage route postfix failed (logged once): {ex}");
        }
    }
}
