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
using UnityEngine.UIElements;
using Column = Mafi.Unity.UiToolkit.Library.Column;
using Label = Mafi.Unity.UiToolkit.Library.Label;

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

        // The vanilla fuel panel's footer ("N / per a single journey | Round trip duration")
        // only ever shows real values for world-trade ships — local ships are charged per leg
        // by the mod, so for them the strip is a permanent "0 / ?". It lives in compiler-
        // generated locals of the inspector constructor, so it is located in the built visual
        // tree instead (by its own labels, built from the same Tr strings, so the lookup is
        // locale-proof) and hidden whenever a local ship is shown.
        VisualElement fuelFooter = null;
        try
        {
            fuelFooter = findFuelJourneyFooter(panel.RootElement);
            if (fuelFooter == null)
            {
                Log.Warning("Shipping++: fuel journey footer not found in the ship window; "
                    + "it stays visible for local ships.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Shipping++: fuel journey footer lookup failed: {ex.Message}");
        }

        inspector.Observe(() => inspector.Entity != null
                && ShippingManager.IsLocalShip(inspector.Entity))
            .Do(delegate(bool isLocal)
            {
                panel.Visible(isLocal);
                if (fuelFooter != null)
                {
                    fuelFooter.style.display = isLocal ? DisplayStyle.None : DisplayStyle.Flex;
                }
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

    /// <summary>
    /// The visual element of the fuel panel's per-journey footer. Found from any component of
    /// the same inspector: climb to the tree root, find the label built as
    /// <c>$"/ {Tr.FuelPerJourneySuffix}"</c>, ascend to the deepest ancestor that also holds
    /// the trip-duration label (the footer's body row), then keep ascending while the parent
    /// adds no text of its own — that stops exactly at the footer root (its only sibling
    /// content is the bolts decoration), before the panel body that also holds the fuel bar.
    /// </summary>
    private static VisualElement findFuelJourneyFooter(VisualElement anchor)
    {
        VisualElement root = anchor;
        while (root.parent != null)
        {
            root = root.parent;
        }
        string perJourney = $"/ {Tr.FuelPerJourneySuffix}";
        string tripDuration = $"{Tr.CargoShip_TripDuration}: ";
        VisualElement label = null;
        foreach (TextElement text in root.Query<TextElement>().Build())
        {
            if (text.text == perJourney)
            {
                label = text;
                break;
            }
        }
        if (label == null)
        {
            return null;
        }
        VisualElement node = label;
        while (node != null && !subtreeHasText(node, tripDuration))
        {
            node = node.parent;
        }
        while (node != null && node.parent != null
            && countTexts(node.parent) == countTexts(node))
        {
            node = node.parent;
        }
        return node;
    }

    private static bool subtreeHasText(VisualElement node, string value)
    {
        foreach (TextElement text in node.Query<TextElement>().Build())
        {
            if (text.text == value)
            {
                return true;
            }
        }
        return false;
    }

    private static int countTexts(VisualElement node)
    {
        int count = 0;
        foreach (TextElement _ in node.Query<TextElement>().Build())
        {
            count++;
        }
        return count;
    }

    /// <summary>Signature of the sale outcome, so the observer only fires when it changes.
    /// </summary>
    private static string describeSale(CargoShipV2 ship)
    {
        return (ShippingManager.Current?.IsShipForSale(ship) ?? false ? "sold|" : "")
            + ShippingManager.GetShipRefund(ship);
    }

    /// <summary>What selling this ship returns. The materials go to the shipyard like any other
    /// overflow, so there is nothing to lose and no terminal to name.</summary>
    private static LocStrFormatted sellSummary(CargoShipV2 ship)
    {
        AssetValue refund = ShippingManager.GetShipRefund(ship);
        return refund.IsEmpty
            ? Txt.Ship_SellNoRefund
            : Txt.SellRefund(describeProducts(refund));
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
