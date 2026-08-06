using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Entities;
using Mafi.Core.Syncers;
using Mafi.Localization;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Inspectors;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;

namespace ShippingPP.Ships;

/// <summary>
/// Adds a "Home port" panel to the vanilla cargo ship window for local ships: shows the ship's
/// home terminal (or a red warning when the home was destroyed) and a button that starts the
/// map home-port picker (see <see cref="Lines.ShippingLinesManagerWindow.Controller"/>).
///
/// The vanilla <see cref="CargoShipInspector"/> serves our ships too (they are plain
/// <see cref="CargoShipV2"/> entities), so the panel is appended by a Harmony postfix on the
/// inspector's constructor and kept hidden for vanilla world-trade ships.
/// </summary>
internal static class ShipHomePortPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.shiphomeport";

    private static bool s_applied;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;
        try
        {
            ConstructorInfo ctor = typeof(CargoShipInspector).GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)[0];
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(ShipHomePortPatch),
                nameof(InspectorPostfix)));
            Log.Info("Shipping++: ship home-port panel patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply ship home-port panel patch: {ex}");
        }
    }

    private static void InspectorPostfix(CargoShipInspector __instance)
    {
        CargoShipInspector inspector = __instance;
        var homeLabel = new Label();
        var selectBtn = new ButtonText("Select home port".AsLoc(), delegate
        {
            CargoShipV2 ship = inspector.Entity;
            if (ship != null && ShippingManager.IsLocalShip(ship))
            {
                Lines.ShippingLinesManagerWindow.Controller.Current?.StartHomeSelection(ship);
            }
        }).NoShrink();
        PanelWithHeader panel = inspector.AddPanelWithHeader(new Row(4.pt())
        {
            (Action<Row>)delegate(Row r)
            {
                r.AlignItemsCenter().JustifyItemsSpaceBetween();
            },
            homeLabel,
            selectBtn
        });
        panel.Title("Home port".AsLoc(), ("The terminal this ship belongs to: the ship's cargo "
            + "modules mirror the home terminal's modules, network trips deliver to and fetch "
            + "for the home terminal, and the ship refuels there. Use the button to pick a new "
            + "home terminal on the map. Note that cargo in ship modules that do not match the "
            + "new home's module layout is lost when re-homing.").AsLoc());

        inspector.Observe(() => inspector.Entity != null
                && ShippingManager.IsLocalShip(inspector.Entity))
            .Do(delegate(bool isLocal)
            {
                panel.Visible(isLocal);
            });
        inspector.Observe(delegate
        {
            CargoShipV2 ship = inspector.Entity;
            if (ship == null || !ShippingManager.IsLocalShip(ship))
            {
                return "";
            }
            CargoDepot home = ship.AssignedDepot.ValueOrNull;
            return home == null || home.IsDestroyed ? "!" : home.GetTitle();
        }).Do(delegate(string title)
        {
            bool orphaned = title == "!";
            ((IComponentWithText)homeLabel).SetValue(
                (orphaned ? "None — the home terminal was destroyed!" : title).AsLoc());
            homeLabel.Color(orphaned ? ColorRgba.Red : (ColorRgba?)null);
        });
    }
}
