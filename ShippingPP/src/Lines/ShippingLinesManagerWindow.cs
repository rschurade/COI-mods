using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Entities;
using Mafi.Core.Input;
using Mafi.Core.Syncers;
using Mafi.Core.Trains;
using Mafi.Localization;
using Mafi.Unity.Camera;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Hud;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.Ui.Trains;
using Mafi.Unity.UiStatic;
using Mafi.Unity.UiStatic.Cursors;
using Mafi.Unity.UiStatic.Toolbar;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Component.Manipulators;
using Mafi.Unity.UiToolkit.Library;
using ShippingPP.Terminals;

namespace ShippingPP.Lines;

/// <summary>
/// The shipping lines manager — the mod's counterpart of the vanilla train lines manager window,
/// replicating its design: lines list on the left (color badge, bold name, stop/ship counts,
/// selection arrow; delete + new-line in the footer), the selected line's detail panel on the
/// right with rename title and color dropdown, and a stops tab built like the vanilla schedule
/// tab — the line-colored vertical route diagram with numbered stop badges, each stop a bordered
/// panel row with a drag handle strip (drag to reorder, the vanilla <see cref="Reorderable"/>
/// manipulator), and an add-stop footer that picks stops by clicking terminals/buoys on the map
/// (window hides, pick cursor + cursor message, shift adds multiple — the vanilla
/// station-selection interaction).
/// </summary>
public class ShippingLinesManagerWindow : Window
{
    private const string ICON_STOP = "Assets/Unity/UserInterface/Trains/TrainDestination.svg";
    private const string ICON_SHIP = "Assets/Unity/UserInterface/Toolbar/CargoShip.svg";
    private const string ICON_ARROW = "Assets/Unity/UserInterface/General/ArrowRight.svg";
    private const string ICON_FOCUS = "Assets/Unity/UserInterface/General/Search.svg";
    private const string ICON_TRASH = "Assets/Unity/UserInterface/General/Trash128.png";
    private const string ICON_PLUS = "Assets/Unity/UserInterface/General/PlusThin.svg";

    // The vanilla schedule tab's stop row colors (TrainLinesManagerScheduleTab.ScheduleItemUi).
    private static readonly ColorRgba STOP_ROW_BG = new ColorRgba(2961459u);
    private static readonly ColorRgba STOP_ROW_BORDER = new ColorRgba(8159624u);

    private readonly ShippingManager m_manager;
    private readonly EntitiesManager m_entitiesManager;
    private readonly IInputScheduler m_inputScheduler;
    private readonly CameraController m_cameraController;
    private readonly Controller m_controller;

    private readonly Column m_linesColumn;
    private readonly Column m_stopsColumn;
    private readonly Column m_shipsColumn;
    private readonly VerticalSingleTrainLineDiagramUi.Line m_lineDiagram;
    private readonly WarningLabel m_lineWarning;
    private readonly Label m_noStopsLabel;
    private readonly ButtonIcon m_deleteLineBtn;
    private readonly TitleWithRename m_detailTitle;
    private readonly Column m_detailsPanel;
    private readonly Panel m_noLinesPanel;

    private int m_selectedLineId = -1;

    public ShippingLinesManagerWindow(Controller controller, UiContext context,
        ShippingManager manager, EntitiesManager entitiesManager,
        CameraController cameraController)
        : base("Shipping lines".AsLoc())
    {
        m_controller = controller;
        m_manager = manager;
        m_entitiesManager = entitiesManager;
        m_inputScheduler = context.InputScheduler;
        m_cameraController = cameraController;

        MakeMovable();
        EnablePinning();
        WindowSize(900.px(), 700.px());

        // Left: lines list; footer with delete + new-line (the vanilla trains manager places
        // line deletion in this footer, not in the detail panel).
        m_linesColumn = new Column(0.pt());
        m_deleteLineBtn = new ButtonIcon(ICON_TRASH)
            .Tooltip(("Delete the selected line. Assigned ships return to automatic "
                + "dispatch.").AsLoc())
            .OnClick((Action)delegate
            {
                if (m_selectedLineId >= 0)
                {
                    m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                        ModifyLineCmd.ACTION_DELETE, m_selectedLineId, default(EntityId)));
                    m_selectedLineId = -1;
                }
            }, allowKeyPresses: false);
        var newLineBtn = new ButtonIconText(Button.General, ICON_PLUS, "Line".AsLoc())
            .Tooltip("Create a new shipping line.".AsLoc())
            .OnClick((Action)createNewLine, allowKeyPresses: false);
        // The vanilla trains manager's left side verbatim: a PanelWithHeader holding the
        // scrollable lines list and a footer with delete (left) and create (right).
        var left = new Column(1.pt())
        {
            (Action<Column>)delegate(Column c)
            {
                c.FlexBasis(35.Percent()).AlignItemsStretch().FlexGrow(1f);
            },
            new PanelWithHeader("Shipping lines".AsLoc())
            {
                (Action<PanelWithHeader>)delegate(PanelWithHeader c)
                {
                    c.FlexGrow(1f).BodyAdd(new ScrollColumn
                    {
                        m_linesColumn.AlignItemsStretch()
                    }.Fill(), new PanelFooterRow
                    {
                        (Action<PanelFooterRow>)delegate(PanelFooterRow r)
                        {
                            r.Apply(delegate(PanelFooterRow x)
                            {
                                x.Body.JustifyItemsSpaceBetween().PaddingLeftRight(1.pt());
                            }).BodyAdd(new Row(2.pt())
                            {
                                m_deleteLineBtn
                            }, new Row(2.pt())
                            {
                                newLineBtn
                            });
                        }
                    });
                }
            }
        };

        // Right: selected line details. The title doubles as the rename field (hover shows the
        // rename icon, same as vanilla line names).
        m_detailTitle = new TitleWithRename();
        m_detailTitle.EnableRename(delegate(string newName)
        {
            if (m_selectedLineId >= 0 && !string.IsNullOrWhiteSpace(newName))
            {
                m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                    ModifyLineCmd.ACTION_RENAME, m_selectedLineId, default(EntityId), newName));
            }
        });

        // Line color: the vanilla train-line palette in a swatch dropdown, exactly like the
        // trains manager's line color control (same palette, same ColorSplit swatches).
        var lineColorDropdown = new Dropdown<TrainLineColor>(
            (TrainLineColor option, int idx, bool inDropdown) =>
                new Row { new ColorSplit(option.Primary, option.Secondary) })
            .SetOptions(TrainLine.COLOR_PALETTE);
        lineColorDropdown.Tooltip("Line color".AsLoc());
        lineColorDropdown.OnValueChanged(delegate(TrainLineColor _, int index)
        {
            if (m_selectedLineId >= 0)
            {
                m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                    ModifyLineCmd.ACTION_SET_COLOR, m_selectedLineId, default(EntityId),
                    arg: index));
            }
        });
        lineColorDropdown.ObserveValueDropdown(delegate
        {
            ShippingLine line = m_selectedLineId >= 0
                ? m_manager.TryGetLine(m_selectedLineId) : null;
            return line?.Color ?? default(TrainLineColor);
        });

        // Stops tab, built like the vanilla schedule tab: dark rounded scroll container with
        // the line-colored route diagram beside the stop rows, and a footer with the add-stop
        // button (map picking) and the line warning.
        m_stopsColumn = new Column(2.pt()).FlexGrow(1f).AlignSelfStretch();
        m_lineDiagram = new VerticalSingleTrainLineDiagramUi.Line();
        m_noStopsLabel = new Label(
            "No stops yet — add local terminals and navigation buoys.".AsLoc()).FontItalic();
        m_lineWarning = new WarningLabel().MaxWidth(Percent.Eighty)
            .Padding(leftRight: 2.pt(), topBottom: 6)
            .Background(ColorRgba.Black.SetA(100)).BorderRadius(7);
        var addStopBtn = new ButtonIconText(Button.Primary, ICON_PLUS, "Add stop".AsLoc())
            .Tooltip(("Pick stops directly on the map: click a local terminal or a navigation "
                + "buoy to append it to the line. Hold shift to add several stops in a row; "
                + "right-click or Escape to finish.").AsLoc())
            .OnClick((Action)delegate
            {
                if (m_selectedLineId >= 0)
                {
                    m_controller.StartStopSelection(m_selectedLineId);
                }
            }, allowKeyPresses: false);
        var stopsTab = new Column
        {
            (Action<Column>)delegate(Column c)
            {
                c.AlignSelfStretch().FlexGrow(1f);
            },
            new UiComponent
            {
                (Action<UiComponent>)delegate(UiComponent c)
                {
                    c.AlignSelfStretch().Fill().Padding(2, 2, 6, 6)
                        .Background(ColorRgba.Black.SetA(190))
                        .BorderRadius(7)
                        .Border(1, Theme.BorderColor);
                },
                new ScrollColumn
                {
                    (Action<ScrollColumn>)delegate(ScrollColumn c)
                    {
                        c.AlignSelfStretch().Fill();
                    },
                    new Column(2.pt())
                    {
                        m_noStopsLabel.AlignSelfCenter().MarginTop(3.pt()),
                        new Row
                        {
                            (Action<Row>)delegate(Row c)
                            {
                                Row component = c.AlignSelfStretch().FlexGrow(1f);
                                Px? left = 2.pt();
                                Px? right = 2.pt();
                                component.Padding(null, right, null, left);
                            },
                            m_lineDiagram.FlexGrow(0f).AlignSelfStretch(),
                            m_stopsColumn
                        }
                    }
                }
            },
            new PanelFooterRow
            {
                (Action<PanelFooterRow>)delegate(PanelFooterRow c)
                {
                    PanelFooterRow component = c.AlignSelfStretch();
                    Px? bottom = -4;
                    Px? left = 1.pt();
                    Px? right = 1.pt();
                    component.Margin(null, right, bottom, left).PaddingBottom(1.pt())
                        .BodyAdd(delegate(Row e)
                        {
                            e.JustifyItemsSpaceBetween().PaddingLeftRight(2.pt());
                        }, addStopBtn, m_lineWarning);
                }
            }
        };

        // Ships tab: same dark scroll container for visual parity.
        m_shipsColumn = new Column(2.pt()).AlignSelfStretch();
        var shipsTab = new Column
        {
            (Action<Column>)delegate(Column c)
            {
                c.AlignSelfStretch().FlexGrow(1f);
            },
            new UiComponent
            {
                (Action<UiComponent>)delegate(UiComponent c)
                {
                    c.AlignSelfStretch().Fill().Padding(2, 2, 6, 6)
                        .Background(ColorRgba.Black.SetA(190))
                        .BorderRadius(7)
                        .Border(1, Theme.BorderColor);
                },
                new ScrollColumn
                {
                    (Action<ScrollColumn>)delegate(ScrollColumn c)
                    {
                        c.AlignSelfStretch().Fill();
                    },
                    m_shipsColumn
                }
            }
        };

        var tabs = new TabContainer();
        tabs.Add("Stops".AsLoc(), stopsTab, Scroll.No);
        tabs.Add("Ships".AsLoc(), shipsTab, Scroll.No);
        tabs.FlexGrow(1f);

        m_detailsPanel = new Column(2.pt())
        {
            (Action<Column>)delegate(Column c)
            {
                c.AlignItemsStretch().FlexGrow(1f);
            },
            new Panel().MarginBottom(1.pt()).AlignSelfStretch().BodyAdd(
                delegate(Column e)
                {
                    e.PaddingTopBottom(2.pt());
                },
                new Row(2.pt())
                {
                    m_detailTitle,
                    new VerticalDivider(),
                    lineColorDropdown
                }),
            tabs.AlignSelfStretch()
        };
        // Right side like vanilla: a plain Panel with a centered hint when nothing is
        // selected, the details column otherwise.
        m_noLinesPanel = new Panel().BodyAdd(new Label(
            "No line selected. Create a line and add terminal stops.".AsLoc())
            .AlignSelfCenter().PaddingTop(4.pt()));
        var right = new Column(1.pt())
        {
            (Action<Column>)delegate(Column c)
            {
                c.FlexBasis(65.Percent()).AlignItemsStretch().FlexGrow(1f);
            },
            m_noLinesPanel.Fill(),
            m_detailsPanel.Fill()
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
            rebuildAll();
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

    private string titleOfStop(Mafi.Core.Entities.Static.StaticEntity stop)
    {
        return stop.Prototype is NavBuoyProto
            ? $"[buoy] {stop.GetTitle()}"
            : stop.GetTitle();
    }

    private string computeStateHash()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(m_selectedLineId).Append('|');
        foreach (ShippingLine line in m_manager.AllLines)
        {
            sb.Append(line.Id).Append(':').Append(line.Name).Append(':')
                .Append(line.Color.Primary.GetHashCode()).Append(':')
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

    private void rebuildAll()
    {
        // Like vanilla: with no (valid) selection, the first line is selected automatically —
        // covers both opening the window and deleting the selected line.
        if (m_manager.TryGetLine(m_selectedLineId) == null)
        {
            m_selectedLineId = -1;
            foreach (ShippingLine line in m_manager.AllLines)
            {
                m_selectedLineId = line.Id;
                break;
            }
        }

        // Ships per line (shown in the lines list rows).
        var shipCounts = new Dict<int, int>();
        foreach (CargoShipV2 ship in m_entitiesManager.GetAllEntitiesOfType<CargoShipV2>())
        {
            if (ShippingManager.IsLocalShip(ship) && !ship.IsDestroyed)
            {
                int? lineId = m_manager.GetLineIdFor(ship);
                if (lineId.HasValue)
                {
                    shipCounts.TryGetValue(lineId.Value, out int count);
                    shipCounts[lineId.Value] = count + 1;
                }
            }
        }

        // Lines list: color badge, bold name, stop/ship counts, selection arrow — the vanilla
        // train lines menu item layout.
        m_linesColumn.Clear();
        int index = 0;
        int total = 0;
        foreach (ShippingLine _ in m_manager.AllLines)
        {
            total++;
        }
        foreach (ShippingLine line in m_manager.AllLines)
        {
            ShippingLine captured = line;
            bool isSelected = line.Id == m_selectedLineId;
            shipCounts.TryGetValue(line.Id, out int shipsOnLine);
            var row = new ButtonRow(Button.Area);
            row.Gap(2.pt());
            row.Add(
                new ColorSplit(line.Color.Primary, line.Color.Secondary),
                new Column(1.pt())
                {
                    new Label(line.Name.AsLoc()).FontBold(),
                    new Row(1.pt())
                    {
                        new Label($"{line.StopCount}".AsLoc()),
                        new Icon(ICON_STOP).Size(16.px(), Px.Auto),
                        new VerticalDivider(),
                        new Label($"{shipsOnLine}".AsLoc()),
                        new Icon(ICON_SHIP).Size(16.px(), Px.Auto)
                    }
                },
                new Icon(ICON_ARROW).Small().AbsolutePosition(null, 6.px())
                    .Color(Theme.PrimaryColor).Visible(isSelected));
            row.OnClick((Action)delegate
            {
                m_selectedLineId = captured.Id;
            }, allowKeyPresses: false);
            row.Selected(isSelected);
            row.BorderBottom(index == total - 1 ? Px.NotSet : 1.px(), Theme.BorderColor);
            m_linesColumn.Add(row);
            index++;
        }

        ShippingLine selected = m_selectedLineId >= 0
            ? m_manager.TryGetLine(m_selectedLineId) : null;
        m_noLinesPanel.Visible(selected == null);
        m_detailsPanel.Visible(selected != null);
        m_deleteLineBtn.Enabled(selected != null);
        if (selected == null)
        {
            return;
        }
        m_detailTitle.Text(selected.Name.AsLoc());

        // Stops: vanilla schedule rows — numbered line-colored badge on the route diagram,
        // bordered panel with the stop title and focus/remove buttons, and a drag-handle strip
        // (drag to reorder, using the vanilla Reorderable manipulator).
        m_stopsColumn.Clear();
        m_noStopsLabel.Visible(selected.StopCount == 0);
        m_lineDiagram.Visible(selected.StopCount > 0);
        m_lineDiagram.Colors(selected.Color.Primary, selected.Color.Secondary);
        m_lineWarning.Visible(!selected.HasUsableStops);
        m_lineWarning.Values(
            "A line needs at least two local terminal stops.".AsLoc(),
            ("Ships sail the stops in order and only exchange cargo at local terminals; "
                + "buoys are waypoints. With fewer than two terminals the line's ships have "
                + "no route to sail.").AsLoc());
        for (int i = 0; i < selected.StopCount; i++)
        {
            Mafi.Core.Entities.Static.StaticEntity stop = selected.StopAtOrNull(i);
            if (stop == null)
            {
                continue;
            }
            Mafi.Core.Entities.Static.StaticEntity captured = stop;
            int capturedIndex = i;
            var focusBtn = new ButtonIcon(Button.IconOnly, ICON_FOCUS)
                .Tooltip("Show on the map".AsLoc())
                .OnClick((Action)delegate
                {
                    m_cameraController.PanTo(captured.Position2f);
                }, allowKeyPresses: false);
            var removeBtn = new ButtonIcon(Button.IconOnlyDanger, ICON_TRASH)
                .Tooltip("Remove this stop from the line".AsLoc())
                .OnClick((Action)delegate
                {
                    m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                        ModifyLineCmd.ACTION_REMOVE_STOP, selected.Id, captured.Id,
                        arg: capturedIndex));
                }, allowKeyPresses: false);
            var badge = new VerticalSingleTrainLineDiagramUi.Stop()
                .Colors(selected.Color.Primary, selected.Color.Secondary)
                .Number(i + 1);
            var dragHandle = new Column().Class(Cls.dragHandle).AlignSelfStretch();
            var row = new Row
            {
                (Action<Row>)delegate(Row c)
                {
                    c.AlignSelfStretch();
                },
                new Column
                {
                    (Action<Column>)delegate(Column c)
                    {
                        c.AlignSelfStretch();
                    },
                    badge
                },
                new Row(Outer.ShadowAll)
                {
                    (Action<Row>)delegate(Row c)
                    {
                        c.Background(STOP_ROW_BG).Border(1.px(), STOP_ROW_BORDER, 5)
                            .OverflowHidden()
                            .FlexGrow(1f)
                            .AlignItemsStart()
                            .AlignSelfStretch();
                    },
                    new Row
                    {
                        (Action<Row>)delegate(Row c)
                        {
                            c.FlexGrow(1f).AlignSelfStretch().JustifyItemsSpaceBetween()
                                .AlignItemsCenter().MinHeight(44.px())
                                .Padding(6, left: 2.pt(), right: 2.pt());
                        },
                        new Label(titleOfStop(stop).AsLoc()).FontBold(),
                        new Row(1.pt())
                        {
                            focusBtn.FillRow(),
                            removeBtn.FillRow()
                        }
                    }
                },
                new Row
                {
                    (Action<Row>)delegate(Row c)
                    {
                        c.FlexGrow(0f).AlignSelfStretch().Width(15.px())
                            .BorderLeft(1.px(), Theme.BorderColor)
                            .BorderRadiusRight(6)
                            .Background(Theme.BackgroundDark);
                    },
                    dragHandle
                }
            };
            var reorderable = new Reorderable(dragHandle.RootElement);
            reorderable.OnOrderChanged += delegate(int oldIndex, int newIndex)
            {
                m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                    ModifyLineCmd.ACTION_REORDER_STOP, selected.Id, default(EntityId),
                    arg: capturedIndex, arg2: newIndex));
            };
            row.AddManipulator(reorderable);
            m_stopsColumn.Add(row);
        }

        // Ships tab: this line's ships first, then unassigned, then other lines' ships.
        m_shipsColumn.Clear();
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (CargoShipV2 ship in m_entitiesManager.GetAllEntitiesOfType<CargoShipV2>())
            {
                if (!ShippingManager.IsLocalShip(ship) || ship.IsDestroyed)
                {
                    continue;
                }
                int? lineId = m_manager.GetLineIdFor(ship);
                bool onThisLine = lineId == selected.Id;
                int shipPass = onThisLine ? 0 : (lineId.HasValue ? 2 : 1);
                if (shipPass != pass)
                {
                    continue;
                }
                CargoShipV2 capturedShip = ship;
                string home = ship.AssignedDepot.ValueOrNull?.GetTitle() ?? "-";
                string assignment = lineId.HasValue
                    ? (onThisLine ? "on this line" : $"on line {lineId.Value}")
                    : "automatic dispatch";
                var focusShipBtn = new ButtonIcon(Button.IconOnly, ICON_FOCUS)
                    .Tooltip("Show on the map".AsLoc())
                    .OnClick((Action)delegate
                    {
                        m_cameraController.PanTo(capturedShip.Position2f);
                    }, allowKeyPresses: false);
                var actionBtn = new ButtonText(
                    (onThisLine ? "Unassign" : "Assign").AsLoc(), delegate
                    {
                        m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(onThisLine
                            ? ModifyLineCmd.ACTION_UNASSIGN_SHIP
                            : ModifyLineCmd.ACTION_ASSIGN_SHIP, selected.Id, capturedShip.Id));
                    });
                m_shipsColumn.Add(new Row
                {
                    (Action<Row>)delegate(Row c)
                    {
                        c.AlignSelfStretch().JustifyItemsSpaceBetween()
                            .AlignItemsCenter().MinHeight(44.px())
                            .Background(STOP_ROW_BG).Border(1.px(), STOP_ROW_BORDER, 5)
                            .Padding(6, left: 2.pt(), right: 2.pt());
                    },
                    new Row(2.pt())
                    {
                        new Icon(ICON_SHIP).Size(20.px(), Px.Auto),
                        new Column(1.pt())
                        {
                            new Label(ship.GetTitle().AsLoc()).FontBold(),
                            new Label($"Home: {home} — {assignment}".AsLoc())
                                .Color(Theme.InactiveColor)
                        }
                    },
                    new Row(1.pt())
                    {
                        focusShipBtn,
                        actionBtn
                    }
                });
            }
        }
    }

    /// <summary>
    /// Window controller: auto-discovered by the game's DI scan of mod assemblies, registers the
    /// toolbar button, and lazily creates/opens the window — the same mechanism every vanilla
    /// manager window uses. Also owns the map stop-selection mode (the vanilla train station
    /// selection interaction): while active the controller acts as a tool — the window hides,
    /// the pick cursor and a cursor message guide the player, clicking a terminal/buoy appends
    /// it to the line (shift keeps adding), right-click/Escape finishes.
    /// </summary>
    [GlobalDependency(RegistrationMode.AsEverything)]
    public class Controller : WindowController<ShippingLinesManagerWindow>,
        IToolbarItemController, IUnityInputController
    {
        // Highlight colors while picking stops, matching the vanilla station selection:
        // candidates glow yellow, the hovered one green, stops already on the line white.
        private static readonly ColorRgba HIGHLIGHT_CANDIDATE = ColorRgba.Yellow;
        private static readonly ColorRgba HIGHLIGHT_HOVERED = ColorRgba.Green;
        private static readonly ColorRgba HIGHLIGHT_ON_LINE = ColorRgba.White;

        private readonly CursorPickingManager m_cursorPickingManager;
        private readonly ShortcutsManager m_shortcutsManager;
        private readonly IInputScheduler m_inputScheduler;
        private readonly CursorMessage m_cursorMessage;
        private readonly Cursoor m_selectCursor;
        private readonly EntitiesManager m_entitiesManager;
        private readonly Mafi.Unity.Entities.EntitiesRenderingManager m_entitiesRenderer;
        private readonly ShippingManager m_shippingManager;

        private readonly Dict<Mafi.Core.Entities.Static.StaticEntity, ulong> m_highlights =
            new Dict<Mafi.Core.Entities.Static.StaticEntity, ulong>();
        private Mafi.Core.Entities.Static.StaticEntity m_hoveredEntity;

        private int m_lineIdForSelection = -1;
        private int m_selectedStopsCount;
        /// <summary>Ship whose new home port is being picked on the map (null = not picking).
        /// This mode is entered from the ship window, not from the lines window.</summary>
        private CargoShipV2 m_shipForHome;

        /// <summary>The controller of the current session — the ship-inspector patch (plain
        /// Harmony code outside DI) starts home-port picking through this.</summary>
        public static Controller Current { get; private set; }

        public bool IsVisible => true;
        public bool DeactivateShortcutsIfNotVisible => true;
        public event Action<IToolbarItemController> VisibilityChanged
        {
            add { }
            remove { }
        }

        public override ControllerConfig Config =>
            m_lineIdForSelection >= 0 || m_shipForHome != null
                ? ControllerConfig.Tool
                : ControllerConfig.Window;

        public Controller(ControllerContext controllerContext, ToolbarHud toolbar,
            CursorPickingManager cursorPickingManager, CursorManager cursorManager,
            ShortcutsManager shortcutsManager, IInputScheduler inputScheduler,
            NewInstanceOf<CursorMessage> cursorMessage, EntitiesManager entitiesManager,
            Mafi.Unity.Entities.EntitiesRenderingManager entitiesRenderer,
            ShippingManager shippingManager)
            : base(controllerContext, null)
        {
            m_cursorPickingManager = cursorPickingManager;
            m_shortcutsManager = shortcutsManager;
            m_inputScheduler = inputScheduler;
            m_cursorMessage = cursorMessage.Instance;
            m_entitiesManager = entitiesManager;
            m_entitiesRenderer = entitiesRenderer;
            m_shippingManager = shippingManager;
            m_selectCursor = cursorManager.RegisterCursor(CursorsStyles.InspectorHover);
            toolbar.AddMainMenuButton("Shipping lines".AsLoc(), this,
                "Assets/Unity/UserInterface/Toolbar/CargoShip.svg", 221f, null);
            Current = this;
        }

        protected override ShippingLinesManagerWindow CreateWindow()
        {
            return Context.Resolver.Instantiate<ShippingLinesManagerWindow>(
                new object[1] { this });
        }

        protected override void OnActivate()
        {
            // Home-port picking activates this controller as a pure tool — the lines window
            // stays out of sight.
            if (m_shipForHome == null)
            {
                base.Window.Show();
            }
            else
            {
                base.Window.Hide();
            }
        }

        protected override void OnDeactivate()
        {
            stopStopSelection(showWindow: false);
            stopHomeSelection();
        }

        /// <summary>Enters the map home-port-picking mode for the ship (started from the ship
        /// window's home-port panel): click a local terminal to make it the ship's new home,
        /// right-click/Escape to cancel.</summary>
        public void StartHomeSelection(CargoShipV2 ship)
        {
            if (ship == null || ship.IsDestroyed)
            {
                return;
            }
            stopStopSelection(showWindow: false);
            m_shipForHome = ship;
            if (!IsActive)
            {
                Context.InputManager.ActivateNewController(this);
            }
            base.Window.Hide();
            m_selectCursor.Show();
            highlightHomeCandidates();
        }

        /// <summary>Glow every terminal the ship could be homed at: candidates yellow, the
        /// current home (if it still exists) white.</summary>
        private void highlightHomeCandidates()
        {
            CargoDepot home = m_shipForHome.AssignedDepot.ValueOrNull;
            foreach (LocalTerminal terminal in
                m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
            {
                if (isValidHome(terminal))
                {
                    setHighlight(terminal,
                        terminal == home ? HIGHLIGHT_ON_LINE : HIGHLIGHT_CANDIDATE);
                }
            }
        }

        private static bool isValidHome(LocalTerminal terminal)
        {
            return !terminal.IsDestroyed && terminal.IsConstructed;
        }

        private void stopHomeSelection()
        {
            if (m_shipForHome == null)
            {
                return;
            }
            m_shipForHome = null;
            m_selectCursor.Hide();
            m_cursorMessage.Hide();
            clearHighlights();
            // Entered as a tool from the ship window — leave the input stack entirely instead
            // of falling back to the (hidden) lines window.
            if (IsActive)
            {
                Context.InputManager.DeactivateController(this);
            }
        }

        /// <summary>Enters the map stop-picking mode for the line (vanilla station-selection
        /// interaction: window hides, pick cursor on, click to add stops).</summary>
        public void StartStopSelection(int lineId)
        {
            if (lineId < 0)
            {
                return;
            }
            m_lineIdForSelection = lineId;
            m_selectedStopsCount = 0;
            base.Window.Hide();
            m_selectCursor.Show();
            highlightAllCandidates();
        }

        /// <summary>Glow every pickable stop on the map: candidates yellow, stops already on
        /// the line white (the vanilla station-selection highlighting).</summary>
        private void highlightAllCandidates()
        {
            ShippingLine line = m_shippingManager.TryGetLine(m_lineIdForSelection);
            foreach (Mafi.Core.Entities.Static.StaticEntity entity in
                m_entitiesManager.GetAllEntitiesOfType<Mafi.Core.Entities.Static.StaticEntity>())
            {
                if (isValidStop(entity))
                {
                    bool onLine = line != null && line.ContainsStop(entity);
                    setHighlight(entity, onLine ? HIGHLIGHT_ON_LINE : HIGHLIGHT_CANDIDATE);
                }
            }
        }

        private void setHighlight(Mafi.Core.Entities.Static.StaticEntity entity,
            ColorRgba color)
        {
            if (m_highlights.TryGetValue(entity, out ulong handle))
            {
                m_entitiesRenderer.RemoveHighlight(handle);
            }
            m_highlights[entity] = m_entitiesRenderer.AddHighlight(entity, color);
        }

        private void clearHighlights()
        {
            foreach (System.Collections.Generic.KeyValuePair<
                Mafi.Core.Entities.Static.StaticEntity, ulong> pair in m_highlights)
            {
                m_entitiesRenderer.RemoveHighlight(pair.Value);
            }
            m_highlights.Clear();
            m_hoveredEntity = null;
        }

        public override bool InputUpdate()
        {
            if (m_shipForHome != null)
            {
                return homeSelectionInputUpdate();
            }
            if (m_lineIdForSelection < 0)
            {
                return base.InputUpdate();
            }
            if (m_shortcutsManager.IsSecondaryActionUp
                || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape))
            {
                stopStopSelection(showWindow: true);
                return true;
            }
            Option<Mafi.Core.Entities.Static.StaticEntity> entity =
                m_cursorPickingManager.PickEntity<Mafi.Core.Entities.Static.StaticEntity>(
                    isValidStop);
            updateHoverHighlight(entity.ValueOrNull);
            if (entity.HasValue)
            {
                m_cursorMessage.MessageInfo(
                    $"Add \"{entity.Value.GetTitle()}\" to the line".AsLoc());
            }
            else
            {
                m_cursorMessage.MessageInfo(("Click a local terminal or a navigation buoy to "
                    + "add it as a stop (shift: add multiple, right-click: finish)").AsLoc());
            }
            if (m_shortcutsManager.IsPrimaryActionDown && entity.HasValue)
            {
                m_inputScheduler.ScheduleInputCmd(new ModifyLineCmd(
                    ModifyLineCmd.ACTION_ADD_STOP, m_lineIdForSelection, entity.Value.Id));
                m_selectedStopsCount++;
                // The stop is on the line now — recolor it, whatever happens next.
                setHighlight(entity.Value, HIGHLIGHT_ON_LINE);
                m_hoveredEntity = null;
                if (!m_shortcutsManager.IsOn(m_shortcutsManager.PlaceMultiple))
                {
                    stopStopSelection(showWindow: true);
                }
                return true;
            }
            if (m_shortcutsManager.IsUp(m_shortcutsManager.PlaceMultiple)
                && m_selectedStopsCount > 0)
            {
                stopStopSelection(showWindow: true);
                return true;
            }
            return false;
        }

        private static bool isValidStop(Mafi.Core.Entities.Static.StaticEntity entity)
        {
            return !entity.IsDestroyed && entity.IsConstructed
                && (entity is LocalTerminal || entity.Prototype is NavBuoyProto);
        }

        private bool homeSelectionInputUpdate()
        {
            if (m_shipForHome.IsDestroyed || m_shortcutsManager.IsSecondaryActionUp
                || UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape))
            {
                stopHomeSelection();
                return true;
            }
            Option<LocalTerminal> terminal =
                m_cursorPickingManager.PickEntity<LocalTerminal>(isValidHome);
            updateHoverHighlight(terminal.ValueOrNull);
            if (terminal.HasValue)
            {
                m_cursorMessage.MessageInfo(
                    $"Make \"{terminal.Value.GetTitle()}\" the ship's home port".AsLoc());
            }
            else
            {
                m_cursorMessage.MessageInfo(("Click a local terminal to make it this ship's "
                    + "new home port (right-click: cancel)").AsLoc());
            }
            if (m_shortcutsManager.IsPrimaryActionDown && terminal.HasValue)
            {
                m_inputScheduler.ScheduleInputCmd(new Terminals.SetShipHomeCmd(
                    m_shipForHome.Id, terminal.Value.Id));
                stopHomeSelection();
                return true;
            }
            return false;
        }

        /// <summary>Restores the previously hovered entity's base glow and turns the newly
        /// hovered candidate green.</summary>
        private void updateHoverHighlight(Mafi.Core.Entities.Static.StaticEntity hovered)
        {
            if (hovered == m_hoveredEntity)
            {
                return;
            }
            if (m_hoveredEntity != null && m_highlights.ContainsKey(m_hoveredEntity))
            {
                setHighlight(m_hoveredEntity, baseHighlightColor(m_hoveredEntity));
            }
            if (hovered != null)
            {
                setHighlight(hovered, HIGHLIGHT_HOVERED);
            }
            m_hoveredEntity = hovered;
        }

        /// <summary>The un-hovered glow of an entity in the current picking mode: white for
        /// "already chosen" (stop on the line / the ship's current home), yellow candidates.</summary>
        private ColorRgba baseHighlightColor(Mafi.Core.Entities.Static.StaticEntity entity)
        {
            if (m_shipForHome != null)
            {
                return entity == m_shipForHome.AssignedDepot.ValueOrNull
                    ? HIGHLIGHT_ON_LINE
                    : HIGHLIGHT_CANDIDATE;
            }
            ShippingLine line = m_shippingManager.TryGetLine(m_lineIdForSelection);
            bool onLine = line != null && line.ContainsStop(entity);
            return onLine ? HIGHLIGHT_ON_LINE : HIGHLIGHT_CANDIDATE;
        }

        private void stopStopSelection(bool showWindow)
        {
            if (m_lineIdForSelection >= 0)
            {
                m_lineIdForSelection = -1;
                m_selectCursor.Hide();
                m_cursorMessage.Hide();
                clearHighlights();
            }
            if (showWindow)
            {
                base.Window.Show();
            }
        }
    }
}
