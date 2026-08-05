using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Products;

namespace ShippingPP.Terminals;

/// <summary>
/// Two prefixes on <see cref="CargoDepotModule"/>, both gated to modules attached to a
/// <see cref="LocalTerminal"/> (vanilla depots untouched):
///
///  - <c>IsForImport()</c> is contract-driven and always true without a contract; for terminal
///    modules it now returns the mod's per-module offer/request flag. This drives the truck
///    logistics registration (import modules offer loads, export modules accept deliveries) and
///    the Import/Export labels in vanilla UI.
///  - <c>IsProductSupported()</c> restricts contract-less modules to world-minable products; for
///    terminal modules any product of the module's type (unit/loose/fluid) is allowed.
/// </summary>
internal static class ModuleDirectionPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.moduledirection";

    private static bool s_applied;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodInfo isForImport = AccessTools.Method(typeof(CargoDepotModule), "IsForImport");
        MethodInfo isProductSupported = AccessTools.Method(typeof(CargoDepotModule),
            "IsProductSupported");
        if (isForImport == null || isProductSupported == null)
        {
            Log.Error("Shipping++: CargoDepotModule.IsForImport/IsProductSupported not "
                + "resolved; terminal module direction control disabled.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(isForImport, prefix: new HarmonyMethod(typeof(ModuleDirectionPatch),
                nameof(IsForImportPrefix)));
            harmony.Patch(isProductSupported, prefix: new HarmonyMethod(
                typeof(ModuleDirectionPatch), nameof(IsProductSupportedPrefix)));
            Log.Info("Shipping++: module direction patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply module direction patch: {ex}");
        }
    }

    private static bool IsForImportPrefix(CargoDepotModule __instance, ref bool __result)
    {
        if (!(__instance.Depot.ValueOrNull is LocalTerminal))
        {
            return true;
        }
        ShippingManager manager = ShippingManager.Current;
        __result = manager == null || !manager.IsExportModule(__instance);
        return false;
    }

    private static bool IsProductSupportedPrefix(CargoDepotModule __instance,
        ProductProto product, ref bool __result)
    {
        if (!(__instance.Depot.ValueOrNull is LocalTerminal))
        {
            return true;
        }
        __result = product.Type.Matches(__instance.Prototype.ProductType);
        return false;
    }
}
