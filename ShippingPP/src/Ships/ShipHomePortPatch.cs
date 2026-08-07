using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Economy;
using Mafi.Core.Entities;
using Mafi.Core.Products;
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
        var selectBtn = new ButtonText(Txt.Ship_SelectHomePort, delegate
        {
            CargoShipV2 ship = inspector.Entity;
            if (ship != null && ShippingManager.IsLocalShip(ship))
            {
                Lines.ShippingLinesManagerWindow.Controller.Current?.StartHomeSelection(ship);
            }
        }).NoShrink();
        // Selling is destructive and irreversible, so the button carries the exact refund (and
        // what will be lost) in its tooltip rather than only in the panel header.
        var sellLabel = new Label().Color(Theme.InactiveColor);
        var sellBtn = new ButtonText(Txt.Ship_Sell, delegate
        {
            CargoShipV2 ship = inspector.Entity;
            if (ship != null && ShippingManager.IsLocalShip(ship))
            {
                Lines.ShippingLinesManagerWindow.Controller.Current?.SellShip(ship);
            }
        }).NoShrink();
        PanelWithHeader panel = inspector.AddPanelWithHeader(new Column(2.pt())
        {
            new Row(4.pt())
            {
                (Action<Row>)delegate(Row r)
                {
                    r.AlignItemsCenter().JustifyItemsSpaceBetween().AlignSelfStretch();
                },
                homeLabel,
                selectBtn
            },
            new Row(4.pt())
            {
                (Action<Row>)delegate(Row r)
                {
                    r.AlignItemsCenter().JustifyItemsSpaceBetween().AlignSelfStretch();
                },
                sellLabel,
                sellBtn
            }
        });
        panel.Title(Txt.Ship_HomePort_Title, Txt.Ship_HomePort_Tooltip);

        inspector.Observe(delegate
        {
            CargoShipV2 ship = inspector.Entity;
            if (ship == null || !ShippingManager.IsLocalShip(ship))
            {
                return "";
            }
            // Recomputed from live module fill, so the shown loss tracks the terminal's state.
            return describeSale(ship);
        }).Do(delegate(string _)
        {
            CargoShipV2 ship = inspector.Entity;
            if (ship == null || !ShippingManager.IsLocalShip(ship))
            {
                return;
            }
            bool selling = ShippingManager.Current?.IsShipForSale(ship) ?? false;
            sellBtn.Visible(!selling);
            ((IComponentWithText)sellLabel).SetValue(
                selling ? Txt.Ship_Selling : sellSummary(ship));
            sellBtn.Tooltip(Txt.Ship_Sell_Tooltip);
        });

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
                orphaned ? Txt.Ship_NoHomePort : title.AsLoc());
            homeLabel.Color(orphaned ? ColorRgba.Red : (ColorRgba?)null);
        });
    }

    /// <summary>Signature of the sale outcome, so the observer only fires when it changes.
    /// </summary>
    private static string describeSale(CargoShipV2 ship)
    {
        return (ShippingManager.Current?.IsShipForSale(ship) ?? false ? "sold|" : "")
            + ShippingManager.GetShipRefund(ship) + "|" + ShippingManager.GetShipRefundLoss(ship);
    }

    /// <summary>One line telling the player exactly what selling this ship returns, and what it
    /// destroys — a terminal whose modules carry none of the build materials absorbs nothing, so
    /// silence here would read as a full refund.</summary>
    private static LocStrFormatted sellSummary(CargoShipV2 ship)
    {
        AssetValue refund = ShippingManager.GetShipRefund(ship);
        if (refund.IsEmpty)
        {
            return Txt.Ship_SellNoRefund;
        }
        // The refund is worth the same wherever the ship is homed; the home terminal is only
        // where it gets stored, so a missing one loses the lot rather than reducing it.
        CargoDepot home = ship.AssignedDepot.ValueOrNull;
        if (home == null || home.IsDestroyed)
        {
            return Txt.SellLoss(describeProducts(refund));
        }
        AssetValue lost = ShippingManager.GetShipRefundLoss(ship);
        LocStrFormatted summary = Txt.SellRefund(home.GetTitle(), describeProducts(refund));
        return lost.IsEmpty
            ? summary
            : summary + " ".AsLoc() + Txt.SellLoss(describeProducts(lost));
    }

    private static string describeProducts(AssetValue value)
    {
        string text = "";
        foreach (ProductQuantity pq in value.Products)
        {
            text += (text.Length == 0 ? "" : ", ")
                + pq.Quantity.Value + " " + pq.Product.Strings.Name.TranslatedString;
        }
        return text.Length == 0 ? "-" : text;
    }
}
