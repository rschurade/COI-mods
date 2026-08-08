using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Syncers;
using Mafi.Core.Vehicles;
using Mafi.Localization;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.Ui.Library.Inspectors;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using Mafi.Unity.Vehicles;

namespace ShippingPP.Terminals;

/// <summary>
/// The local terminal's window. Picked up automatically for <see cref="LocalTerminal"/> entities
/// (the inspector manager scans mod assemblies and selects by most-derived entity type), so
/// vanilla cargo depots keep their vanilla window untouched.
///
/// Replicates the vanilla depot window's content — the ship-fuel buffer with truck import/export
/// sliders and the ship-pathability overlay toggle — and adds the mod's ship panel: a build-ship
/// button that opens a construction site (materials delivered by truck), with construction
/// progress and a cancel button while the build runs.
/// </summary>
public class LocalTerminalInspector : BaseInspector<LocalTerminal>
{
    private readonly ShippingManager m_shippingManager;

    public LocalTerminalInspector(UiContext context, VehicleBuffersRegistry vehicleBuffersRegistry,
        ShippingManager shippingManager)
        : base(context)
    {
        m_shippingManager = shippingManager;

        // Ship-pathability overlay toggle (same as the vanilla depot window).
        ShipsPathabilityOverlayRenderer navOverlayRenderer = context.ShipsPathabilityOverlayRenderer;
        TopRightButtons.AddAndReturn(new ButtonIcon("Assets/Unity/UserInterface/General/Path.svg")
            .Tooltip(Tr.EntityToggleNavigationOverlay__Tooltip)
            .OnClick((System.Action)delegate
            {
                if (navOverlayRenderer.IsOverlayShown)
                {
                    navOverlayRenderer.HideOverlay();
                }
                else
                {
                    navOverlayRenderer.ShowOverlayFor(
                        Entity.Prototype.CargoShipProto.PathFindingParams.PathabilityQueryMask);
                }
            }, allowKeyPresses: false)
            .Toggleable()
            .ObserveSelected(() => navOverlayRenderer.IsOverlayShown));

        // --- Ship panel: build button + construction progress. ---
        ButtonIconText buildBtn = new ButtonIconText(Button.Primary,
            "Assets/Unity/UserInterface/General/Build.svg", Txt.Terminal_BuildShip)
            .NoShrink().AlignSelfCenter()
            .OnClick((System.Action)delegate
            {
                ScheduleCommand(new SetShipConstructionCmd(Entity.Id, isConstructing: true));
            }, allowKeyPresses: false);
        // The cost depends on the terminal tier (bigger terminals build bigger ships), so the
        // tooltip is built per shown entity, not once.
        this.Observe(() => Entity.Prototype).Do(delegate(CargoDepotProto proto)
        {
            string costText = "";
            foreach (ProductQuantity pq in ShippingManager.GetShipBuildCost(Entity).Products)
            {
                costText += (costText.Length == 0 ? "" : ", ") + Txt.ProductQuantity(
                    pq.Quantity.Value, pq.Product.Strings.Name.TranslatedString).Value;
            }
            int modules = proto.CargoShipProto != null
                ? proto.CargoShipProto.MaximumModulesCount : 2;
            LocStrFormatted tooltip = Txt.BuildShipTooltip(modules);
            if (costText.Length != 0)
            {
                tooltip = tooltip + " ".AsLoc() + Txt.BuildShipRequires(costText);
            }
            buildBtn.Tooltip(tooltip);
        });

        var constrUi = new ConstructionUi();
        PanelFooterRow constrUiPanel;
        var fleetLabel = new Label().MarginTop(2.pt());
        AddPanelWithHeader(new Row(4.pt())
        {
            fleetLabel,
            buildBtn
        }).Title(Tr.StatsCat__CargoShips, Txt.Terminal_Ships_Tooltip);
        this.Observe(() => m_shippingManager.CountShipsHomedAt(Entity))
            .Do(delegate(int count)
            {
                ((IComponentWithText)fleetLabel).SetValue(Txt.ShipsCount(count));
            });
        AddPanel(delegate(Column c)
        {
            c.Gap(2.pt());
        }, constrUiPanel = new PanelFooterRow { constrUi.AlignSelfStretch() });

        constrUi.AddCancelBtn(delegate
        {
            ScheduleCommand(new SetShipConstructionCmd(Entity.Id, isConstructing: false));
        }, () => EntityValidationResult.Success);

        this.Observe(() => m_shippingManager.IsBuildingShip(Entity))
            .Observe(() => ShippingManager.CountBuiltModules(Entity))
            .Do(delegate(bool isBuilding, int builtModules)
            {
                buildBtn.Visible(!isBuilding);
                // A ship without cargo modules to mirror would be useless — require at least
                // one module on the terminal before a ship can be laid down.
                buildBtn.Enabled(builtModules > 0);
            });
        this.Observe(() => m_shippingManager.TryGetShipBuildProgress(Entity))
            .DoOnSync(delegate(Option<ConstructionProgress> progress)
            {
                constrUiPanel.Visible(progress.HasValue);
                if (progress.HasValue)
                {
                    constrUi.SetProgress(progress.Value);
                }
            });
        constrUi.Observe(() => m_shippingManager.TryGetShipBuildProgress(Entity))
            .Do(delegate(Option<ConstructionProgress> progress)
            {
                if (progress.IsNone)
                {
                    return;
                }
                Percent percent = progress.Value.Progress;
                if (progress.Value.WasBlockedOnProductsLastSim)
                {
                    constrUi.As(percent, Tr.ConstructionState__WaitingForDelivery,
                        DisplayState.Warning);
                }
                else
                {
                    constrUi.As(percent, Txt.Terminal_BuildingShip, DisplayState.Positive);
                }
                constrUi.SetProgress(percent, isDeconstruction: false);
            });

        // --- Shipping: offer/request direction, one row per PRODUCT. ---
        // Several modules can store the same product; giving each its own checkbox allowed
        // conflicting directions for one cargo type, so the rows are per distinct product and
        // a toggle applies to every module storing it. Rows for up to 8 distinct products (the
        // largest depot has 8 slots); unused rows stay hidden.
        var slotsColumn = new Column(2.pt());
        for (int rowIndex = 0; rowIndex < 8; rowIndex++)
        {
            int i = rowIndex;
            var slotIcon = new Icon().Large().MarginTop(2.pt());
            var slotToggle = new Mafi.Unity.UiToolkit.Library.Toggle(standalone: true)
                .Label(Txt.Terminal_ModuleExport)
                .Tooltip(Txt.Terminal_ModuleExport_Tooltip);
            slotToggle.OnValueChanged(delegate
            {
                ProductProto product = productForRow(i);
                if (product == null)
                {
                    return;
                }
                // A half-configured product (directions disagree, possible in old saves)
                // flips to all-export first, then toggles as one from there.
                bool target = !allModulesExport(product);
                foreach (Mafi.Option<CargoDepotModule> slot in Entity.Modules)
                {
                    CargoDepotModule module = slot.ValueOrNull;
                    if (module != null && module.StoredProduct.ValueOrNull == product
                        && m_shippingManager.IsExportModule(module) != target)
                    {
                        ScheduleCommand(new SetModuleDirectionCmd(module.Id, target));
                    }
                }
            });
            slotToggle.ObserveValue(delegate
            {
                ProductProto product = productForRow(i);
                return product != null && allModulesExport(product);
            });
            var thresholdDropdown = new Dropdown<int>(
                (int opt, int idx, bool inDropdown) => new Label($"{opt} %".AsLoc()))
                .SetOptions(new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 });
            thresholdDropdown.Tooltip(Txt.Terminal_Threshold_Tooltip);
            thresholdDropdown.OnValueChanged(delegate(int value, int _)
            {
                ProductProto product = productForRow(i);
                if (product == null)
                {
                    return;
                }
                foreach (Mafi.Option<CargoDepotModule> slot in Entity.Modules)
                {
                    CargoDepotModule module = slot.ValueOrNull;
                    if (module != null && module.StoredProduct.ValueOrNull == product
                        && m_shippingManager.GetModuleThreshold(module) != value)
                    {
                        ScheduleCommand(new SetModuleThresholdCmd(module.Id, value));
                    }
                }
            });
            thresholdDropdown.ObserveValueDropdown(delegate
            {
                ProductProto product = productForRow(i);
                CargoDepotModule module = product != null ? firstModuleOf(product) : null;
                return module != null ? m_shippingManager.GetModuleThreshold(module) : 100;
            });
            var slotRow = new Row(4.pt())
            {
                slotIcon,
                slotToggle,
                thresholdDropdown
            };
            slotsColumn.Add(slotRow);
            this.Observe(delegate
            {
                return productForRow(i);
            }).Do(delegate(ProductProto product)
            {
                slotRow.Visible(product != null);
                if (product != null)
                {
                    slotIcon.Value(((IProtoWithIcon)product).SomeOption());
                    slotIcon.Tooltip(product.Strings.Name);
                }
            });
        }
        AddPanelWithHeader(slotsColumn).Title(Txt.Terminal_Shipping_Title,
            Txt.Terminal_Shipping_Tooltip);

        // --- Ship fuel panel (same as the vanilla depot window). ---
        var buffer = new BufferWithSlider(addPendingBars: true);
        Icon productIcon = new Icon().Large().MarginTop(2.pt());
        BufferSlider truckImportSlider = buffer.AddTruckImportSlider();
        truckImportSlider.OnValueChanged(delegate(int step)
        {
            ScheduleCommand(new CargoDepotSetFuelSliderStepCmd(Entity.Id, step, null));
        });
        BufferSlider truckExportSlider = buffer.AddTruckExportSlider();
        truckExportSlider.OnValueChanged(delegate(int step)
        {
            ScheduleCommand(new CargoDepotSetFuelSliderStepCmd(Entity.Id, null, step));
        });
        truckImportSlider.OppositeSlider = truckExportSlider;
        truckExportSlider.OppositeSlider = truckImportSlider;
        var priorityToggle = new CustomPriorityToggleForBuffer(Context.InputScheduler,
            () => Entity, "FuelImportPrio", "FuelExportPrio",
            Tr.ImportPriority__ShipFuelTooltip, Tr.ExportPriority__ShipFuelTooltip);
        AddPanelWithHeader(new Row(4.pt())
        {
            (System.Action<Row>)delegate(Row c)
            {
                c.AlignItemsStart();
            },
            productIcon,
            buffer,
            priorityToggle
        }).Title(Tr.FuelForShip__Title, Tr.FuelForShip__Tooltip);
        this.Observe(() => Entity.FuelBuffer.Product)
            .Observe(() => Entity.FuelBuffer.Quantity)
            .Observe(() => Entity.FuelBuffer.Capacity)
            .Do(delegate(ProductProto p, Quantity q, Quantity c)
            {
                productIcon.Value(((IProtoWithIcon)p).SomeOption());
                buffer.ProductName(p.Strings.Name);
                buffer.Values(q, c);
                truckImportSlider.ProductIcon(p);
                truckExportSlider.ProductIcon(p);
            });
        this.Observe(() => Entity.FuelBuffer.Capacity)
            .Observe(() => Entity.FuelBuffer.ImportUntilPercent)
            .Observe(() => Entity.FuelBuffer.ExportFromPercent)
            .Do(delegate(Quantity cap, Percent importUntil, Percent exportFrom)
            {
                BufferWithSlider.UpdateImportExportSlidersHelper(truckImportSlider,
                    truckExportSlider, Option<BufferSlider>.None, Option<BufferSlider>.None,
                    hasProduct: true, cap, importUntil, exportFrom, Percent.Zero, Percent.Zero);
            });
        buffer.SetupPendingBarsUpdater(vehicleBuffersRegistry, () => Entity.FuelBuffer.Product,
            () => Entity, () => Entity.FuelBuffer.Capacity);
    }

    /// <summary>The row-th DISTINCT product stored across the terminal's modules (in slot
    /// order), or null. The shipping rows are per product, not per module slot.</summary>
    private ProductProto productForRow(int row)
    {
        if (Entity == null)
        {
            return null;
        }
        int seen = 0;
        for (int i = 0; i < Entity.Modules.Length; i++)
        {
            ProductProto product = Entity.Modules[i].ValueOrNull?.StoredProduct.ValueOrNull;
            if (product == null || indexOfProduct(product) < i)
            {
                continue; // Empty, or already listed for an earlier slot.
            }
            if (seen == row)
            {
                return product;
            }
            seen++;
        }
        return null;
    }

    /// <summary>Index of the first module slot storing the product, or int.MaxValue.</summary>
    private int indexOfProduct(ProductProto product)
    {
        for (int i = 0; i < Entity.Modules.Length; i++)
        {
            if (Entity.Modules[i].ValueOrNull?.StoredProduct.ValueOrNull == product)
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    private CargoDepotModule firstModuleOf(ProductProto product)
    {
        int index = indexOfProduct(product);
        return index == int.MaxValue ? null : Entity.Modules[index].ValueOrNull;
    }

    /// <summary>Whether every module storing the product is set to export.</summary>
    private bool allModulesExport(ProductProto product)
    {
        bool any = false;
        foreach (Mafi.Option<CargoDepotModule> slot in Entity.Modules)
        {
            CargoDepotModule module = slot.ValueOrNull;
            if (module != null && module.StoredProduct.ValueOrNull == product)
            {
                if (!m_shippingManager.IsExportModule(module))
                {
                    return false;
                }
                any = true;
            }
        }
        return any;
    }

    protected override void OnDeactivated()
    {
        Context.ShipsPathabilityOverlayRenderer.HideOverlay();
        base.OnDeactivated();
    }
}
