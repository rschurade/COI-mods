using System;
using System.Reflection;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Prototypes;
using Mafi.Core.Research;
using Mafi.Core.UnlockingTree;

namespace ShippingPP;

/// <summary>Shared helpers for registering protos cloned from vanilla donors.</summary>
internal static class ProtoUtils
{
    /// <summary>
    /// Adds the proto. If a research node is given, the proto is locked on init and unlocked by
    /// that node (hidden in the research UI to avoid duplicate icons). With no node it is left
    /// unlocked so it never becomes permanently unbuildable.
    /// </summary>
    internal static Proto AddGated(ProtosDb db, Proto proto, ResearchNodeProto unlockedBy)
    {
        Proto added = db.Add(proto, lockOnInit: unlockedBy != null);
        if (unlockedBy != null)
        {
            unlockedBy.AddProtoToUnlock((IProtoWithIcon)added, hideInUi: true);
            Log.Info($"Shipping++: '{proto.Id}' unlocks with research '{unlockedBy.Id}'.");
        }
        else
        {
            Log.Warning($"Shipping++: '{proto.Id}' has no unlocking research; left always-available.");
        }
        return added;
    }

    /// <summary>The research node that unlocks the given proto, or null.</summary>
    internal static ResearchNodeProto FindUnlockingNode(ProtosDb db, IProto target)
    {
        foreach (ResearchNodeProto node in db.All<ResearchNodeProto>())
        {
            foreach (IProto unlocked in ProtoUnlock.GetUnlockedProtos(node.Units.AsEnumerable()))
            {
                if (unlocked == target)
                {
                    return node;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The source proto's generated icon path, reconstructed explicitly. <c>v.Graphics.IconPath</c>
    /// is still null at mod registration time (vanilla protos are not initialized yet), so the
    /// path the initializer WOULD produce is rebuilt here.
    /// </summary>
    internal static string VanillaIconPath(StaticEntityProto v)
    {
        return Proto.Gfx.GetGeneratedIconPathRoot(v) + "/LayoutEntity/" + v.Id + ".png";
    }

    /// <summary>
    /// Shallow-clones a proto Gfx, retargets its toolbar categories, points it at the source
    /// proto's icon, and marks the icon custom so the proto's Initialize won't overwrite it with a
    /// (missing) generated path for the new id. The vanilla proto is untouched (separate Gfx).
    /// </summary>
    internal static object CloneGfxWithCategory(object vanillaGfx, string iconPath,
        ImmutableArray<ToolbarEntryData> categoryArray)
    {
        object clone = typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(vanillaGfx, null);
        Type t = clone.GetType();
        SetField(t, clone, "<Categories>k__BackingField", categoryArray);
        SetField(t, clone, "<IconPath>k__BackingField", iconPath);
        if (!SetField(t, clone, "IconIsCustom", true))
        {
            Log.Warning("Shipping++: Gfx IconIsCustom field not found; icons may be missing.");
        }
        return clone;
    }

    internal static object GetField(Type type, object target, string name)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                return f.GetValue(target);
            }
        }
        return null;
    }

    internal static bool SetField(Type type, object target, string name, object value)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                f.SetValue(target, value);
                return true;
            }
        }
        return false;
    }
}
