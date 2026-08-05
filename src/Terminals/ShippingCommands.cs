using System;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Input;
using Mafi.Serialization;

namespace ShippingPP.Terminals;

/// <summary>
/// Starts (or cancels) ship construction at a local terminal. Sent by the terminal window's
/// build-ship button; routed through the input-command pipeline so it is deterministic and
/// replay/save safe like every vanilla command.
/// </summary>
public class SetShipConstructionCmd : InputCommand
{
    public readonly EntityId TerminalId;

    public readonly bool IsConstructing;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((SetShipConstructionCmd)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((SetShipConstructionCmd)obj).DeserializeData(reader);

    public SetShipConstructionCmd(EntityId terminalId, bool isConstructing)
    {
        TerminalId = terminalId;
        IsConstructing = isConstructing;
    }

    public static void Serialize(SetShipConstructionCmd value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        EntityId.Serialize(TerminalId, writer);
        writer.WriteBool(IsConstructing);
    }

    public new static SetShipConstructionCmd Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out SetShipConstructionCmd obj,
            (Func<BlobReader, Type, SetShipConstructionCmd>)null,
            (Func<BlobReader, string, SetShipConstructionCmd>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "TerminalId", EntityId.Deserialize(reader));
        reader.SetField(this, "IsConstructing", reader.ReadBool());
    }
}

/// <summary>Sets a terminal module's direction: export ("offer") or import ("request").</summary>
public class SetModuleDirectionCmd : InputCommand
{
    public readonly EntityId ModuleId;

    public readonly bool IsExport;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((SetModuleDirectionCmd)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((SetModuleDirectionCmd)obj).DeserializeData(reader);

    public SetModuleDirectionCmd(EntityId moduleId, bool isExport)
    {
        ModuleId = moduleId;
        IsExport = isExport;
    }

    public static void Serialize(SetModuleDirectionCmd value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        EntityId.Serialize(ModuleId, writer);
        writer.WriteBool(IsExport);
    }

    public new static SetModuleDirectionCmd Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out SetModuleDirectionCmd obj,
            (Func<BlobReader, Type, SetModuleDirectionCmd>)null,
            (Func<BlobReader, string, SetModuleDirectionCmd>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "ModuleId", EntityId.Deserialize(reader));
        reader.SetField(this, "IsExport", reader.ReadBool());
    }
}

/// <summary>Sets a terminal module's network threshold (percent; 100 = always active).</summary>
public class SetModuleThresholdCmd : InputCommand
{
    public readonly EntityId ModuleId;

    public readonly int Percent;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((SetModuleThresholdCmd)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((SetModuleThresholdCmd)obj).DeserializeData(reader);

    public SetModuleThresholdCmd(EntityId moduleId, int percent)
    {
        ModuleId = moduleId;
        Percent = percent;
    }

    public static void Serialize(SetModuleThresholdCmd value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        EntityId.Serialize(ModuleId, writer);
        writer.WriteInt(Percent);
    }

    public new static SetModuleThresholdCmd Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out SetModuleThresholdCmd obj,
            (Func<BlobReader, Type, SetModuleThresholdCmd>)null,
            (Func<BlobReader, string, SetModuleThresholdCmd>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "ModuleId", EntityId.Deserialize(reader));
        reader.SetField(this, "Percent", reader.ReadInt());
    }
}

/// <summary>Processes the mod's input commands (registered as all interfaces in mod DI).</summary>
internal class ShippingCommandsProcessor
    : ICommandProcessor<SetShipConstructionCmd>, IAction<SetShipConstructionCmd>,
      ICommandProcessor<SetModuleDirectionCmd>, IAction<SetModuleDirectionCmd>,
      ICommandProcessor<SetModuleThresholdCmd>, IAction<SetModuleThresholdCmd>
{
    private readonly EntitiesManager m_entitiesManager;
    private readonly ShippingManager m_shippingManager;

    public ShippingCommandsProcessor(EntitiesManager entitiesManager,
        ShippingManager shippingManager)
    {
        m_entitiesManager = entitiesManager;
        m_shippingManager = shippingManager;
    }

    void IAction<SetShipConstructionCmd>.Invoke(SetShipConstructionCmd cmd)
    {
        if (!m_entitiesManager.TryGetEntity(cmd.TerminalId, out LocalTerminal terminal))
        {
            cmd.SetResultError($"Failed to get local terminal with ID {cmd.TerminalId}.");
            return;
        }
        if (cmd.IsConstructing)
        {
            string error = m_shippingManager.StartShipConstruction(terminal);
            if (error != null)
            {
                cmd.SetResultError(error);
                return;
            }
        }
        else
        {
            m_shippingManager.CancelShipConstruction(terminal);
        }
        cmd.SetResultSuccess();
    }

    void IAction<SetModuleDirectionCmd>.Invoke(SetModuleDirectionCmd cmd)
    {
        if (!m_entitiesManager.TryGetEntity(cmd.ModuleId,
            out Mafi.Core.Buildings.Cargo.Modules.CargoDepotModule module)
            || !(module.Depot.ValueOrNull is LocalTerminal))
        {
            cmd.SetResultError($"Failed to get terminal module with ID {cmd.ModuleId}.");
            return;
        }
        m_shippingManager.SetModuleExport(module, cmd.IsExport);
        cmd.SetResultSuccess();
    }

    void IAction<SetModuleThresholdCmd>.Invoke(SetModuleThresholdCmd cmd)
    {
        if (!m_entitiesManager.TryGetEntity(cmd.ModuleId,
            out Mafi.Core.Buildings.Cargo.Modules.CargoDepotModule module)
            || !(module.Depot.ValueOrNull is LocalTerminal))
        {
            cmd.SetResultError($"Failed to get terminal module with ID {cmd.ModuleId}.");
            return;
        }
        m_shippingManager.SetModuleThreshold(module, cmd.Percent);
        cmd.SetResultSuccess();
    }
}
