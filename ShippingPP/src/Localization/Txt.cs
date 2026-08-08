using Mafi.Localization;

namespace ShippingPP;

/// <summary>
/// Every user-facing string of the mod's UI, in one place so it can be translated (see
/// <see cref="ModTranslations"/>). Strings the vanilla game already has an exact equivalent for
/// are NOT repeated here — those call sites use the game's own <c>Tr</c> catalog directly and so
/// come out translated in every language the game ships.
///
/// Ids are permanent: renaming one silently invalidates that entry in every translation file.
/// Ids of strings with placeholders end in <c>_Fmt</c>; their <c>{0}</c>, <c>{1}</c>, ... must
/// survive translation.
///
/// Proto names and descriptions are not here — they are registered with the proto (with ids the
/// game itself derives from the proto id) in the proto's own file, but go through the same
/// translation lookup.
/// </summary>
internal static class Txt
{
    // ---------------------------------------------------------------- Shipping lines manager.
    public static readonly LocStrFormatted LinesManager_Title =
        str("LinesManager_Title", "Shipping lines");

    public static readonly LocStrFormatted LinesManager_DeleteLine_Tooltip =
        str("LinesManager_DeleteLine_Tooltip",
            "Delete the selected line. Its ships become idle until assigned to another line.");

    public static readonly LocStrFormatted LinesManager_NewLine_Tooltip =
        str("LinesManager_NewLine_Tooltip", "Create a new shipping line.");

    public static readonly LocStrFormatted LinesManager_LineColor_Tooltip =
        str("LinesManager_LineColor_Tooltip", "Line color");

    public static readonly LocStrFormatted LinesManager_ApplyColorToShips =
        str("LinesManager_ApplyColorToShips", "Apply line color to ships");

    public static readonly LocStrFormatted LinesManager_ApplyColorToShips_Tooltip =
        str("LinesManager_ApplyColorToShips_Tooltip",
            "When enabled, ships assigned to this line are painted in the line color.");

    public static readonly LocStrFormatted LinesManager_NoStops =
        str("LinesManager_NoStops", "No stops yet — add local terminals and navigation buoys.");

    public static readonly LocStrFormatted LinesManager_AddStop_Tooltip =
        str("LinesManager_AddStop_Tooltip",
            "Pick stops directly on the map: click a local terminal or a navigation buoy to "
            + "append it to the line. Hold shift to add several stops in a row; right-click or "
            + "Escape to finish.");

    public static readonly LocStrFormatted LinesManager_StopsTab =
        str("LinesManager_StopsTab", "Stops");

    public static readonly LocStrFormatted LinesManager_ShipsTab =
        str("LinesManager_ShipsTab", "Ships");

    public static readonly LocStrFormatted LinesManager_NoLineSelected =
        str("LinesManager_NoLineSelected",
            "No line selected. Create a line and add terminal stops.");

    public static readonly LocStrFormatted LinesManager_NoRoute =
        str("LinesManager_NoRoute", "A line needs at least two local terminal stops.");

    public static readonly LocStrFormatted LinesManager_NoRoute_Tooltip =
        str("LinesManager_NoRoute_Tooltip",
            "Ships sail the stops in order and only exchange cargo at local terminals; buoys "
            + "are waypoints. With fewer than two terminals the line's ships have no route to "
            + "sail.");

    public static readonly LocStrFormatted LinesManager_RemoveStop_Tooltip =
        str("LinesManager_RemoveStop_Tooltip", "Remove this stop from the line");

    /// <summary>Second half of <see cref="LinesManager_ShipHome"/> for an unassigned ship.</summary>
    public static readonly LocStrFormatted LinesManager_ShipAutoDispatch =
        str("LinesManager_ShipAutoDispatch", "no line — idle");

    /// <summary>Second half of <see cref="LinesManager_ShipHome"/> for a ship of the shown line.</summary>
    public static readonly LocStrFormatted LinesManager_ShipOnThisLine =
        str("LinesManager_ShipOnThisLine", "on this line");

    public static readonly LocStrFormatted LinesManager_ModuleOffers =
        str("LinesManager_ModuleOffers", "This module offers its product (export).");

    public static readonly LocStrFormatted LinesManager_ModuleRequests =
        str("LinesManager_ModuleRequests", "This module requests its product (import).");

    public static readonly LocStrFormatted LinesManager_PickStopHint =
        str("LinesManager_PickStopHint",
            "Click a local terminal or a navigation buoy to add it as a stop "
            + "(shift: add multiple, right-click: finish)");

    public static readonly LocStrFormatted LinesManager_PickHomeHint =
        str("LinesManager_PickHomeHint",
            "Click a local terminal to make it this ship's new home port (right-click: cancel)");

    private static readonly string s_buoyStop =
        text("LinesManager_BuoyStop_Fmt", "[buoy] {0}");

    private static readonly string s_shipHome =
        text("LinesManager_ShipHome_Fmt", "Home: {0} — {1}");

    private static readonly string s_shipOnLine =
        text("LinesManager_ShipOnLine_Fmt", "on line {0}");

    private static readonly string s_moduleFill =
        text("LinesManager_ModuleFill_Fmt", "{0}: {1} / {2} stored.");

    private static readonly string s_addStopToLine =
        text("LinesManager_AddStopToLine_Fmt", "Add \"{0}\" to the line");

    private static readonly string s_makeHomePort =
        text("LinesManager_MakeHomePort_Fmt", "Make \"{0}\" the ship's home port");

    /// <summary>Title of a navigation buoy stop, marked as a waypoint.</summary>
    public static LocStrFormatted BuoyStop(string title) => ModTranslations.Fmt(s_buoyStop, title);

    /// <summary>A ship row's subtitle: home terminal and what the ship is assigned to.</summary>
    public static LocStrFormatted ShipHome(string home, LocStrFormatted assignment)
        => ModTranslations.Fmt(s_shipHome, home, assignment.Value);

    public static LocStrFormatted ShipOnLine(int lineId)
        => ModTranslations.Fmt(s_shipOnLine, lineId);

    /// <summary>Fill of one terminal module: product, stored amount and its capacity.</summary>
    public static LocStrFormatted ModuleFill(string product, int stored, int capacity)
        => ModTranslations.Fmt(s_moduleFill, product, stored, capacity);

    public static LocStrFormatted AddStopToLine(string title)
        => ModTranslations.Fmt(s_addStopToLine, title);

    public static LocStrFormatted MakeHomePort(string title)
        => ModTranslations.Fmt(s_makeHomePort, title);

    // Stop departure rules (see StopRule).
    public static readonly LocStrFormatted StopRule_LeaveWhenIdle =
        str("StopRule_LeaveWhenIdle", "Leave when done");

    public static readonly LocStrFormatted StopRule_Tooltip = str("StopRule_Tooltip",
        "When a ship may leave this stop. \"Leave when done\" departs as soon as the cranes stop "
        + "— at a full import terminal that means leaving still laden, at an empty export "
        + "terminal it means leaving empty. \"Load to\" and \"Unload to\" hold the ship until "
        + "its cargo reaches the given share of its capacity. Click to cycle.");

    public static readonly LocStrFormatted StopRulePercent_Tooltip =
        str("StopRulePercent_Tooltip",
            "Cargo level the ship waits for, as a share of its total capacity. Click to change.");

    public static readonly LocStrFormatted StopRuleTimeout_Tooltip =
        str("StopRuleTimeout_Tooltip",
            "Leave anyway after this long, so a stop that never reaches the level cannot hold "
            + "the ship — and the berth behind it — forever. Click to change; \"no limit\" waits "
            + "indefinitely.");

    public static readonly LocStrFormatted StopRule_LoadTo = str("StopRule_LoadTo", "Load to");
    public static readonly LocStrFormatted StopRule_UnloadTo =
        str("StopRule_UnloadTo", "Unload to");
    private static readonly string s_stopRulePercent = text("StopRule_Percent_Fmt", "{0}%");
    private static readonly string s_stopRuleTimeout = text("StopRule_Timeout_Fmt", "max {0}s");
    public static readonly LocStrFormatted StopRule_NoTimeout =
        str("StopRule_NoTimeout", "no limit");

    /// <summary>Label of a stop's departure-mode button.</summary>
    public static LocStrFormatted StopRuleMode(Lines.StopWait mode)
    {
        switch (mode)
        {
            case Lines.StopWait.LoadTo:
                return StopRule_LoadTo;
            case Lines.StopWait.UnloadTo:
                return StopRule_UnloadTo;
            default:
                return StopRule_LeaveWhenIdle;
        }
    }

    public static LocStrFormatted StopRulePercent(int percent)
        => ModTranslations.Fmt(s_stopRulePercent, percent);

    public static LocStrFormatted StopRuleTimeout(int seconds)
        => seconds <= 0 ? StopRule_NoTimeout : ModTranslations.Fmt(s_stopRuleTimeout, seconds);

    // --------------------------------------------------------------- Local terminal inspector.
    public static readonly LocStrFormatted Terminal_BuildShip =
        str("Terminal_BuildShip", "Build ship");

    public static readonly LocStrFormatted Terminal_BuildingShip =
        str("Terminal_BuildingShip", "Building ship");

    public static readonly LocStrFormatted Terminal_Ships_Tooltip =
        str("Terminal_Ships_Tooltip",
            "Ships built on site from delivered materials, homed at this terminal. Build as "
            + "many as you like — arrivals queue up and hold at anchor while the dock serves "
            + "one ship at a time. A ship's cargo modules mirror this terminal's modules, so at "
            + "least one module must be built before a ship can be laid down; ships gain the "
            + "remaining modules automatically as more are built on the terminal.");

    public static readonly LocStrFormatted Terminal_ModuleExport =
        str("Terminal_ModuleExport", "Offer (export)");

    public static readonly LocStrFormatted Terminal_ModuleExport_Tooltip =
        str("Terminal_ModuleExport_Tooltip",
            "On: this module OFFERS its product to the shipping network — trucks fill it from "
            + "your factory and docked ships are loaded from it. Off: this module REQUESTS its "
            + "product — docked ships are unloaded into it and trucks distribute the goods to "
            + "your factory.");

    public static readonly LocStrFormatted Terminal_Threshold_Tooltip =
        str("Terminal_Threshold_Tooltip",
            "Network threshold: an import module requests cargo only while filled below this, "
            + "an export module offers only while filled above (100 % minus this). "
            + "100 % = always active.");

    public static readonly LocStrFormatted Terminal_Shipping_Title =
        str("Terminal_Shipping_Title", "Shipping");

    public static readonly LocStrFormatted Terminal_Shipping_Tooltip =
        str("Terminal_Shipping_Tooltip",
            "Direction of each terminal module. Assign a product in the module's own window "
            + "first; any product of the module's type can be shipped.");

    private static readonly string s_shipsCount = text("Terminal_ShipsCount_Fmt", "Ships: {0}");

    private static readonly string s_buildShipTooltip =
        text("Terminal_BuildShip_Tooltip_Fmt",
            "Builds a cargo ship ({0} modules) on site: the construction materials are "
            + "requested from truck logistics, and the ship enters service at this terminal "
            + "once everything is delivered.");

    private static readonly string s_buildShipRequires =
        text("Terminal_BuildShipRequires_Fmt", "Requires: {0}.");

    private static readonly string s_productQuantity = text("ProductQuantity_Fmt", "{0}x {1}");

    public static LocStrFormatted ShipsCount(int count)
        => ModTranslations.Fmt(s_shipsCount, count);

    public static LocStrFormatted BuildShipTooltip(int modules)
        => ModTranslations.Fmt(s_buildShipTooltip, modules);

    /// <summary>Appended to <see cref="BuildShipTooltip"/> when the ship has a build cost.</summary>
    public static LocStrFormatted BuildShipRequires(string products)
        => ModTranslations.Fmt(s_buildShipRequires, products);

    /// <summary>One entry of a build cost list, e.g. "12x Steel".</summary>
    public static LocStrFormatted ProductQuantity(int quantity, string product)
        => ModTranslations.Fmt(s_productQuantity, quantity, product);

    // ---------------------------------------------------------- Home port panel (ship window).
    public static readonly LocStrFormatted Ship_SelectHomePort =
        str("Ship_SelectHomePort", "Select home port");

    public static readonly LocStrFormatted Ship_HomePort_Title =
        str("Ship_HomePort_Title", "Home port");

    public static readonly LocStrFormatted Ship_HomePort_Tooltip =
        str("Ship_HomePort_Tooltip",
            "The terminal this ship belongs to: the ship's cargo modules mirror the home "
            + "terminal's modules, network trips deliver to and fetch for the home terminal, "
            + "and the ship refuels there. Use the button to pick a new home terminal on the "
            + "map. Note that cargo in ship modules that do not match the new home's module "
            + "layout is lost when re-homing.");

    public static readonly LocStrFormatted Ship_NoHomePort =
        str("Ship_NoHomePort", "None — the home terminal was destroyed!");

    public static readonly LocStrFormatted Ship_Sell =
        str("Ship_Sell", "Sell ship");

    public static readonly LocStrFormatted Ship_Selling =
        str("Ship_Selling", "Sold — leaving the map");

    public static readonly LocStrFormatted Ship_Sell_Tooltip = str("Ship_Sell_Tooltip",
        "Sells this ship: it sails off the map and is removed. Its build cost, reduced by the "
        + "game's deconstruction refund setting, and any cargo still aboard go to the shipyard "
        + "like any other overflow material.");

    private static readonly string s_shipSellRefund =
        text("Ship_SellRefund_Fmt", "Refund: {0}");

    public static readonly LocStrFormatted Ship_SellNoRefund =
        str("Ship_SellNoRefund", "No refund available for this ship.");

    /// <summary>What selling a ship returns.</summary>
    public static LocStrFormatted SellRefund(string products)
        => ModTranslations.Fmt(s_shipSellRefund, products);

    // ---------------------------------------------------------------- Ship status (ship window).
    public static readonly LocStrFormatted ShipStatus_Orphaned =
        str("ShipStatus_Orphaned",
            "No home port — the home terminal was destroyed. Select a new home port in this "
            + "window to put the ship back into service.");

    public static readonly LocStrFormatted ShipStatus_LowFuel =
        str("ShipStatus_LowFuel", "Not enough fuel for the next trip");

    private static readonly string s_shipWaitingForFuel = text("ShipStatus_WaitingForFuel_Fmt",
        "Out of fuel — holding the berth until {0} is delivered here");

    /// <summary>Docked and out of fuel: the ship holds the berth until the terminal can fill its
    /// tank, so the status names the fuel the player has to deliver.</summary>
    public static LocStrFormatted ShipStatus_WaitingForFuel(string fuel)
        => ModTranslations.Fmt(s_shipWaitingForFuel, fuel);

    public static readonly LocStrFormatted ShipStatus_TransferringCargo =
        str("ShipStatus_TransferringCargo", "Transferring cargo");

    public static readonly LocStrFormatted ShipStatus_LineUnusable =
        str("ShipStatus_LineUnusable",
            "Assigned line has no usable route — add at least two terminal stops to the line.");

    public static readonly LocStrFormatted ShipStatus_Idle =
        str("ShipStatus_Idle",
            "Not assigned to a line — assign it in the shipping lines window to put it to work");

    private static readonly string s_waitingForBerth =
        text("ShipStatus_WaitingForBerth_Fmt", "Waiting for a free berth at {0}");

    private static readonly string s_onLine = text("ShipStatus_OnLine_Fmt", "On line \"{0}\"");

    public static LocStrFormatted ShipStatus_WaitingForBerth(string terminal)
        => ModTranslations.Fmt(s_waitingForBerth, terminal);

    public static LocStrFormatted ShipStatus_OnLine(string lineName)
        => ModTranslations.Fmt(s_onLine, lineName);

    private static LocStrFormatted str(string id, string enUs)
        => ModTranslations.Str("ShippingPP__" + id, enUs);

    private static string text(string id, string enUs)
        => ModTranslations.Text("ShippingPP__" + id, enUs);
}
