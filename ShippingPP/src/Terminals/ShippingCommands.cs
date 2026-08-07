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

/// <summary>Re-homes a local ship to another terminal (the ship window's home-port picker).</summary>
public class SetShipHomeCmd : InputCommand
{
    public readonly EntityId ShipId;

    public readonly EntityId TerminalId;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((SetShipHomeCmd)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((SetShipHomeCmd)obj).DeserializeData(reader);

    public SetShipHomeCmd(EntityId shipId, EntityId terminalId)
    {
        ShipId = shipId;
        TerminalId = terminalId;
    }

    public static void Serialize(SetShipHomeCmd value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        EntityId.Serialize(ShipId, writer);
        EntityId.Serialize(TerminalId, writer);
    }

    public new static SetShipHomeCmd Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out SetShipHomeCmd obj,
            (Func<BlobReader, Type, SetShipHomeCmd>)null,
            (Func<BlobReader, string, SetShipHomeCmd>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "ShipId", EntityId.Deserialize(reader));
        reader.SetField(this, "TerminalId", EntityId.Deserialize(reader));
    }
}

/// <summary>
/// Sells a local cargo ship: the refund is credited to its home terminal at once and the ship
/// sails off the map, where it is removed. Sent by the ship window's sell button.
/// </summary>
public class SellShipCmd : InputCommand
{
    public readonly EntityId ShipId;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((SellShipCmd)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((SellShipCmd)obj).DeserializeData(reader);

    public SellShipCmd(EntityId shipId)
    {
        ShipId = shipId;
    }

    public static void Serialize(SellShipCmd value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        EntityId.Serialize(ShipId, writer);
    }

    public new static SellShipCmd Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out SellShipCmd obj,
            (Func<BlobReader, Type, SellShipCmd>)null,
            (Func<BlobReader, string, SellShipCmd>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "ShipId", EntityId.Deserialize(reader));
    }
}

/// <summary>Sets a line stop's departure rule (see <see cref="ShippingPP.Lines.StopRule"/>).
/// </summary>
public class SetStopRuleCmd : InputCommand
{
    public readonly int LineId;

    public readonly int StopIndex;

    /// <summary><see cref="ShippingPP.Lines.StopWait"/> as an int.</summary>
    public readonly int Mode;

    public readonly int Percent;

    public readonly int TimeoutSec;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((SetStopRuleCmd)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((SetStopRuleCmd)obj).DeserializeData(reader);

    public SetStopRuleCmd(int lineId, int stopIndex, int mode, int percent, int timeoutSec)
    {
        LineId = lineId;
        StopIndex = stopIndex;
        Mode = mode;
        Percent = percent;
        TimeoutSec = timeoutSec;
    }

    public static void Serialize(SetStopRuleCmd value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        writer.WriteInt(LineId);
        writer.WriteInt(StopIndex);
        writer.WriteInt(Mode);
        writer.WriteInt(Percent);
        writer.WriteInt(TimeoutSec);
    }

    public new static SetStopRuleCmd Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out SetStopRuleCmd obj,
            (Func<BlobReader, Type, SetStopRuleCmd>)null,
            (Func<BlobReader, string, SetStopRuleCmd>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "LineId", reader.ReadInt());
        reader.SetField(this, "StopIndex", reader.ReadInt());
        reader.SetField(this, "Mode", reader.ReadInt());
        reader.SetField(this, "Percent", reader.ReadInt());
        reader.SetField(this, "TimeoutSec", reader.ReadInt());
    }
}

/// <summary>Edits shipping lines: create/delete lines, add/remove stops, (un)assign ships.</summary>
public class ModifyLineCmd : InputCommand
{
    public const byte ACTION_CREATE = 0;
    public const byte ACTION_DELETE = 1;
    public const byte ACTION_ADD_STOP = 2;
    public const byte ACTION_REMOVE_STOP = 3;
    public const byte ACTION_ASSIGN_SHIP = 4;
    public const byte ACTION_UNASSIGN_SHIP = 5;
    public const byte ACTION_RENAME = 6;
    public const byte ACTION_REORDER_STOP = 7;
    public const byte ACTION_SET_COLOR = 8;

    public readonly byte Action;

    public readonly int LineId;

    /// <summary>Terminal id for stop actions, ship id for assignment actions.</summary>
    public readonly EntityId TargetId;

    /// <summary>New name for the rename action.</summary>
    public readonly string Name;

    /// <summary>Stop index for the remove/reorder actions (reorder: the old index), palette
    /// index for the color action.</summary>
    public readonly int Arg;

    /// <summary>Second argument: the new stop index for the reorder action.</summary>
    public readonly int Arg2;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((ModifyLineCmd)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((ModifyLineCmd)obj).DeserializeData(reader);

    public ModifyLineCmd(byte action, int lineId, EntityId targetId, string name = "",
        int arg = 0, int arg2 = 0)
    {
        Action = action;
        LineId = lineId;
        TargetId = targetId;
        Name = name ?? "";
        Arg = arg;
        Arg2 = arg2;
    }

    public static void Serialize(ModifyLineCmd value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        writer.WriteByte(Action);
        writer.WriteInt(LineId);
        EntityId.Serialize(TargetId, writer);
        writer.WriteString(Name);
        writer.WriteInt(Arg);
        writer.WriteInt(Arg2);
    }

    public new static ModifyLineCmd Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out ModifyLineCmd obj,
            (Func<BlobReader, Type, ModifyLineCmd>)null,
            (Func<BlobReader, string, ModifyLineCmd>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "Action", reader.ReadByte());
        reader.SetField(this, "LineId", reader.ReadInt());
        reader.SetField(this, "TargetId", EntityId.Deserialize(reader));
        reader.SetField(this, "Name", reader.ReadString());
        reader.SetField(this, "Arg", reader.ReadInt());
        reader.SetField(this, "Arg2", reader.ReadInt());
    }
}

/// <summary>Processes the mod's input commands (registered as all interfaces in mod DI).</summary>
internal class ShippingCommandsProcessor
    : ICommandProcessor<SetShipConstructionCmd>, IAction<SetShipConstructionCmd>,
      ICommandProcessor<SetModuleDirectionCmd>, IAction<SetModuleDirectionCmd>,
      ICommandProcessor<SetModuleThresholdCmd>, IAction<SetModuleThresholdCmd>,
      ICommandProcessor<SetShipHomeCmd>, IAction<SetShipHomeCmd>,
      ICommandProcessor<SellShipCmd>, IAction<SellShipCmd>,
      ICommandProcessor<SetStopRuleCmd>, IAction<SetStopRuleCmd>,
      ICommandProcessor<ModifyLineCmd>, IAction<ModifyLineCmd>
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

    void IAction<SetShipHomeCmd>.Invoke(SetShipHomeCmd cmd)
    {
        if (!m_entitiesManager.TryGetEntity(cmd.ShipId,
            out Mafi.Core.Buildings.Cargo.Ships.CargoShipV2 ship)
            || !m_entitiesManager.TryGetEntity(cmd.TerminalId, out LocalTerminal terminal))
        {
            cmd.SetResultError("Ship or terminal not found.");
            return;
        }
        string error = m_shippingManager.SetShipHome(ship, terminal);
        if (error != null)
        {
            cmd.SetResultError(error);
            return;
        }
        cmd.SetResultSuccess();
    }

    void IAction<SellShipCmd>.Invoke(SellShipCmd cmd)
    {
        if (!m_entitiesManager.TryGetEntity(cmd.ShipId,
            out Mafi.Core.Buildings.Cargo.Ships.CargoShipV2 ship))
        {
            cmd.SetResultError($"Failed to get cargo ship with ID {cmd.ShipId}.");
            return;
        }
        string error = m_shippingManager.SellShip(ship);
        if (error != null)
        {
            cmd.SetResultError(error);
            return;
        }
        cmd.SetResultSuccess();
    }

    void IAction<SetStopRuleCmd>.Invoke(SetStopRuleCmd cmd)
    {
        ShippingPP.Lines.ShippingLine line = m_shippingManager.TryGetLine(cmd.LineId);
        if (line == null)
        {
            cmd.SetResultError($"Line {cmd.LineId} not found.");
            return;
        }
        if (!line.SetRuleAt(cmd.StopIndex, new ShippingPP.Lines.StopRule(
            (ShippingPP.Lines.StopWait)cmd.Mode, cmd.Percent, cmd.TimeoutSec)))
        {
            cmd.SetResultError($"Line {cmd.LineId} has no stop {cmd.StopIndex}.");
            return;
        }
        cmd.SetResultSuccess();
    }

    void IAction<ModifyLineCmd>.Invoke(ModifyLineCmd cmd)
    {
        switch (cmd.Action)
        {
            case ModifyLineCmd.ACTION_CREATE:
                if (m_entitiesManager.TryGetEntity(cmd.TargetId, out LocalTerminal first))
                {
                    m_shippingManager.CreateLine(first);
                    cmd.SetResultSuccess();
                    return;
                }
                break;
            case ModifyLineCmd.ACTION_DELETE:
                m_shippingManager.DeleteLine(cmd.LineId);
                cmd.SetResultSuccess();
                return;
            case ModifyLineCmd.ACTION_ADD_STOP:
                if (tryGetLineStop(cmd.TargetId, out Mafi.Core.Entities.Static.StaticEntity stop))
                {
                    m_shippingManager.TryGetLine(cmd.LineId)?.AddStop(stop);
                    cmd.SetResultSuccess();
                    return;
                }
                break;
            case ModifyLineCmd.ACTION_REMOVE_STOP:
                if (tryGetLineStop(cmd.TargetId,
                    out Mafi.Core.Entities.Static.StaticEntity removed))
                {
                    // Prefer removal by row index (stops may repeat on a line); falls back to
                    // by-entity when the list shifted since the UI was built.
                    Lines.ShippingLine line = m_shippingManager.TryGetLine(cmd.LineId);
                    if (line != null && !line.RemoveStopAt(cmd.Arg, removed))
                    {
                        line.RemoveStop(removed);
                    }
                    cmd.SetResultSuccess();
                    return;
                }
                break;
            case ModifyLineCmd.ACTION_ASSIGN_SHIP:
                if (m_entitiesManager.TryGetEntity(cmd.TargetId,
                    out Mafi.Core.Buildings.Cargo.Ships.CargoShipV2 ship))
                {
                    m_shippingManager.SetShipLine(ship, cmd.LineId);
                    cmd.SetResultSuccess();
                    return;
                }
                break;
            case ModifyLineCmd.ACTION_UNASSIGN_SHIP:
                if (m_entitiesManager.TryGetEntity(cmd.TargetId,
                    out Mafi.Core.Buildings.Cargo.Ships.CargoShipV2 unassigned))
                {
                    m_shippingManager.SetShipLine(unassigned, null);
                    cmd.SetResultSuccess();
                    return;
                }
                break;
            case ModifyLineCmd.ACTION_RENAME:
                Lines.ShippingLine renamed = m_shippingManager.TryGetLine(cmd.LineId);
                if (renamed != null && !string.IsNullOrEmpty(cmd.Name))
                {
                    renamed.Name = cmd.Name;
                    cmd.SetResultSuccess();
                    return;
                }
                break;
            case ModifyLineCmd.ACTION_REORDER_STOP:
                if (m_shippingManager.TryGetLine(cmd.LineId)?.MoveStopTo(cmd.Arg, cmd.Arg2)
                    == true)
                {
                    cmd.SetResultSuccess();
                    return;
                }
                break;
            case ModifyLineCmd.ACTION_SET_COLOR:
                Lines.ShippingLine colored = m_shippingManager.TryGetLine(cmd.LineId);
                if (colored != null)
                {
                    var palette = Mafi.Core.Trains.TrainLine.COLOR_PALETTE;
                    colored.Color = palette[Math.Abs(cmd.Arg) % palette.Length];
                    cmd.SetResultSuccess();
                    return;
                }
                break;
        }
        cmd.SetResultError("Failed to modify shipping line.");
    }

    /// <summary>Valid line stops: local terminals (dock + transfer) and navigation buoys.</summary>
    private bool tryGetLineStop(EntityId id, out Mafi.Core.Entities.Static.StaticEntity stop)
    {
        if (m_entitiesManager.TryGetEntity(id, out stop)
            && (stop is LocalTerminal || stop.Prototype is Lines.NavBuoyProto))
        {
            return true;
        }
        stop = null;
        return false;
    }
}
