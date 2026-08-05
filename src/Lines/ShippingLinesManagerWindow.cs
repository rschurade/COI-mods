using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Entities;
using Mafi.Core.Input;
using Mafi.Core.Syncers;
using Mafi.Localization;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Hud;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.UiStatic.Toolbar;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using ShippingPP.Terminals;

namespace ShippingPP.Lines;

/// <summary>
/// The shipping lines manager — the mod's counterpart of the vanilla train lines manager window:
/// lines list on the left, the selected line's stop list and ship assignments on the right.
/// Opened from its own toolbar button (registered by the nested <see cref="Controller"/>, which
/// the game's DI scan picks up automatically from the mod assembly, exactly like vanilla window
/// controllers).
/// </summary>
public class ShippingLinesManagerWindow : Window
{
    private readonly ShippingManager m_manager;
    private readonly EntitiesManager m_entitiesManager;
    private readonly IInputScheduler m_inputScheduler;

    private readonly Column m_linesColumn;
    private readonly Column m_stopsColumn;
    private readonly Column m_shipsColumn;
    private readonly TitleWithRename m_detailTitle;
    private readonly Column m_detailsPanel;
    private readonly Label m_noLinesLabel;

    private int m_selectedLineId = -1;
    private int m_pendingAddTerminalId;

    public ShippingLinesManagerWindow(Controller controller, UiContext context,
        ShippingManager manager, EntitiesManager entitiesManager)
        : base("Shipping lines".AsLoc())
    {
        m_manager = manager;
        m_entitiesManager = entitiesManager;
        m_inputScheduler = context.InputScheduler;

        MakeMovable();
        EnablePinning();
        WindowSize(900.px(), 700.px());

        // Left: lines list + new-line button.
        m_linesColumn = new Column(1.pt());
        var newLineBtn = new ButtonIconText(Button.General,
            "Assets/Unity/UserInterface/General/Plus.svg", "New line".AsLoc())
            .OnClick((Action)createNewLine, allowKeyPresses: false);
        var left = new Column(1.pt())
        {
            (Action<Column>)delegate(Column c)
            {
                c.FlexBasis(35.Percent()).AlignItemsStretch().FlexGrow(1f);
            },
            new ScrollColumn
            {
                m_linesColumn.AlignItemsStretch()
            }.Fill(),
            new PanelFooterRow
            {
                newLineBtn
            }
        };

        // Right: selected line details (stops + ships tabs). The title doubles as the rename
        // field (hover shows the rename icon, same as vanilla line names).
        m_detailTitle = new TitleWithRename();
        m_detailTitle.EnableRename(delegate(string newName)
        {
            if (m_selectedLineId >= 0 && !string.IsNullOrWhiteSpace(newName))
            {
                m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                    ModifyLineCmd.ACTION_RENAME, m_selectedLineId, default(EntityId), newName));
            }
        });
        m_stopsColumn = new Column(2.pt());
        m_shipsColumn = new Column(2.pt());

        var addStopDropdown = new Dropdown<int>(
            (int stopEntityId, int idx, bool inDropdown) =>
                new Label(titleOfStop(stopEntityId).AsLoc()));
        addStopDropdown.OnValueChanged(delegate(int terminalId, int _)
        {
            m_pendingAddTerminalId = terminalId;
        });
        var addStopBtn = new ButtonIconText(Button.Primary,
            "Assets/Unity/UserInterface/General/Plus.svg", "Add stop".AsLoc())
            .OnClick((Action)delegate
            {
                if (m_selectedLineId >= 0 && m_pendingAddTerminalId != 0)
                {
                    m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                        ModifyLineCmd.ACTION_ADD_STOP, m_selectedLineId,
                        new EntityId(m_pendingAddTerminalId)));
                }
            }, allowKeyPresses: false);

        var stopsTab = new Column(2.pt())
        {
            m_stopsColumn,
            new Row(4.pt())
            {
                addStopDropdown,
                addStopBtn
            }
        };
        var shipsTab = new Column(2.pt())
        {
            m_shipsColumn
        };
        var tabs = new TabContainer();
        tabs.Add("Stops".AsLoc(), stopsTab);
        tabs.Add("Ships".AsLoc(), shipsTab);

        var deleteLineBtn = new ButtonIconText(Button.Danger,
            "Assets/Unity/UserInterface/General/Trash128.png", "Delete line".AsLoc())
            .OnClick((Action)delegate
            {
                if (m_selectedLineId >= 0)
                {
                    m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                        ModifyLineCmd.ACTION_DELETE, m_selectedLineId, default(EntityId)));
                    m_selectedLineId = -1;
                }
            }, allowKeyPresses: false);

        m_detailsPanel = new Column(2.pt())
        {
            (Action<Column>)delegate(Column c)
            {
                c.AlignItemsStretch();
            },
            new Row(4.pt())
            {
                m_detailTitle,
                deleteLineBtn
            },
            tabs.AlignSelfStretch()
        };
        m_noLinesLabel = new Label(
            "No line selected. Create a line and add terminal stops.".AsLoc());
        var right = new Column(1.pt())
        {
            (Action<Column>)delegate(Column c)
            {
                c.FlexBasis(65.Percent()).AlignItemsStretch().FlexGrow(1f);
            },
            m_noLinesLabel,
            m_detailsPanel
        };

        Body.Add(new Row(1.pt())
        {
            (Action<Row>)delegate(Row c)
            {
                c.Fill().AlignItemsStart();
            },
            left,
            right
        });

        // One composite state hash drives all rebuilds (lines, stops, assignments, selection).
        this.Observe(computeStateHash).Do(delegate(string _)
        {
            rebuildAll(addStopDropdown);
        });
    }

    private void createNewLine()
    {
        LocalTerminal any = firstTerminalOrNull();
        if (any != null)
        {
            m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                ModifyLineCmd.ACTION_CREATE, 0, any.Id));
        }
    }

    private LocalTerminal firstTerminalOrNull()
    {
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            if (!terminal.IsDestroyed && terminal.IsConstructed)
            {
                return terminal;
            }
        }
        return null;
    }

    private string titleOfStop(int entityIdValue)
    {
        if (m_entitiesManager.TryGetEntity(new EntityId(entityIdValue),
            out Mafi.Core.Entities.Static.StaticEntity stop))
        {
            return stop.Prototype is NavBuoyProto
                ? $"[buoy] {stop.GetTitle()}"
                : stop.GetTitle();
        }
        return $"Stop {entityIdValue}";
    }

    private string computeStateHash()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(m_selectedLineId).Append('|');
        foreach (ShippingLine line in m_manager.AllLines)
        {
            sb.Append(line.Id).Append(':').Append(line.Name).Append(':')
                .Append(line.StopCount).Append(';');
            for (int i = 0; i < line.StopCount; i++)
            {
                sb.Append(line.StopAtOrNull(i)?.Id.Value ?? 0).Append(',');
            }
        }
        sb.Append('|');
        foreach (CargoShipV2 ship in m_entitiesManager.GetAllEntitiesOfType<CargoShipV2>())
        {
            if (ShippingManager.IsLocalShip(ship) && !ship.IsDestroyed)
            {
                // Titles included so entity renames refresh the rows.
                sb.Append(ship.Id.Value).Append(':').Append(ship.GetTitle()).Append(':')
                    .Append(m_manager.GetLineIdFor(ship) ?? -1).Append(';');
            }
        }
        sb.Append('|');
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            if (!terminal.IsDestroyed && terminal.IsConstructed)
            {
                sb.Append(terminal.Id.Value).Append(':').Append(terminal.GetTitle()).Append(';');
            }
        }
        sb.Append('|');
        // StaticEntity + proto filter: buoys placed before the NavBuoy entity class existed
        // are plain barrier entities, newer ones are NavBuoy — this covers both.
        foreach (Mafi.Core.Entities.Static.StaticEntity buoy in
            m_entitiesManager.GetAllEntitiesOfType<Mafi.Core.Entities.Static.StaticEntity>())
        {
            if (buoy.Prototype is NavBuoyProto && !buoy.IsDestroyed && buoy.IsConstructed)
            {
                sb.Append(buoy.Id.Value).Append(':').Append(buoy.GetTitle()).Append(';');
            }
        }
        return sb.ToString();
    }

    private void rebuildAll(Dropdown<int> addStopDropdown)
    {
        // Lines list.
        m_linesColumn.Clear();
        foreach (ShippingLine line in m_manager.AllLines)
        {
            ShippingLine captured = line;
            var row = new ButtonRow(Button.Area);
            row.Add(new Label($"{line.Name}  —  {line.StopCount} stops".AsLoc()));
            row.OnClick((Action)delegate
            {
                m_selectedLineId = captured.Id;
            }, allowKeyPresses: false);
            row.Selected(line.Id == m_selectedLineId);
            m_linesColumn.Add(row);
        }

        ShippingLine selected = m_selectedLineId >= 0
            ? m_manager.TryGetLine(m_selectedLineId) : null;
        m_noLinesLabel.Visible(selected == null);
        m_detailsPanel.Visible(selected != null);
        if (selected == null)
        {
            return;
        }
        m_detailTitle.Text(selected.Name.AsLoc());

        // Stops of the selected line.
        m_stopsColumn.Clear();
        for (int i = 0; i < selected.StopCount; i++)
        {
            Mafi.Core.Entities.Static.StaticEntity stop = selected.StopAtOrNull(i);
            if (stop == null)
            {
                continue;
            }
            Mafi.Core.Entities.Static.StaticEntity captured = stop;
            var removeBtn = new ButtonText("remove".AsLoc(), delegate
            {
                m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                    ModifyLineCmd.ACTION_REMOVE_STOP, selected.Id, captured.Id));
            });
            m_stopsColumn.Add(new Row(4.pt())
            {
                new Label($"{i + 1}. {titleOfStop(stop.Id.Value)}".AsLoc()),
                removeBtn
            });
        }

        // Add-stop dropdown options: all constructed terminals and navigation buoys.
        var options = new Lyst<int>();
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            if (!terminal.IsDestroyed && terminal.IsConstructed)
            {
                options.Add(terminal.Id.Value);
            }
        }
        foreach (Mafi.Core.Entities.Static.StaticEntity buoy in
            m_entitiesManager.GetAllEntitiesOfType<Mafi.Core.Entities.Static.StaticEntity>())
        {
            if (buoy.Prototype is NavBuoyProto && !buoy.IsDestroyed && buoy.IsConstructed)
            {
                options.Add(buoy.Id.Value);
            }
        }
        addStopDropdown.SetOptions(options);

        // Ships tab: every local ship with assign/unassign for the selected line.
        m_shipsColumn.Clear();
        foreach (CargoShipV2 ship in m_entitiesManager.GetAllEntitiesOfType<CargoShipV2>())
        {
            if (!ShippingManager.IsLocalShip(ship) || ship.IsDestroyed)
            {
                continue;
            }
            CargoShipV2 capturedShip = ship;
            int? lineId = m_manager.GetLineIdFor(ship);
            bool onThisLine = lineId == selected.Id;
            string home = ship.AssignedDepot.ValueOrNull?.GetTitle() ?? "-";
            string assignment = lineId.HasValue
                ? (onThisLine ? "this line" : $"line {lineId.Value}")
                : "auto dispatch";
            var actionBtn = new ButtonText(
                (onThisLine ? "unassign" : "assign").AsLoc(), delegate
                {
                    m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(onThisLine
                        ? ModifyLineCmd.ACTION_UNASSIGN_SHIP
                        : ModifyLineCmd.ACTION_ASSIGN_SHIP, selected.Id, capturedShip.Id));
                });
            m_shipsColumn.Add(new Row(4.pt())
            {
                new Label($"{ship.GetTitle()} (home: {home}, {assignment})".AsLoc()),
                actionBtn
            });
        }
    }

    /// <summary>
    /// Window controller: auto-discovered by the game's DI scan of mod assemblies, registers the
    /// toolbar button, and lazily creates/opens the window — the same mechanism every vanilla
    /// manager window uses.
    /// </summary>
    [GlobalDependency(RegistrationMode.AsEverything)]
    public class Controller : WindowController<ShippingLinesManagerWindow>,
        IToolbarItemController, IUnityInputController
    {
        public bool IsVisible => true;
        public bool DeactivateShortcutsIfNotVisible => true;
        public event Action<IToolbarItemController> VisibilityChanged
        {
            add { }
            remove { }
        }

        public Controller(ControllerContext controllerContext, ToolbarHud toolbar)
            : base(controllerContext, null)
        {
            toolbar.AddMainMenuButton("Shipping lines".AsLoc(), this,
                "Assets/Unity/UserInterface/Toolbar/CargoShip.svg", 221f, null);
        }

        protected override ShippingLinesManagerWindow CreateWindow()
        {
            return Context.Resolver.Instantiate<ShippingLinesManagerWindow>(
                new object[1] { this });
        }

        protected override void OnActivate()
        {
            base.Window.Show();
        }
    }
}
