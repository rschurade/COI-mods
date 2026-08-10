using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;

namespace ShippingPP.Terminals;

/// <summary>
/// Makes cargo modules placed on a LOCAL terminal instantiate as
/// <see cref="LocalTerminalModule"/> (the route-capable subclass) while modules on vanilla
/// depots keep the vanilla class — same protos, same toolbar buttons, no clones.
///
/// A prefix on <c>DefaultStaticEntityFactory.Create</c> re-does exactly what the vanilla method
/// does (resolver-instantiate with a fresh entity id) but with the subclass type, whenever the
/// module proto's owner at the placement transform — resolved through the same
/// <see cref="CargoDepotManager.FindOwnerForModule"/> the module itself uses to bind its depot —
/// is a <see cref="LocalTerminal"/>. Only new placements go through the factory, so modules
/// existing in older saves stay vanilla (and simply cannot join truck routes) until rebuilt.
/// </summary>
internal static class ModuleFactoryPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.modulefactory";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_idFactoryField;
    private static FieldInfo s_resolverField;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodInfo create = AccessTools.Method(typeof(DefaultStaticEntityFactory), "Create");
        s_idFactoryField = AccessTools.Field(typeof(DefaultStaticEntityFactory), "m_idFactory");
        s_resolverField = AccessTools.Field(typeof(DefaultStaticEntityFactory), "m_resolver");
        if (create == null || s_idFactoryField == null || s_resolverField == null)
        {
            Log.Error("Shipping++: DefaultStaticEntityFactory internals not resolved; terminal "
                + "modules will be vanilla (no truck-route support).");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(create, prefix: new HarmonyMethod(typeof(ModuleFactoryPatch),
                nameof(CreatePrefix)));
            Log.Info("Shipping++: module factory patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply module factory patch: {ex}");
        }
    }

    private static bool CreatePrefix(DefaultStaticEntityFactory __instance,
        StaticEntityProto proto, TileTransform transform, ref StaticEntity __result)
    {
        try
        {
            if (!(proto is CargoDepotModuleProto moduleProto))
            {
                return true;
            }
            if (!(s_resolverField.GetValue(__instance) is DependencyResolver resolver))
            {
                return true;
            }
            CargoDepotManager depots = resolver.Resolve<CargoDepotManager>();
            KeyValuePair<CargoDepot, int>? owner = depots.FindOwnerForModule(moduleProto, transform);
            if (!(owner?.Key is LocalTerminal))
            {
                return true;
            }
            var idFactory = (EntityId.Factory)s_idFactoryField.GetValue(__instance);
            __result = resolver.InstantiateAs<StaticEntity>(typeof(LocalTerminalModule),
                idFactory.GetNextId(), proto, transform);
            return false;
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Shipping++: module factory prefix failed (logged once): {ex}");
            }
            return true;
        }
    }
}
