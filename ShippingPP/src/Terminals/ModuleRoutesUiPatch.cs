using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Commands;
using Mafi.Core.Prototypes;
using Mafi.Core.Syncers;
using Mafi.Localization;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui.Library.Inspectors;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;

namespace ShippingPP.Terminals;

/// <summary>
/// Adds the vanilla "Import routes"/"Export routes" panels to the cargo module window for
/// route-capable local-terminal modules (<see cref="LocalTerminalModule"/>), so routes can be
/// seen, created and removed from the module side too — not only from the storage window.
///
/// Which panel shows follows the module's shipping direction (mirroring buildings like the ore
/// sorting plant that only offer the direction that makes sense): an EXPORT module shows Import
/// routes (storages that deliver to it by truck), an IMPORT module shows Export routes
/// (storages its cargo is trucked to). The panels are built from the same public building
/// blocks the vanilla inspectors use — <see cref="BuildingsAssignerUiHeader"/>,
/// <see cref="AssignedBuildingIcon"/> (click = pan to, right-click = unassign) and
/// <see cref="BuildingsAssigner"/> for the +/- map picking — and are gated on the same custom
/// routes research. Vanilla modules (and pre-rebuild terminal modules) never show them.
///
/// The vanilla <c>CargoDepotModuleInspector</c> is internal and its ctor builds the whole
/// window, so the panels are appended by a Harmony postfix on that ctor; a custom body is used
/// instead of the vanilla <c>BuildingsAssignerUiBody</c> because the latter's observers assume
/// the inspected entity always implements the route interfaces (true for storages, not for
/// vanilla modules shown by the same inspector).
/// </summary>
internal static class ModuleRoutesUiPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.moduleroutes";

    private static bool s_patched;
    private static bool s_runtimeErrorLogged;
    private static DependencyResolver s_resolver;

    public static void TryInitialize(DependencyResolver resolver)
    {
        // The resolver is per game session; the Harmony patch is applied once.
        s_resolver = resolver;
        if (s_patched)
        {
            return;
        }

        Type inspectorType = AccessTools.TypeByName(
            "Mafi.Unity.Ui.Inspectors.CargoDepotModuleInspector");
        if (inspectorType == null)
        {
            Log.Info("Shipping++: CargoDepotModuleInspector not found (headless run?); "
                + "module routes UI skipped.");
            return;
        }
        s_patched = true;

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            foreach (ConstructorInfo ctor in AccessTools.GetDeclaredConstructors(inspectorType))
            {
                harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(ModuleRoutesUiPatch),
                    nameof(CtorPostfix)));
            }
            Log.Info("Shipping++: module routes UI patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply module routes UI patch: {ex}");
        }
    }

    private static void CtorPostfix(object __instance)
    {
        try
        {
            var inspector = (BaseInspector<CargoDepotModule>)__instance;
            addRoutesPanel(inspector, importRoutes: true);
            addRoutesPanel(inspector, importRoutes: false);
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void addRoutesPanel(BaseInspector<CargoDepotModule> inspector, bool importRoutes)
    {
        var items = new Row().Fill().Wrap();
        var noItems = new Label($"({Tr.AssignedForLogistics__Empty})".AsLoc()).MarginTop(3.pt());
        var body = new Row();
        body.Gap(2.pt()).AlignSelfStretch().AlignItemsStart();
        body.Add(new ButtonIcon("Assets/Unity/UserInterface/General/PlusMinus.svg",
                (Action)(() => editRoutes(inspector, importRoutes))).MarginTopBottom(1.pt()),
            items);

        PanelWithHeader panel = inspector.AddPanelWithHeader(body);
        panel.Header.Add(new BuildingsAssignerUiHeader(importRoutes,
                importRoutes
                    ? Tr.AssignedForLogistics__ImportTooltipGeneral
                    : Tr.AssignedForLogistics__ExportTooltipGeneral)
            .FlexGrow(1f, Percent.Fifty));

        // Visible only for a route-capable module, only for the panel matching its direction,
        // and only once custom routes are researched (same gate as the vanilla panels).
        Proto routesTech = inspector.Context.ProtosDb.GetOrThrow<Proto>(
            IdsCore.Technology.CustomRoutes);
        inspector.Observe(() =>
        {
            LocalTerminalModule module = inspector.Entity as LocalTerminalModule;
            if (module == null
                || !inspector.Context.UnlockedProtosDbForUi.IsUnlocked(routesTech))
            {
                return false;
            }
            bool isExport = ShippingManager.Current != null
                && ShippingManager.Current.IsExportModule(module);
            // Export modules are truck-fed (import routes); import modules truck out.
            return importRoutes == isExport;
        }).Do(delegate(bool visible)
        {
            panel.Visible(visible);
        });

        inspector.ObserveEnumerable(() => routeEntities(inspector, importRoutes))
            .Observe(() => inspector.Entity)
            .DoOnSync(delegate(Lyst<ILayoutEntity> list, CargoDepotModule _)
            {
                items.Clear();
                foreach (ILayoutEntity partner in list)
                {
                    items.Add(new AssignedBuildingIcon(
                        clicked => inspector.Context.CameraController.PanTo(clicked.Position2f),
                        clicked => unassign(inspector, clicked, importRoutes)).Value(partner));
                }
                if (list.IsEmpty)
                {
                    items.Add(noItems);
                }
            });
    }

    /// <summary>The module's partners of the given panel: suppliers (its AssignedOutputs) for
    /// the import panel, receivers (its AssignedInputs) for the export panel.</summary>
    private static IEnumerable<ILayoutEntity> routeEntities(
        BaseInspector<CargoDepotModule> inspector, bool importRoutes)
    {
        if (!(inspector.Entity is LocalTerminalModule module))
        {
            return Enumerable.Empty<ILayoutEntity>();
        }
        return importRoutes
            ? module.AssignedOutputs.Cast<ILayoutEntity>()
            : module.AssignedInputs.Cast<ILayoutEntity>();
    }

    private static void unassign(BaseInspector<CargoDepotModule> inspector, ILayoutEntity partner,
        bool importRoutes)
    {
        if (!(inspector.Entity is LocalTerminalModule module)
            || !(partner is IEntityAssignedAsOutput && partner is IEntityAssignedAsInput))
        {
            return;
        }
        UnassignStaticEntityCmd cmd = importRoutes
            ? new UnassignStaticEntityCmd((IEntityAssignedAsOutput)partner, module)
            : new UnassignStaticEntityCmd(module, (IEntityAssignedAsInput)partner);
        inspector.Context.InputScheduler.ScheduleInputCmd(cmd);
    }

    /// <summary>The +/- button: the vanilla map-picking tool, source side for the export panel
    /// (isForInputs: true — picking "inputs" is vanilla's term for a source picking its
    /// receivers), receiver side for the import panel.</summary>
    private static void editRoutes(BaseInspector<CargoDepotModule> inspector, bool importRoutes)
    {
        if (!(inspector.Entity is LocalTerminalModule module) || s_resolver == null)
        {
            return;
        }
        BuildingsAssigner assigner = s_resolver.Resolve<BuildingsAssigner>();
        assigner.ActivateFor(module,
            e => inspector.Context.InspectorsManager.TryActivateFor(e),
            isForInputs: !importRoutes);
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Shipping++: module routes UI failed (logged once): {ex}");
        }
    }
}
