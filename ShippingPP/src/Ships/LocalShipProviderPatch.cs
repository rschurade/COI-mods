using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Buildings.Cargo.Ships;

namespace ShippingPP.Ships;

/// <summary>
/// Routes locally-built ships to the mod's <see cref="LocalShipJobProvider"/>.
///
/// <c>CargoShipV2.UpdateJobProviderIfNeeded()</c> hardcodes the choice between the three vanilla
/// providers (contract / world-cargo / idle), all of which send the ship off-map. This prefix
/// takes over that choice for ships tracked by the <see cref="ShippingManager"/> and leaves every
/// other ship to the vanilla logic. The provider instance is stored in the ship's own private
/// provider field, so it is saved and restored with the ship like the vanilla providers.
/// </summary>
internal static class LocalShipProviderPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.shipprovider";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_jobProviderField;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodInfo target = AccessTools.Method(typeof(CargoShipV2),
            nameof(CargoShipV2.UpdateJobProviderIfNeeded));
        s_jobProviderField = AccessTools.Field(typeof(CargoShipV2), "m_jobProvider");
        if (target == null || s_jobProviderField == null)
        {
            Log.Error("Shipping++: CargoShipV2.UpdateJobProviderIfNeeded/m_jobProvider not "
                + "resolved; local ships would behave like world-trade ships.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(LocalShipProviderPatch),
                nameof(UpdateJobProviderPrefix)));
            Log.Info("Shipping++: local ship provider patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply local ship provider patch: {ex}");
        }
    }

    /// <summary>The ship's local job provider, or null. Diagnostics only — see
    /// <see cref="Diag"/>.</summary>
    internal static LocalShipJobProvider TryGetProviderOf(CargoShipV2 ship)
    {
        if (ship == null || s_jobProviderField == null)
        {
            return null;
        }
        try
        {
            return s_jobProviderField.GetValue(ship) as LocalShipJobProvider;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool UpdateJobProviderPrefix(CargoShipV2 __instance)
    {
        try
        {
            if (!ShippingManager.IsLocalShip(__instance))
            {
                return true;
            }
            var current = s_jobProviderField.GetValue(__instance) as ICargoShipJobProvider;
            if (current is LocalShipJobProvider && current.IsValid())
            {
                return false;
            }
            current?.Destroy();
            s_jobProviderField.SetValue(__instance, new LocalShipJobProvider(__instance));
            return false;
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Shipping++: local ship provider prefix failed: {ex}");
            }
            return true;
        }
    }
}
