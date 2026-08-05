using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Library.Inspectors;

namespace ShippingPP.Lines;

/// <summary>
/// The navigation buoy's window. Picked up automatically for <see cref="NavBuoy"/> entities
/// (most-derived entity type wins over the vanilla barrier inspector). The base inspector
/// enables title renaming on its own because <see cref="NavBuoy"/> carries a custom title —
/// same interaction as renaming a vanilla train station — and that name is what the shipping
/// lines manager shows for the buoy's stops.
/// </summary>
public class NavBuoyInspector : BaseInspector<NavBuoy>
{
    public NavBuoyInspector(UiContext context)
        : base(context)
    {
    }
}
