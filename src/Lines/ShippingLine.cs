using System;
using Mafi.Collections;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Entities.Static;
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
    private const int SAVE_VERSION = 2;

    public int Id { get; private set; }

    public string Name { get; set; }

    private Lyst<StaticEntity> m_stops;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((ShippingLine)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((ShippingLine)obj).DeserializeData(reader);

    public ShippingLine(int id)
    {
        Id = id;
        Name = $"Line {id}";
        m_stops = new Lyst<StaticEntity>();
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
