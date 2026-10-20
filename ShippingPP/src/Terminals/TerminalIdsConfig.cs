using System;
using System.Collections.Generic;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Mods;
using Mafi.Serialization;

namespace ShippingPP.Terminals;

/// <summary>
/// Which cargo depot each local terminal proto id stands for, stored in the save.
///
/// Terminal ids are saved with every terminal entity, so an id must keep meaning the same donor
/// depot for the lifetime of a save, whatever depot mods come and go. Up to 0.6.4 the ids
/// T1..Tn were handed out by sorting ALL depots by slot count, so installing or updating a mod
/// that adds depots shifted them and broke the save (a terminal loaded with another depot's
/// layout). This config pins the mapping: it is a serializable global-dependency config, so the
/// game restores it from the save BEFORE proto registration and saves it back with the game;
/// <see cref="LocalTerminalData"/> registers exactly these ids and only appends new donors.
/// </summary>
[GlobalDependency(RegistrationMode.AsEverything)]
public sealed class TerminalIdsConfig : IModConfig
{
    private const int SAVE_VERSION = 1;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((TerminalIdsConfig)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((TerminalIdsConfig)obj).DeserializeData(reader);

    /// <summary>Terminal proto id -> donor depot proto id, in registration order. Entries whose
    /// donor is currently missing are kept, so the id comes back if the depot mod does.</summary>
    public Lyst<KeyValuePair<string, string>> Entries { get; private set; }

    /// <summary>False for a new game and for saves from before this config existed (0.6.4 and
    /// older); the ids are then reconstructed the way those versions assigned them.</summary>
    public bool IsFromSave { get; private set; }

    public TerminalIdsConfig()
    {
        Entries = new Lyst<KeyValuePair<string, string>>();
    }

    public static void Serialize(TerminalIdsConfig value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    private void SerializeData(BlobWriter writer)
    {
        writer.WriteInt(SAVE_VERSION);
        writer.WriteInt(Entries.Count);
        foreach (KeyValuePair<string, string> entry in Entries)
        {
            writer.WriteString(entry.Key);
            writer.WriteString(entry.Value);
        }
    }

    public static TerminalIdsConfig Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out TerminalIdsConfig obj,
            (Func<BlobReader, Type, TerminalIdsConfig>)null,
            (Func<BlobReader, string, TerminalIdsConfig>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    private void DeserializeData(BlobReader reader)
    {
        reader.ReadInt(); // SAVE_VERSION
        int count = reader.ReadInt();
        Entries = new Lyst<KeyValuePair<string, string>>(count);
        for (int i = 0; i < count; i++)
        {
            string terminalId = reader.ReadString();
            string donorId = reader.ReadString();
            Entries.Add(Make.Kvp(terminalId, donorId));
        }
        IsFromSave = true;
    }
}
