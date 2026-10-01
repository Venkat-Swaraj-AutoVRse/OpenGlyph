using System;
using System.Collections.Generic;

namespace LightSide.Msdf
{
    /// <summary>
    /// Faithful port of msdfgen v1.12 (85e8b3d, MIT, Viktor Chlumsky) multi-channel distance
    /// selection + contour combination: <c>core/edge-selectors.cpp/.h</c>
    /// (<c>PerpendicularDistanceSelectorBase</c>, <c>MultiDistanceSelector</c>) and
    /// <c>core/contour-combiners.cpp/.h</c> (<c>OverlappingContourCombiner</c>), driven as in
    /// <c>ShapeDistanceFinder.hpp</c>. See Third-Party Notices.txt. No upstream source is bundled.
    ///
    /// WHY. OpenGlyph's earlier <see cref="MsdfGenerator"/> computed each channel as a flat global
    /// nearest-edge min over ALL edges of ALL contours, then one perpendicular-distance extension.
    /// That coincides with msdfgen inside a single contour (so bulk fields matched) but DIVERGES at
    /// edge-domain boundaries (corners/junctions) and at overlapping contours, because msdfgen
    /// (a) resolves the perpendicular distance per channel with the prev/next-edge domain gating
    /// below, and (b) combines contours by winding via OverlappingContourCombiner. Those were the
    /// measured RAW |Δ| of 0.05–0.27 on W/g/&amp;/@ (NativeSource~/tests/msdfref/PARITY_RESULTS_MSDF.md).
    /// This file makes the generator bit-faithful.
    /// </summary>
    internal static class MsdfDistance
    {
        private const double DistanceDeltaFactor = 1.001; // msdfgen DISTANCE_DELTA_FACTOR
        private static double NonZeroSign(double n) => n > 0 ? 1 : -1;

        // ===== PerpendicularDistanceSelectorBase (edge-selectors.cpp) ============================
        internal struct EdgeCache
        {
            public Vector2D Point;
            public double AbsDistance;
            public double ADomainDistance, BDomainDistance;
            public double APerpendicularDistance, BPerpendicularDistance;
        }

        internal sealed class PerpendicularDistanceSelectorBase
        {
            private SignedDistance _minTrueDistance = SignedDistance.Infinite;
            private double _minNegativePerp;
            private double _minPositivePerp;
            private EdgeSegment _nearEdge;
            private double _nearEdgeParam;

            public PerpendicularDistanceSelectorBase()
            {
                _minNegativePerp = -Math.Abs(_minTrueDistance.Distance);
                _minPositivePerp = Math.Abs(_minTrueDistance.Distance);
            }

            public static bool GetPerpendicularDistance(ref double distance, Vector2D ep, Vector2D edgeDir)
            {
                double ts = Vector2D.Dot(ep, edgeDir);
                if (ts > 0)
                {
                    double perp = Vector2D.Cross(ep, edgeDir);
                    if (Math.Abs(perp) < Math.Abs(distance)) { distance = perp; return true; }
                }
                return false;
            }

            public void Reset(double delta)
            {
                _minTrueDistance = new SignedDistance(
                    _minTrueDistance.Distance + NonZeroSign(_minTrueDistance.Distance) * delta,
                    _minTrueDistance.Dot);
                _minNegativePerp = -Math.Abs(_minTrueDistance.Distance);
                _minPositivePerp = Math.Abs(_minTrueDistance.Distance);
                _nearEdge = null;
                _nearEdgeParam = 0;
            }

            public bool IsEdgeRelevant(in EdgeCache cache, Vector2D p)
            {
                double delta = DistanceDeltaFactor * (p - cache.Point).Length();
                return
                    cache.AbsDistance - delta <= Math.Abs(_minTrueDistance.Distance) ||
                    Math.Abs(cache.ADomainDistance) < delta ||
                    Math.Abs(cache.BDomainDistance) < delta ||
                    (cache.ADomainDistance > 0 && (cache.APerpendicularDistance < 0
                        ? cache.APerpendicularDistance + delta >= _minNegativePerp
                        : cache.APerpendicularDistance - delta <= _minPositivePerp)) ||
                    (cache.BDomainDistance > 0 && (cache.BPerpendicularDistance < 0
                        ? cache.BPerpendicularDistance + delta >= _minNegativePerp
                        : cache.BPerpendicularDistance - delta <= _minPositivePerp));
            }

            public void AddEdgeTrueDistance(EdgeSegment edge, in SignedDistance distance, double param)
            {
                if (distance < _minTrueDistance)
                {
                    _minTrueDistance = distance;
                    _nearEdge = edge;
                    _nearEdgeParam = param;
                }
            }

            public void AddEdgePerpendicularDistance(double distance)
            {
                if (distance <= 0 && distance > _minNegativePerp) _minNegativePerp = distance;
                if (distance >= 0 && distance < _minPositivePerp) _minPositivePerp = distance;
            }

            public void Merge(PerpendicularDistanceSelectorBase other)
            {
                if (other._minTrueDistance < _minTrueDistance)
                {
                    _minTrueDistance = other._minTrueDistance;
                    _nearEdge = other._nearEdge;
                    _nearEdgeParam = other._nearEdgeParam;
                }
                if (other._minNegativePerp > _minNegativePerp) _minNegativePerp = other._minNegativePerp;
                if (other._minPositivePerp < _minPositivePerp) _minPositivePerp = other._minPositivePerp;
            }

            public double ComputeDistance(Vector2D p)
            {
                double minDistance = _minTrueDistance.Distance < 0 ? _minNegativePerp : _minPositivePerp;
                if (_nearEdge != null)
                {
                    SignedDistance distance = _minTrueDistance;
                    _nearEdge.DistanceToPerpendicularDistance(ref distance, p, _nearEdgeParam);
                    if (Math.Abs(distance.Distance) < Math.Abs(minDistance)) minDistance = distance.Distance;
                }
                return minDistance;
            }
        }

        // ===== MultiDistanceSelector (edge-selectors.cpp) ========================================
        internal sealed class MultiDistanceSelector
        {
            private Vector2D _p;
            private readonly PerpendicularDistanceSelectorBase _r = new PerpendicularDistanceSelectorBase();
            private readonly PerpendicularDistanceSelectorBase _g = new PerpendicularDistanceSelectorBase();
            private readonly PerpendicularDistanceSelectorBase _b = new PerpendicularDistanceSelectorBase();

            public void Reset(Vector2D p)
            {
                double delta = DistanceDeltaFactor * (p - _p).Length();
                _r.Reset(delta); _g.Reset(delta); _b.Reset(delta);
                _p = p;
            }

            public void AddEdge(ref EdgeCache cache, EdgeSegment prevEdge, EdgeSegment edge, EdgeSegment nextEdge)
            {
                EdgeColor color = edge.Color;
                if (((color & EdgeColor.Red) != 0 && _r.IsEdgeRelevant(cache, _p)) ||
                    ((color & EdgeColor.Green) != 0 && _g.IsEdgeRelevant(cache, _p)) ||
                    ((color & EdgeColor.Blue) != 0 && _b.IsEdgeRelevant(cache, _p)))
                {
                    SignedDistance distance = edge.MinSignedDistance(_p, out double param);
                    if ((color & EdgeColor.Red) != 0) _r.AddEdgeTrueDistance(edge, distance, param);
                    if ((color & EdgeColor.Green) != 0) _g.AddEdgeTrueDistance(edge, distance, param);
                    if ((color & EdgeColor.Blue) != 0) _b.AddEdgeTrueDistance(edge, distance, param);
                    cache.Point = _p;
                    cache.AbsDistance = Math.Abs(distance.Distance);

                    Vector2D ap = _p - edge.Point(0);
                    Vector2D bp = _p - edge.Point(1);
                    Vector2D aDir = edge.Direction(0).Normalize(true);
                    Vector2D bDir = edge.Direction(1).Normalize(true);
                    Vector2D prevDir = prevEdge.Direction(1).Normalize(true);
                    Vector2D nextDir = nextEdge.Direction(0).Normalize(true);
                    double add = Vector2D.Dot(ap, (prevDir + aDir).Normalize(true));
                    double bdd = -Vector2D.Dot(bp, (bDir + nextDir).Normalize(true));
                    if (add > 0)
                    {
                        double pd = distance.Distance;
                        if (PerpendicularDistanceSelectorBase.GetPerpendicularDistance(ref pd, ap, -aDir))
                        {
                            pd = -pd;
                            if ((color & EdgeColor.Red) != 0) _r.AddEdgePerpendicularDistance(pd);
                            if ((color & EdgeColor.Green) != 0) _g.AddEdgePerpendicularDistance(pd);
                            if ((color & EdgeColor.Blue) != 0) _b.AddEdgePerpendicularDistance(pd);
                        }
                        cache.APerpendicularDistance = pd;
                    }
                    if (bdd > 0)
                    {
                        double pd = distance.Distance;
                        if (PerpendicularDistanceSelectorBase.GetPerpendicularDistance(ref pd, bp, bDir))
                        {
                            if ((color & EdgeColor.Red) != 0) _r.AddEdgePerpendicularDistance(pd);
                            if ((color & EdgeColor.Green) != 0) _g.AddEdgePerpendicularDistance(pd);
                            if ((color & EdgeColor.Blue) != 0) _b.AddEdgePerpendicularDistance(pd);
                        }
                        cache.BPerpendicularDistance = pd;
                    }
                    cache.ADomainDistance = add;
                    cache.BDomainDistance = bdd;
                }
            }

            public void Merge(MultiDistanceSelector other) { _r.Merge(other._r); _g.Merge(other._g); _b.Merge(other._b); }

            public (double r, double g, double b) Distance()
                => (_r.ComputeDistance(_p), _g.ComputeDistance(_p), _b.ComputeDistance(_p));
        }

        // ===== OverlappingContourCombiner<MultiDistanceSelector> (contour-combiners.cpp) =========
        private static double ResolveDistance(in (double r, double g, double b) d) =>
            Math.Max(Math.Min(d.r, d.g), Math.Min(Math.Max(d.r, d.g), d.b)); // median

        internal sealed class OverlappingContourCombiner
        {
            private readonly int[] _windings;
            private readonly MultiDistanceSelector[] _edgeSelectors;
            private Vector2D _p;

            public OverlappingContourCombiner(Shape shape)
            {
                int n = shape.Contours.Count;
                _windings = new int[n];
                _edgeSelectors = new MultiDistanceSelector[n];
                for (int i = 0; i < n; i++)
                {
                    _windings[i] = shape.Contours[i].Winding();
                    _edgeSelectors[i] = new MultiDistanceSelector();
                }
            }

            public void Reset(Vector2D p) { _p = p; foreach (var s in _edgeSelectors) s.Reset(p); }
            public MultiDistanceSelector EdgeSelector(int i) => _edgeSelectors[i];

            public (double r, double g, double b) Distance()
            {
                int contourCount = _edgeSelectors.Length;
                var shapeSel = new MultiDistanceSelector();
                var innerSel = new MultiDistanceSelector();
                var outerSel = new MultiDistanceSelector();
                shapeSel.Reset(_p); innerSel.Reset(_p); outerSel.Reset(_p);
                for (int i = 0; i < contourCount; i++)
                {
                    var edgeDistance = _edgeSelectors[i].Distance();
                    shapeSel.Merge(_edgeSelectors[i]);
                    if (_windings[i] > 0 && ResolveDistance(edgeDistance) >= 0) innerSel.Merge(_edgeSelectors[i]);
                    if (_windings[i] < 0 && ResolveDistance(edgeDistance) <= 0) outerSel.Merge(_edgeSelectors[i]);
                }

                var shapeDistance = shapeSel.Distance();
                var innerDistance = innerSel.Distance();
                var outerDistance = outerSel.Distance();
                double innerScalar = ResolveDistance(innerDistance);
                double outerScalar = ResolveDistance(outerDistance);
                (double r, double g, double b) distance = (double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

                int winding = 0;
                if (innerScalar >= 0 && Math.Abs(innerScalar) <= Math.Abs(outerScalar))
                {
                    distance = innerDistance; winding = 1;
                    for (int i = 0; i < contourCount; i++)
                        if (_windings[i] > 0)
                        {
                            var cd = _edgeSelectors[i].Distance();
                            if (Math.Abs(ResolveDistance(cd)) < Math.Abs(outerScalar) && ResolveDistance(cd) > ResolveDistance(distance))
                                distance = cd;
                        }
                }
                else if (outerScalar <= 0 && Math.Abs(outerScalar) < Math.Abs(innerScalar))
                {
                    distance = outerDistance; winding = -1;
                    for (int i = 0; i < contourCount; i++)
                        if (_windings[i] < 0)
                        {
                            var cd = _edgeSelectors[i].Distance();
                            if (Math.Abs(ResolveDistance(cd)) < Math.Abs(innerScalar) && ResolveDistance(cd) < ResolveDistance(distance))
                                distance = cd;
                        }
                }
                else
                    return shapeDistance;

                for (int i = 0; i < contourCount; i++)
                    if (_windings[i] != winding)
                    {
                        var cd = _edgeSelectors[i].Distance();
                        if (ResolveDistance(cd) * ResolveDistance(distance) >= 0 && Math.Abs(ResolveDistance(cd)) < Math.Abs(ResolveDistance(distance)))
                            distance = cd;
                    }
                if (ResolveDistance(distance) == ResolveDistance(shapeDistance))
                    distance = shapeDistance;
                return distance;
            }
        }

        /// <summary>
        /// ShapeDistanceFinder.distance(origin) for OverlappingContourCombiner&lt;MultiDistanceSelector&gt;:
        /// feeds each contour's edges with the prev/cur/next wrap exactly as msdfgen's loop, then
        /// combines. msdfgen keeps a persistent per-edge EdgeCache array (shapeEdgeCache) across
        /// successive distance() calls to skip far edges; we instead reset a fresh cache per edge,
        /// which only disables that optimisation — the selected distance is identical because every
        /// edge is still offered to the selector (isEdgeRelevant on a fresh zero cache returns true).
        /// Returns per-channel distances (NOT yet range-mapped).
        /// </summary>
        public static (double r, double g, double b) ShapeDistance(OverlappingContourCombiner combiner, Shape shape, Vector2D origin)
        {
            combiner.Reset(origin);
            int ci = 0;
            foreach (var contour in shape.Contours)
            {
                var edges = contour.Edges;
                if (edges.Count > 0)
                {
                    var sel = combiner.EdgeSelector(ci);
                    EdgeSegment prevEdge = edges.Count >= 2 ? edges[edges.Count - 2] : edges[0];
                    EdgeSegment curEdge = edges[edges.Count - 1];
                    for (int e = 0; e < edges.Count; e++)
                    {
                        EdgeSegment nextEdge = edges[e];
                        var cache = new EdgeCache();
                        sel.AddEdge(ref cache, prevEdge, curEdge, nextEdge);
                        prevEdge = curEdge;
                        curEdge = nextEdge;
                    }
                }
                ci++;
            }
            return combiner.Distance();
        }
    }
}
