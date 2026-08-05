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
            "Assets/Unity/UserInterface/General/Build.svg", "Build ship".AsLoc())
            .NoShrink().AlignSelfCenter()
            .OnClick((System.Action)delegate
            {
                ScheduleCommand(new SetShipConstructionCmd(Entity.Id, isConstructing: true));
            }, allowKeyPresses: false);
        string costText = "";
        foreach (ProductQuantity pq in ShippingManager.ShipBuildCost.Products)
        {
            costText += (costText.Length == 0 ? "" : ", ")
                + $"{pq.Quantity.Value}x {pq.Product.Strings.Name.TranslatedString}";
        }
        buildBtn.Tooltip(("Builds this terminal's cargo ship on site: the construction materials "
            + "are requested from truck logistics, and the ship enters service at this terminal "
            + "once everything is delivered."
            + (costText.Length == 0 ? "" : $" Requires: {costText}.")).AsLoc());

        var constrUi = new ConstructionUi();
        PanelFooterRow constrUiPanel;
        AddPanelWithHeader(new Row(4.pt())
        {
            buildBtn
        }).Title("Cargo ship".AsLoc(), ("The terminal's own ship, built on site from delivered "
            + "materials. It serves this terminal only.").AsLoc());
        AddPanel(delegate(Column c)
        {
            c.Gap(2.pt());
        }, constrUiPanel = new PanelFooterRow { constrUi.AlignSelfStretch() });

        constrUi.AddCancelBtn(delegate
        {
            ScheduleCommand(new SetShipConstructionCmd(Entity.Id, isConstructing: false));
        }, () => EntityValidationResult.Success);

        this.Observe(() => m_shippingManager.IsBuildingShip(Entity))
            .Observe(() => Entity.CargoShip.HasValue)
            .Do(delegate(bool isBuilding, bool hasShip)
            {
                buildBtn.Visible(!isBuilding && !hasShip);
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
                    constrUi.As(percent, "Building ship".AsLoc(), DisplayState.Positive);
                }
                constrUi.SetProgress(percent, isDeconstruction: false);
            });

        // --- Shipping modules: offer/request direction per module slot. ---
        // Rows for up to 8 slots (the largest depot); rows without a module/product stay hidden.
        var slotsColumn = new Column(2.pt());
        for (int slotIndex = 0; slotIndex < 8; slotIndex++)
        {
            int i = slotIndex;
            var slotIcon = new Icon().Large().MarginTop(2.pt());
            var slotToggle = new Mafi.Unity.UiToolkit.Library.Toggle(standalone: true)
                .Label("Offer (export)".AsLoc())
                .Tooltip(("On: this module OFFERS its product to the shipping network — trucks "
                    + "fill it from your factory and docked ships are loaded from it. Off: this "
                    + "module REQUESTS its product — docked ships are unloaded into it and "
                    + "trucks distribute the goods to your factory.").AsLoc());
            slotToggle.OnValueChanged(delegate
            {
                CargoDepotModule module = moduleAt(i);
                if (module != null)
                {
                    ScheduleCommand(new SetModuleDirectionCmd(module.Id,
                        !m_shippingManager.IsExportModule(module)));
                }
            });
            slotToggle.ObserveValue(delegate
            {
                CargoDepotModule module = moduleAt(i);
                return module != null && m_shippingManager.IsExportModule(module);
            });
            var thresholdDropdown = new Dropdown<int>(
                (int opt, int idx, bool inDropdown) => new Label($"{opt} %".AsLoc()))
                .SetOptions(new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 });
            thresholdDropdown.Tooltip(("Network threshold: an import module requests cargo only "
                + "while filled below this, an export module offers only while filled above "
                + "(100 % minus this). 100 % = always active.").AsLoc());
            thresholdDropdown.OnValueChanged(delegate(int value, int _)
            {
                CargoDepotModule module = moduleAt(i);
                if (module != null && m_shippingManager.GetModuleThreshold(module) != value)
                {
                    ScheduleCommand(new SetModuleThresholdCmd(module.Id, value));
                }
            });
            thresholdDropdown.ObserveValueDropdown(delegate
            {
                CargoDepotModule module = moduleAt(i);
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
                CargoDepotModule module = moduleAt(i);
                return module?.StoredProduct.ValueOrNull;
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
        AddPanelWithHeader(slotsColumn).Title("Shipping".AsLoc(),
            ("Direction of each terminal module. Assign a product in the module's own window "
            + "first; any product of the module's type can be shipped.").AsLoc());

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

    private CargoDepotModule moduleAt(int index)
    {
        if (Entity == null || index >= Entity.Modules.Length)
        {
            return null;
        }
        return Entity.Modules[index].ValueOrNull;
    }

    protected override void OnDeactivated()
    {
        Context.ShipsPathabilityOverlayRenderer.HideOverlay();
        base.OnDeactivated();
    }
}
