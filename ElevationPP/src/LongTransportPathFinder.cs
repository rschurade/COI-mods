using System;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Ports.Io;
using Mafi.Core.Terrain;
using Mafi.PathFinding;

namespace ElevationPP;

/// <summary>
/// Lets a single belt/pipe drag be longer than the vanilla limit (configurable via
/// <c>TransportPlacementMaxLength</c>).
///
/// The vanilla <see cref="TransportPathFinder"/> routes inside a fixed 64x64x16 node window with the
/// start pinned at its center, and silently clamps the goal into that window — so one placement can
/// reach only ~31 tiles from where the drag started. The window size is compile-time constant
/// (ushort bit-packing, inlined bounds checks, pre-allocated arrays), so it cannot be patched.
///
/// Instead, this class re-implements <see cref="ITransportPathFinder"/> as a thin router on top of
/// an unmodified vanilla finder (registered via <c>SetPreferredImplementationFor</c>, so every
/// consumer — drag preview, blueprint build action — gets it via DI):
///
///   - A goal within the vanilla window delegates 1:1 to the vanilla finder (identical behaviour).
///   - A farther goal is split into legs along the straight start→goal line, each leg within the
///     window, solved sequentially by the vanilla finder and stitched at the waypoints. Each leg is
///     therefore fully vanilla-validated (collisions, ramps, pillar support, ports), the next leg
///     may not double back into the previous one (its arrival direction's opposite is banned at the
///     joint), and for ramping transports the joints are required to be flat so legs can't meet
///     mid-ramp. If a leg cannot be solved, its waypoint is nudged a few tiles (perpendicular first,
///     then shorter); when no nudge helps, the whole path fails — exactly like a blocked vanilla drag.
///
/// Routing is greedy per leg: an obstacle wider than one window that requires a large global detour
/// can fail where a hypothetical global search would succeed — the fallback is what vanilla players
/// do today: place the run in several shorter drags.
/// </summary>
public sealed class LongTransportPathFinder : ITransportPathFinder
{
    /// <summary>
    /// Max straight-line reach (in tiles, per axis) of one placement, set from the mod config.
    /// At or below the vanilla reach (31) every request is a single vanilla-delegated leg.
    /// </summary>
    public static volatile int MaxSegmentLength = 128;

    // Vanilla window relative to the drag start (64x64x16, start pinned at (32,32,8)).
    private const int VANILLA_REACH_XY = 31;
    private const int VANILLA_REACH_Z_UP = 7;
    private const int VANILLA_REACH_Z_DOWN = 8;
    private const int LEG_LENGTH = 30;

    // Waypoint nudges tried when a middle leg fails: sideways around the obstacle, then shorter.
    private static readonly RelTile2i[] NUDGES =
    {
        new RelTile2i(0, 1), new RelTile2i(0, -1), new RelTile2i(0, 2), new RelTile2i(0, -2),
        new RelTile2i(0, 3), new RelTile2i(0, -3), new RelTile2i(-2, 0), new RelTile2i(-4, 0),
    };

    private readonly TransportPathFinder m_inner;

    private TransportProto m_proto;
    private Tile3i m_start;
    private Tile3i m_goal;
    private TransportPathFinderOptions m_options;
    private Tile3i[] m_bannedTiles;

    private bool m_isMultiLeg;
    private bool m_failed;
    private readonly Lyst<Tile3i> m_waypoints = new Lyst<Tile3i>();  // leg goals; last one == m_goal
    private int m_legIndex;
    private Tile3i m_legStart;
    private int m_nudgeIndex;
    private readonly Lyst<Tile3i> m_pivots = new Lyst<Tile3i>();

    public LongTransportPathFinder(TerrainOccupancyManager occupancyManager, TerrainManager terrainManager,
        IoPortsManager portsManager, ITransportsPredicates transportsPredicates, IPillarsChecker pillarsChecker)
    {
        m_inner = new TransportPathFinder(occupancyManager, terrainManager, portsManager,
            transportsPredicates, pillarsChecker);
    }

    public Tile3i CurrentStart => m_isMultiLeg ? m_start : m_inner.CurrentStart;
    public Tile3i CurrentGoal => m_isMultiLeg ? m_goal : m_inner.CurrentGoal;
    public Tile3i OriginalGoal => m_isMultiLeg ? m_goal : m_inner.OriginalGoal;
    public Option<TransportProto> CurrentTransportProto => m_proto;
    public TransportPathFinderOptions Options => m_options;

    public void InitPathFinding(TransportProto proto, Tile3i start, Tile3i goal,
        TransportPathFinderOptions options, System.Collections.Generic.IEnumerable<Tile3i> bannedTiles = null)
    {
        m_proto = proto;
        m_start = start;
        m_options = options;
        m_bannedTiles = bannedTiles?.ToArray();
        m_goal = clampGoal(start, goal);
        if (options.HasFlags(TransportPathFinderFlags.AllowOnlyStraight))
        {
            // Vanilla (shift held) snaps the goal onto the dominant axis relative to the start and
            // forbids every tile off that axis. Snap the overall goal here so all waypoints are
            // collinear with the start; otherwise each leg would pick its own dominant axis and the
            // legs would zig-zag.
            m_goal = snapToAxis(start, m_goal);
        }
        m_failed = false;

        if (isWithinVanillaWindow(m_start, m_goal))
        {
            m_isMultiLeg = false;
            m_inner.InitPathFinding(proto, start, m_goal, options, m_bannedTiles);
            return;
        }

        m_isMultiLeg = true;
        buildWaypoints();
        m_pivots.Clear();
        m_legIndex = 0;
        m_legStart = m_start;
        m_nudgeIndex = 0;
        initCurrentLeg();
    }

    public PathFinderResult ContinuePathFinding(ref int iterations, out ImmutableArray<Tile3i> outPivots)
    {
        if (!m_isMultiLeg)
        {
            return m_inner.ContinuePathFinding(ref iterations, out outPivots);
        }

        outPivots = ImmutableArray<Tile3i>.Empty;
        if (m_failed)
        {
            return PathFinderResult.PathDoesNotExist;
        }

        while (iterations > 0)
        {
            PathFinderResult result = m_inner.ContinuePathFinding(ref iterations, out ImmutableArray<Tile3i> legPivots);
            if (result == PathFinderResult.StillSearching)
            {
                return PathFinderResult.StillSearching;
            }
            if (result != PathFinderResult.PathFound)
            {
                // Leg is blocked; try a nudged waypoint. The final leg targets the user's own goal
                // and is never nudged.
                if (m_legIndex < m_waypoints.Count - 1 && m_nudgeIndex < NUDGES.Length)
                {
                    if (m_options.HasFlags(TransportPathFinderFlags.AllowOnlyStraight))
                    {
                        // Sideways nudges would leave the forced axis; only shorter legs can help.
                        while (m_nudgeIndex < NUDGES.Length && NUDGES[m_nudgeIndex].Y != 0)
                        {
                            m_nudgeIndex++;
                        }
                        if (m_nudgeIndex >= NUDGES.Length)
                        {
                            m_failed = true;
                            return PathFinderResult.PathDoesNotExist;
                        }
                    }
                    nudgeCurrentWaypoint();
                    initCurrentLeg();
                    continue;
                }
                m_failed = true;
                return PathFinderResult.PathDoesNotExist;
            }

            appendLegPivots(legPivots);
            if (m_legIndex == m_waypoints.Count - 1)
            {
                outPivots = m_pivots.ToImmutableArray();
                return PathFinderResult.PathFound;
            }
            m_legStart = m_waypoints[m_legIndex];
            m_legIndex++;
            m_nudgeIndex = 0;
            initCurrentLeg();
        }
        return PathFinderResult.StillSearching;
    }

    public void SetUndirected()
    {
        m_inner.SetUndirected();
    }

    public void ChangeGoal(Tile3i goal)
    {
        Tile3i clamped = clampGoal(m_start, goal);
        if (!m_isMultiLeg && isWithinVanillaWindow(m_start, clamped))
        {
            m_goal = clamped;
            m_inner.ChangeGoal(clamped);
            return;
        }
        // Leg layout changes — replan from scratch (cost is spread over frames by the caller's
        // iteration budget, same as vanilla's incremental search).
        InitPathFinding(m_proto, m_start, goal, m_options, m_bannedTiles);
    }

    public void GetExploredTiles(Lyst<TransportPfExploredTile> exploredTiles)
    {
        m_inner.GetExploredTiles(exploredTiles);
    }

    private static bool isWithinVanillaWindow(Tile3i start, Tile3i goal)
    {
        RelTile3i d = goal - start;
        return Math.Abs(d.X) <= VANILLA_REACH_XY && Math.Abs(d.Y) <= VANILLA_REACH_XY
            && d.Z <= VANILLA_REACH_Z_UP && d.Z >= -VANILLA_REACH_Z_DOWN;
    }

    private static Tile3i snapToAxis(Tile3i start, Tile3i goal)
    {
        RelTile3i d = goal - start;
        return Math.Abs(d.X) <= Math.Abs(d.Y)
            ? new Tile3i(start.X, goal.Y, goal.Z)
            : new Tile3i(goal.X, start.Y, goal.Z);
    }

    private Tile3i clampGoal(Tile3i start, Tile3i goal)
    {
        int reach = Math.Max(VANILLA_REACH_XY, MaxSegmentLength);
        RelTile3i d = goal - start;
        return new Tile3i(
            start.X + d.X.Clamp(-reach, reach),
            start.Y + d.Y.Clamp(-reach, reach),
            goal.Z);
    }

    /// <summary>
    /// Splits start→goal into legs of at most <see cref="LEG_LENGTH"/> tiles per axis, waypoints on
    /// the straight line with Z interpolated (each leg's Z delta stays within the vanilla window).
    /// </summary>
    private void buildWaypoints()
    {
        m_waypoints.Clear();
        RelTile3i total = m_goal - m_start;
        int maxAxis = Math.Max(Math.Abs(total.X), Math.Abs(total.Y));
        int legCount = Math.Max(1, (maxAxis + LEG_LENGTH - 1) / LEG_LENGTH);
        for (int i = 1; i < legCount; i++)
        {
            m_waypoints.Add(new Tile3i(
                m_start.X + (int)Math.Round(total.X * (double)i / legCount),
                m_start.Y + (int)Math.Round(total.Y * (double)i / legCount),
                m_start.Z + (total.Z * i / legCount).Clamp(-VANILLA_REACH_Z_DOWN * i, VANILLA_REACH_Z_UP * i)));
        }
        m_waypoints.Add(m_goal);
    }

    private void initCurrentLeg()
    {
        bool isFirst = m_legIndex == 0;
        bool isLast = m_legIndex == m_waypoints.Count - 1;

        // Flat/port flags belong to the overall endpoints; middle joints of ramping transports are
        // forced flat so consecutive legs can never meet mid-ramp.
        TransportPathFinderFlags flags = m_options.Flags
            & ~(TransportPathFinderFlags.StartMustBeFlat | TransportPathFinderFlags.GoalMustBeFlat);
        bool ramps = m_proto.ZStepLength.IsNotZero && m_proto.ZStepLength != RelTile1i.MaxValue;
        if (isFirst ? m_options.HasFlags(TransportPathFinderFlags.StartMustBeFlat) : ramps)
        {
            flags |= TransportPathFinderFlags.StartMustBeFlat;
        }
        if (isLast ? m_options.HasFlags(TransportPathFinderFlags.GoalMustBeFlat) : ramps)
        {
            flags |= TransportPathFinderFlags.GoalMustBeFlat;
        }

        Direction903d? forcedStart = isFirst ? m_options.ForcedStartDirection : null;
        ImmutableArray<Direction903d> bannedStarts = default;
        if (isFirst)
        {
            bannedStarts = m_options.BannedStartDirections;
        }
        else if (m_pivots.Count >= 2)
        {
            // Don't let the new leg double back into the one just placed.
            Tile3i last = m_pivots[m_pivots.Count - 1];
            Tile3i prev = m_pivots[m_pivots.Count - 2];
            RelTile3i arrival = last - prev;
            if (arrival.Xy != RelTile2i.Zero)
            {
                bannedStarts = ImmutableArray.Create(new Direction903d(-arrival.X, -arrival.Y, 0));
            }
        }

        var legOptions = new TransportPathFinderOptions(m_options.PreferredHeight, forcedStart, bannedStarts, flags);
        m_inner.InitPathFinding(m_proto, m_legStart, m_waypoints[m_legIndex], legOptions, m_bannedTiles);
    }

    private void appendLegPivots(ImmutableArray<Tile3i> legPivots)
    {
        foreach (Tile3i pivot in legPivots)
        {
            if (m_pivots.Count > 0 && m_pivots[m_pivots.Count - 1] == pivot)
            {
                continue;
            }
            m_pivots.Add(pivot);
        }
    }

    private void nudgeCurrentWaypoint()
    {
        // Nudge sideways relative to the leg's dominant travel axis so the offset actually steps
        // around the obstacle instead of along it.
        Tile3i waypoint = m_waypoints[m_legIndex];
        RelTile3i d = waypoint - m_legStart;
        RelTile2i nudge = NUDGES[m_nudgeIndex++];
        RelTile2i offset = Math.Abs(d.X) >= Math.Abs(d.Y)
            ? new RelTile2i(nudge.X * Math.Sign(d.X == 0 ? 1 : d.X), nudge.Y)
            : new RelTile2i(nudge.Y, nudge.X * Math.Sign(d.Y == 0 ? 1 : d.Y));
        m_waypoints[m_legIndex] = new Tile3i(waypoint.X + offset.X, waypoint.Y + offset.Y, waypoint.Z);
    }
}
