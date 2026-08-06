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
    private const int SAVE_VERSION = 3;

    public int Id { get; private set; }

    public string Name { get; set; }

    /// <summary>The line's display color — the vanilla train-line color type, so the vanilla
    /// palette and color UI components apply as-is.</summary>
    public TrainLineColor Color { get; set; }

    private Lyst<StaticEntity> m_stops;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((ShippingLine)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((ShippingLine)obj).DeserializeData(reader);

    public ShippingLine(int id)
    {
        Id = id;
        Name = $"Line {id}";
        Color = DefaultColorFor(id);
        m_stops = new Lyst<StaticEntity>();
    }

    /// <summary>New lines cycle through the vanilla train-line palette by id.</summary>
    public static TrainLineColor DefaultColorFor(int id)
    {
        return TrainLine.COLOR_PALETTE[Math.Abs(id - 1) % TrainLine.COLOR_PALETTE.Length];
    }

    public int StopCount => m_stops.Count;

    public StaticEntity StopAtOrNull(int index)
    {
        return index >= 0 && index < m_stops.Count ? m_stops[index] : null;
    }

    public bool ContainsStop(StaticEntity stop)
    {
        return m_stops.Contains(stop);
    }

    /// <summary>A line needs at least two live TERMINAL stops for ships to cycle (buoys alone
    /// are not a route).</summary>
    public bool HasUsableStops
    {
        get
        {
            int liveTerminals = 0;
            foreach (StaticEntity stop in m_stops)
            {
                if (!stop.IsDestroyed && stop is CargoDepot)
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
        if (m_stops.IsNotEmpty && m_stops.Last == stop)
        {
            return false;
        }
        m_stops.Add(stop);
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
        StaticEntity stop = m_stops[from];
        m_stops.RemoveAt(from);
        m_stops.Insert(to, stop);
        return true;
    }

    /// <summary>Removes the stop at the index, if it is the given entity (guards against the
    /// list having shifted since the UI was built).</summary>
    public bool RemoveStopAt(int index, StaticEntity expected)
    {
        if (index >= 0 && index < m_stops.Count && m_stops[index] == expected)
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
            if (m_stops[i] == stop)
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
            if (m_stops[i].IsDestroyed)
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
        Lyst<StaticEntity>.Serialize(m_stops, writer);
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
        if (version >= 2)
        {
            m_stops = Lyst<StaticEntity>.Deserialize(reader);
            return;
        }
        // v1 stored the stops as Lyst<CargoDepot> (different wire format).
        Lyst<CargoDepot> old = Lyst<CargoDepot>.Deserialize(reader);
        m_stops = new Lyst<StaticEntity>();
        foreach (CargoDepot stop in old)
        {
            m_stops.Add(stop);
        }
    }
}
