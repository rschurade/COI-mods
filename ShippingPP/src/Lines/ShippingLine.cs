using System;
using Mafi.Collections;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Entities.Static;
using Mafi.Core.Trains;
using Mafi.Serialization;

namespace ShippingPP.Lines;

/// <summary>
/// A shipping line: an ordered, cyclic list of stops. A stop is either a local terminal (the
/// ship docks and exchanges cargo) or a navigation buoy (the ship sails past it — a waypoint).
/// Ships assigned to a line visit the stops in order and ignore the automatic network
/// dispatcher — the line is explicit player intent, so thresholds and the min-load rule do not
/// apply.
/// </summary>
public sealed class ShippingLine
{
    private const int SAVE_VERSION = 6;

    public int Id { get; private set; }

    public string Name { get; set; }

    /// <summary>The line's display color — the vanilla train-line color type, so the vanilla
    /// palette and color UI components apply as-is.</summary>
    public TrainLineColor Color { get; set; }

    /// <summary>Whether the line's ships are painted in the line color (the ship equivalent of
    /// the vanilla "Apply line color to train cars" line setting).</summary>
    public bool ApplyColorToShips { get; set; }

    /// <summary>The stops, each carrying its own departure rule. One list, so reordering and
    /// removal move a rule with its stop automatically and a rule can never end up attached to
    /// the wrong stop.</summary>
    private Lyst<LineStop> m_stops;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((ShippingLine)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((ShippingLine)obj).DeserializeData(reader);

    public ShippingLine(int id)
    {
        Id = id;
        // The vanilla default train line name ("Line {0}"), so a new line is named in the
        // player's language. Renaming replaces it; the name is stored as plain text from then
        // on (as in vanilla, where a renamed line also keeps its literal name).
        Name = Mafi.Core.Tr.Train_Line.Format(id.ToString()).Value;
        Color = DefaultColorFor(id);
        ApplyColorToShips = true;
        m_stops = new Lyst<LineStop>();
    }

    /// <summary>New lines cycle through the vanilla train-line palette by id.</summary>
    public static TrainLineColor DefaultColorFor(int id)
    {
        return TrainLine.COLOR_PALETTE[Math.Abs(id - 1) % TrainLine.COLOR_PALETTE.Length];
    }

    public int StopCount => m_stops.Count;

    public StaticEntity StopAtOrNull(int index)
    {
        return index >= 0 && index < m_stops.Count ? m_stops[index].Entity : null;
    }

    /// <summary>The stop's departure rule (see <see cref="StopRule"/>), or the default
    /// "leave when the transfer finishes" for an out-of-range index.</summary>
    public StopRule RuleAt(int index)
    {
        return index >= 0 && index < m_stops.Count ? m_stops[index].Rule : StopRule.Default;
    }

    public bool SetRuleAt(int index, StopRule rule)
    {
        if (index < 0 || index >= m_stops.Count)
        {
            return false;
        }
        m_stops[index].Rule = rule;
        return true;
    }

    public bool ContainsStop(StaticEntity stop)
    {
        for (int i = 0; i < m_stops.Count; i++)
        {
            if (m_stops[i].Entity == stop)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A line needs at least two live TERMINAL stops for ships to cycle (buoys alone
    /// are not a route).</summary>
    public bool HasUsableStops
    {
        get
        {
            int liveTerminals = 0;
            foreach (LineStop stop in m_stops)
            {
                if (!stop.Entity.IsDestroyed && stop.Entity is CargoDepot)
                {
                    liveTerminals++;
                }
            }
            return liveTerminals >= 2;
        }
    }

    /// <summary>Appends a stop (refused when identical to the current last stop).</summary>
    public bool AddStop(StaticEntity stop)
    {
        if (m_stops.IsNotEmpty && m_stops.Last.Entity == stop)
        {
            return false;
        }
        m_stops.Add(new LineStop(stop, StopRule.Default));
        return true;
    }

    /// <summary>Moves the stop from one position to another (drag-reorder semantics: remove
    /// at <paramref name="from"/>, re-insert at <paramref name="to"/>).</summary>
    public bool MoveStopTo(int from, int to)
    {
        if (from < 0 || to < 0 || from >= m_stops.Count || to >= m_stops.Count || from == to)
        {
            return false;
        }
        LineStop stop = m_stops[from];
        m_stops.RemoveAt(from);
        m_stops.Insert(to, stop);
        return true;
    }

    /// <summary>Removes the stop at the index, if it is the given entity (guards against the
    /// list having shifted since the UI was built).</summary>
    public bool RemoveStopAt(int index, StaticEntity expected)
    {
        if (index >= 0 && index < m_stops.Count && m_stops[index].Entity == expected)
        {
            m_stops.RemoveAt(index);
            return true;
        }
        return false;
    }

    /// <summary>Removes the last occurrence of the stop from the list.</summary>
    public bool RemoveStop(StaticEntity stop)
    {
        for (int i = m_stops.Count - 1; i >= 0; i--)
        {
            if (m_stops[i].Entity == stop)
            {
                m_stops.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Drops destroyed stops from the list.</summary>
    public void PruneDestroyedStops()
    {
        for (int i = m_stops.Count - 1; i >= 0; i--)
        {
            if (m_stops[i].Entity.IsDestroyed)
            {
                m_stops.RemoveAt(i);
            }
        }
    }

    public static void Serialize(ShippingLine value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    private void SerializeData(BlobWriter writer)
    {
        writer.WriteInt(SAVE_VERSION);
        writer.WriteInt(Id);
        writer.WriteString(Name);
        TrainLineColor.Serialize(Color, writer);
        writer.WriteBool(ApplyColorToShips);
        writer.WriteInt(m_stops.Count);
        foreach (LineStop stop in m_stops)
        {
            writer.WriteGeneric(stop.Entity);
            writer.WriteInt((int)stop.Rule.Mode);
            writer.WriteInt(stop.Rule.Percent);
            writer.WriteInt(stop.Rule.TimeoutSec);
        }
    }

    public static ShippingLine Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out ShippingLine obj,
            (Func<BlobReader, Type, ShippingLine>)null,
            (Func<BlobReader, string, ShippingLine>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    private void DeserializeData(BlobReader reader)
    {
        int version = reader.ReadInt();
        Id = reader.ReadInt();
        Name = reader.ReadString();
        Color = version >= 3 ? TrainLineColor.Deserialize(reader) : DefaultColorFor(Id);
        // Explicit for older saves too: a blob-restored object never ran its constructor, so
        // the property would otherwise default to false and silently unpaint existing fleets.
        ApplyColorToShips = version < 6 || reader.ReadBool();
        m_stops = new Lyst<LineStop>();
        if (version >= 5)
        {
            int count = reader.ReadInt();
            for (int i = 0; i < count; i++)
            {
                StaticEntity entity = reader.ReadGenericAs<StaticEntity>();
                var rule = new StopRule((StopWait)reader.ReadInt(),
                    reader.ReadInt(), reader.ReadInt());
                m_stops.Add(new LineStop(entity, rule));
            }
            return;
        }
        if (version >= 2)
        {
            // v2..v4 stored the stops in one list and (from v4) the rules in a second,
            // index-parallel one. Both are zipped back together in initStopsAfterLoad -- NOT
            // here: Lyst<T>.Deserialize hands back an EMPTY list and enqueues the fill for
            // later, so reading Count now yields 0 and would silently drop every stop.
            m_loadedStops = Lyst<StaticEntity>.Deserialize(reader);
            m_loadedRules = new Lyst<StopRule>();
            if (version >= 4)
            {
                int ruleCount = reader.ReadInt();
                for (int i = 0; i < ruleCount; i++)
                {
                    m_loadedRules.Add(new StopRule((StopWait)reader.ReadInt(),
                        reader.ReadInt(), reader.ReadInt()));
                }
            }
            reader.RegisterInitAfterLoad(this, nameof(initStopsAfterLoad), InitPriority.Normal);
            return;
        }
        // v1 stored the stops as Lyst<CargoDepot> (different wire format), and is deferred for
        // the same reason.
        m_loadedLegacyStops = Lyst<CargoDepot>.Deserialize(reader);
        reader.RegisterInitAfterLoad(this, nameof(initStopsAfterLoad), InitPriority.Normal);
    }

    /// <summary>Stops loaded from a pre-v5 save, parked until their list has actually been
    /// filled (see <see cref="DeserializeData"/>).</summary>
    private Lyst<StaticEntity> m_loadedStops;
    private Lyst<StopRule> m_loadedRules;
    private Lyst<CargoDepot> m_loadedLegacyStops;

    /// <summary>Rebuilds the stop list from a pre-v5 save once the parked lists are populated.
    /// </summary>
    private void initStopsAfterLoad()
    {
        if (m_loadedStops != null)
        {
            for (int i = 0; i < m_loadedStops.Count; i++)
            {
                m_stops.Add(new LineStop(m_loadedStops[i],
                    i < m_loadedRules.Count ? m_loadedRules[i] : StopRule.Default));
            }
        }
        if (m_loadedLegacyStops != null)
        {
            foreach (CargoDepot stop in m_loadedLegacyStops)
            {
                m_stops.Add(new LineStop(stop, StopRule.Default));
            }
        }
        m_loadedStops = null;
        m_loadedRules = null;
        m_loadedLegacyStops = null;
    }
}
