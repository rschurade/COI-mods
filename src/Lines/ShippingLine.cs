using System;
using Mafi.Collections;
using Mafi.Core.Buildings.Cargo;
using Mafi.Serialization;

namespace ShippingPP.Lines;

/// <summary>
/// A shipping line: an ordered, cyclic list of terminal stops. Ships assigned to a line visit
/// the stops in order (transfers at each stop are the usual direction-driven crane exchange) and
/// ignore the automatic network dispatcher — the line is explicit player intent, so thresholds
/// and the min-load rule do not apply.
/// </summary>
public sealed class ShippingLine
{
    private const int SAVE_VERSION = 1;

    public int Id { get; private set; }

    public string Name { get; set; }

    private readonly Lyst<CargoDepot> m_stops;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((ShippingLine)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((ShippingLine)obj).DeserializeData(reader);

    public ShippingLine(int id)
    {
        Id = id;
        Name = $"Line {id}";
        m_stops = new Lyst<CargoDepot>();
    }

    public int StopCount => m_stops.Count;

    public CargoDepot StopAtOrNull(int index)
    {
        return index >= 0 && index < m_stops.Count ? m_stops[index] : null;
    }

    public bool ContainsStop(CargoDepot terminal)
    {
        return m_stops.Contains(terminal);
    }

    /// <summary>A line needs at least two live stops for ships to cycle.</summary>
    public bool HasUsableStops
    {
        get
        {
            int live = 0;
            foreach (CargoDepot stop in m_stops)
            {
                if (!stop.IsDestroyed)
                {
                    live++;
                }
            }
            return live >= 2;
        }
    }

    /// <summary>Appends a stop (refused when identical to the current last stop).</summary>
    public bool AddStop(CargoDepot terminal)
    {
        if (m_stops.IsNotEmpty && m_stops.Last == terminal)
        {
            return false;
        }
        m_stops.Add(terminal);
        return true;
    }

    /// <summary>Removes the last occurrence of the terminal from the stop list.</summary>
    public bool RemoveStop(CargoDepot terminal)
    {
        for (int i = m_stops.Count - 1; i >= 0; i--)
        {
            if (m_stops[i] == terminal)
            {
                m_stops.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Drops destroyed terminals from the stop list.</summary>
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
        Lyst<CargoDepot>.Serialize(m_stops, writer);
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
        reader.ReadInt();
        Id = reader.ReadInt();
        Name = reader.ReadString();
        reader.SetField(this, "m_stops", Lyst<CargoDepot>.Deserialize(reader));
    }
}
