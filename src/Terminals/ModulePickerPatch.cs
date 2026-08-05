using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;

namespace ShippingPP.Terminals;

/// <summary>
/// Lets the vanilla cargo module window's product dropdown list ALL products of the module's
/// type when the module is attached to a local terminal (vanilla only lists world-minable
/// products unless a contract is assigned — local terminals never have contracts).
///
/// The dropdown's source is a compiler-generated closure method on
/// <c>CargoDepotModuleInspector</c> (internal class), so the patch resolves it defensively at
/// runtime: the display class is found by looking for a parameterless closure method returning
/// <c>IEnumerable&lt;ProductProto&gt;</c>, and the inspected module is reached through the
/// closure's captured inspector. If any of that fails (e.g. after a game update), the patch is
/// skipped and the dropdown just keeps its vanilla content.
/// </summary>
internal static class ModulePickerPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.modulepicker";

    /// <summary>Set at proto-registration time; source of the full product list.</summary>
    internal static ProtosDb ProtosDb;

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_closureThisField;
    private static PropertyInfo s_inspectorEntityProperty;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        Type inspectorType = AccessTools.TypeByName(
            "Mafi.Unity.Ui.Inspectors.CargoDepotModuleInspector");
        if (inspectorType == null)
        {
            Log.Info("Shipping++: CargoDepotModuleInspector not found (headless run?); "
                + "module product picker patch skipped.");
            return;
        }

        MethodInfo target = null;
        foreach (Type nested in inspectorType.GetNestedTypes(
            BindingFlags.NonPublic | BindingFlags.Public))
        {
            foreach (MethodInfo method in nested.GetMethods(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetParameters().Length == 0
                    && typeof(IEnumerable<ProductProto>).IsAssignableFrom(method.ReturnType))
                {
                    target = method;
                    s_closureThisField = nested.GetField("<>4__this",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    break;
                }
            }
            if (target != null)
            {
                break;
            }
        }
        for (Type t = inspectorType; t != null && s_inspectorEntityProperty == null; t = t.BaseType)
        {
            s_inspectorEntityProperty = t.GetProperty("Entity",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        }
        if (target == null || s_closureThisField == null || s_inspectorEntityProperty == null)
        {
            Log.Warning("Shipping++: module picker closure not resolved; terminal modules keep "
                + "the vanilla (world-minable) product list.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ModulePickerPatch),
                nameof(ProductSourcePostfix)));
            Log.Info("Shipping++: module product picker patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply module picker patch: {ex}");
        }
    }

    private static void ProductSourcePostfix(object __instance,
        ref IEnumerable<ProductProto> __result)
    {
        try
        {
            object inspector = s_closureThisField.GetValue(__instance);
            if (inspector == null || ProtosDb == null)
            {
                return;
            }
            if (!(s_inspectorEntityProperty.GetValue(inspector) is CargoDepotModule module)
                || !(module.Depot.ValueOrNull is LocalTerminal))
            {
                return;
            }
            var products = new Lyst<ProductProto>();
            foreach (ProductProto product in ProtosDb.All<ProductProto>())
            {
                if (product.Type.Matches(module.Prototype.ProductType) && product.IsStorable)
                {
                    products.Add(product);
                }
            }
            __result = products;
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Shipping++: module picker postfix failed (logged once): {ex}");
            }
        }
    }
}
