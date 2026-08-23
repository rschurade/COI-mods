using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Library.Inspectors;

namespace ElevationPP.Platforms;

/// <summary>
/// The concrete platform's window. Picked up automatically for <see cref="ConcretePlatform"/> entities
/// (the inspector manager keys on the most-derived entity type). The base inspector provides
/// everything the platform needs: title (renameable, the base entity carries a custom title), the
/// construction/deconstruction controls and the standard header buttons.
/// </summary>
public class ConcretePlatformInspector : BaseInspector<ConcretePlatform>
{
    public ConcretePlatformInspector(UiContext context)
        : base(context)
    {
    }
}
