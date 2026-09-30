using System;

namespace LightSide.Msdf
{
    /// <summary>
    /// Per-edge channel mask. An edge contributes to the channels whose bit is set.
    /// Ported from msdfgen's <c>EdgeColor</c> (MIT). BLACK/WHITE are used transiently by coloring.
    /// </summary>
    [Flags]
    public enum EdgeColor
    {
        Black = 0,
        Red = 1,
        Green = 2,
        Yellow = 3,
        Blue = 4,
        Magenta = 5,
        Cyan = 6,
        White = 7,
    }

    /// <summary>
    /// A single glyph-outline edge (line, quadratic or cubic Bézier) carrying an
    /// <see cref="EdgeColor"/>. Provides the signed distance / direction queries the MSDF
    /// generator needs. Ported from msdfgen edge-segments.cpp (Viktor Chlumsky, MIT).
    /// See Third-Party Notices.txt.
    /// </summary>
    public abstract class EdgeSegment
    {
        public EdgeColor Color;

        protected EdgeSegment(EdgeColor color) { Color = color; }

        /// <summary>Point on the edge at parameter t in [0,1].</summary>
        public abstract Vector2D Point(double t);

        /// <summary>Tangent (non-normalised direction) at parameter t.</summary>
        public abstract Vector2D Direction(double t);

        /// <summary>
        /// Signed distance from <paramref name="origin"/> to this edge; <paramref name="t"/>
        /// receives the nearest parameter (may lie outside [0,1] on the endpoints' rays).
        /// </summary>
        public abstract SignedDistance MinSignedDistance(Vector2D origin, out double t);

        /// <summary>Splits [0,1] into thirds for edge subdivision during coloring.</summary>
        public abstract void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c);

        public Vector2D Point0 { get; protected set; }
        public Vector2D Point1 { get; protected set; }

        /// <summary>
        /// Refines the perpendicular-distance sign at an endpoint to match msdfgen's
        /// <c>distanceToPerpendicularDistance</c>, improving corner reconstruction.
        /// </summary>
        public void DistanceToPerpendicularDistance(ref SignedDistance distance, Vector2D origin, double t)
        {
            if (t < 0)
            {
                Vector2D dir = Direction(0).Normalize();
                Vector2D aq = origin - Point(0);
                double ts = Vector2D.Dot(aq, dir);
                if (ts < 0)
                {
                    double pd = Vector2D.Cross(aq, dir);
                    if (Math.Abs(pd) <= Math.Abs(distance.Distance))
                        distance = new SignedDistance(pd, 0);
                }
            }
            else if (t > 1)
            {
                Vector2D dir = Direction(1).Normalize();
                Vector2D bq = origin - Point(1);
                double ts = Vector2D.Dot(bq, dir);
                if (ts > 0)
                {
                    double pd = Vector2D.Cross(bq, dir);
                    if (Math.Abs(pd) <= Math.Abs(distance.Distance))
                        distance = new SignedDistance(pd, 0);
                }
            }
        }

        private static double NonZeroSign(double n) => n > 0 ? 1 : -1;
    }

    public sealed class LinearSegment : EdgeSegment
    {
        public LinearSegment(Vector2D p0, Vector2D p1, EdgeColor color = EdgeColor.White) : base(color)
        {
            Point0 = p0; Point1 = p1;
        }

        public override Vector2D Point(double t) => Point0 + (Point1 - Point0) * t;
        public override Vector2D Direction(double t) => Point1 - Point0;

        public override SignedDistance MinSignedDistance(Vector2D origin, out double t)
        {
            Vector2D aq = origin - Point0;
            Vector2D ab = Point1 - Point0;
            t = Vector2D.Dot(aq, ab) / Vector2D.Dot(ab, ab);
            Vector2D eq = (t > 0.5 ? Point1 : Point0) - origin;
            double endpointDistance = eq.Length();
            if (t > 0 && t < 1)
            {
                double orthoDistance = Vector2D.Dot(ab.GetOrthonormal(false), aq);
                if (Math.Abs(orthoDistance) < endpointDistance)
                    return new SignedDistance(orthoDistance, 0);
            }
            double sign = Sign(Vector2D.Cross(aq, ab));
            double dot = Math.Abs(Vector2D.Dot(ab.Normalize(), eq.Normalize()));
            return new SignedDistance(sign * endpointDistance, dot);
        }

        public override void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c)
        {
            a = new LinearSegment(Point0, Point(1.0 / 3.0), Color);
            b = new LinearSegment(Point(1.0 / 3.0), Point(2.0 / 3.0), Color);
            c = new LinearSegment(Point(2.0 / 3.0), Point1, Color);
        }

        private static double Sign(double n) => n > 0 ? 1 : (n < 0 ? -1 : 0);
    }

    public sealed class QuadraticSegment : EdgeSegment
    {
        private readonly Vector2D _p1; // control

        public QuadraticSegment(Vector2D p0, Vector2D control, Vector2D p1, EdgeColor color = EdgeColor.White)
            : base(color)
        {
            // Degenerate control -> treat as line midpoint (msdfgen guard).
            if (control == p0 || control == p1)
                control = 0.5 * (p0 + p1);
            Point0 = p0; _p1 = control; Point1 = p1;
        }

        public Vector2D Control => _p1;

        public override Vector2D Point(double t)
        {
            double mt = 1 - t;
            return mt * mt * Point0 + 2 * mt * t * _p1 + t * t * Point1;
        }

        public override Vector2D Direction(double t)
        {
            Vector2D tangent = (1 - t) * (_p1 - Point0) + t * (Point1 - _p1);
            if (tangent.X == 0 && tangent.Y == 0)
                return Point1 - Point0;
            return tangent;
        }

        public override SignedDistance MinSignedDistance(Vector2D origin, out double t)
        {
            Vector2D qa = Point0 - origin;
            Vector2D ab = _p1 - Point0;
            Vector2D br = Point0 + Point1 - _p1 - _p1;

            double a = Vector2D.Dot(br, br);
            double b = 3 * Vector2D.Dot(ab, br);
            double c = 2 * Vector2D.Dot(ab, ab) + Vector2D.Dot(qa, br);
            double d = Vector2D.Dot(qa, ab);

            double[] roots = new double[3];
            int solutions = EquationSolver.SolveCubic(roots, a, b, c, d);

            Vector2D epDir = Direction(0);
            double minDistance = Sign(Vector2D.Cross(epDir, qa)) * qa.Length();
            t = -Vector2D.Dot(qa, epDir) / Vector2D.Dot(epDir, epDir);
            {
                epDir = Direction(1);
                double distance = (Point1 - origin).Length();
                if (distance < Math.Abs(minDistance))
                {
                    minDistance = Sign(Vector2D.Cross(epDir, Point1 - origin)) * distance;
                    t = Vector2D.Dot(origin - _p1, epDir) / Vector2D.Dot(epDir, epDir);
                }
            }
            for (int i = 0; i < solutions; i++)
            {
                if (roots[i] > 0 && roots[i] < 1)
                {
                    Vector2D qe = qa + 2 * roots[i] * ab + roots[i] * roots[i] * br;
                    double distance = qe.Length();
                    if (distance <= Math.Abs(minDistance))
                    {
                        minDistance = Sign(Vector2D.Cross(Direction(roots[i]), qe)) * distance;
                        t = roots[i];
                    }
                }
            }

            if (t >= 0 && t <= 1)
                return new SignedDistance(minDistance, 0);
            if (t < 0.5)
                return new SignedDistance(minDistance,
                    Math.Abs(Vector2D.Dot(Direction(0).Normalize(), qa.Normalize())));
            return new SignedDistance(minDistance,
                Math.Abs(Vector2D.Dot(Direction(1).Normalize(), (Point1 - origin).Normalize())));
        }

        public override void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c)
        {
            a = new QuadraticSegment(Point0, Lerp(Point0, _p1, 1.0 / 3.0), Point(1.0 / 3.0), Color);
            b = new QuadraticSegment(Point(1.0 / 3.0),
                Lerp(Lerp(Point0, _p1, 5.0 / 9.0), Lerp(_p1, Point1, 4.0 / 9.0), 0.5),
                Point(2.0 / 3.0), Color);
            c = new QuadraticSegment(Point(2.0 / 3.0), Lerp(_p1, Point1, 2.0 / 3.0), Point1, Color);
        }

        private static Vector2D Lerp(Vector2D a, Vector2D b, double t) => a + (b - a) * t;
        private static double Sign(double n) => n > 0 ? 1 : (n < 0 ? -1 : 0);
    }

    public sealed class CubicSegment : EdgeSegment
    {
        private const int SearchStarts = 4;
        private const int SearchSteps = 4;

        private readonly Vector2D _p1; // control 1
        private readonly Vector2D _p2; // control 2

        public CubicSegment(Vector2D p0, Vector2D c1, Vector2D c2, Vector2D p1, EdgeColor color = EdgeColor.White)
            : base(color)
        {
            // Degenerate controls guard (msdfgen).
            if ((c1 == p0 || c1 == p1) && (c2 == p0 || c2 == p1))
            {
                c1 = Lerp(p0, p1, 1.0 / 3.0);
                c2 = Lerp(p0, p1, 2.0 / 3.0);
            }
            Point0 = p0; _p1 = c1; _p2 = c2; Point1 = p1;
        }

        public Vector2D Control1 => _p1;
        public Vector2D Control2 => _p2;

        public override Vector2D Point(double t)
        {
            Vector2D p12 = Lerp(_p1, _p2, t);
            return Lerp(Lerp(Lerp(Point0, _p1, t), p12, t),
                        Lerp(p12, Lerp(_p2, Point1, t), t), t);
        }

        public override Vector2D Direction(double t)
        {
            Vector2D tangent = Lerp(Lerp(_p1 - Point0, _p2 - _p1, t), Lerp(_p2 - _p1, Point1 - _p2, t), t);
            if (tangent.X == 0 && tangent.Y == 0)
            {
                if (t == 0) return _p2 - Point0;
                if (t == 1) return Point1 - _p1;
            }
            return tangent;
        }

        public override SignedDistance MinSignedDistance(Vector2D origin, out double t)
        {
            Vector2D qa = Point0 - origin;
            Vector2D ab = _p1 - Point0;
            Vector2D br = _p2 - _p1 - ab;
            Vector2D as_ = (Point1 - _p2) - (_p2 - _p1) - br;

            Vector2D epDir = Direction(0);
            double minDistance = Sign(Vector2D.Cross(epDir, qa)) * qa.Length();
            t = -Vector2D.Dot(qa, epDir) / Vector2D.Dot(epDir, epDir);
            {
                epDir = Direction(1);
                double distance = (Point1 - origin).Length();
                if (distance < Math.Abs(minDistance))
                {
                    minDistance = Sign(Vector2D.Cross(epDir, Point1 - origin)) * distance;
                    t = Vector2D.Dot(epDir - (Point1 - origin), epDir) / Vector2D.Dot(epDir, epDir);
                }
            }

            for (int i = 0; i <= SearchStarts; i++)
            {
                double tt = (double)i / SearchStarts;
                Vector2D qe = qa + 3 * tt * ab + 3 * tt * tt * br + tt * tt * tt * as_;
                for (int step = 0; step < SearchSteps; step++)
                {
                    Vector2D d1 = 3 * ab + 6 * tt * br + 3 * tt * tt * as_;
                    Vector2D d2 = 6 * br + 6 * tt * as_;
                    double num = Vector2D.Dot(qe, d1);
                    double den = Vector2D.Dot(d1, d1) + Vector2D.Dot(qe, d2);
                    if (den == 0) break;
                    tt -= num / den;
                    if (tt <= 0 || tt >= 1) break;
                    qe = qa + 3 * tt * ab + 3 * tt * tt * br + tt * tt * tt * as_;
                    double distance = qe.Length();
                    if (distance < Math.Abs(minDistance))
                    {
                        minDistance = Sign(Vector2D.Cross(Direction(tt), qe)) * distance;
                        t = tt;
                    }
                }
            }

            if (t >= 0 && t <= 1)
                return new SignedDistance(minDistance, 0);
            if (t < 0.5)
                return new SignedDistance(minDistance,
                    Math.Abs(Vector2D.Dot(Direction(0).Normalize(), qa.Normalize())));
            return new SignedDistance(minDistance,
                Math.Abs(Vector2D.Dot(Direction(1).Normalize(), (Point1 - origin).Normalize())));
        }

        public override void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c)
        {
            a = new CubicSegment(Point0,
                Point0 == _p1 ? Point0 : Lerp(Point0, _p1, 1.0 / 3.0),
                Lerp(Lerp(Point0, _p1, 1.0 / 3.0), Lerp(_p1, _p2, 1.0 / 3.0), 1.0 / 3.0),
                Point(1.0 / 3.0), Color);
            b = new CubicSegment(Point(1.0 / 3.0),
                Lerp(Lerp(Lerp(Point0, _p1, 1.0 / 3.0), Lerp(_p1, _p2, 1.0 / 3.0), 1.0 / 3.0),
                     Lerp(Lerp(_p1, _p2, 1.0 / 3.0), Lerp(_p2, Point1, 1.0 / 3.0), 1.0 / 3.0), 2.0 / 3.0),
                Lerp(Lerp(Lerp(Point0, _p1, 2.0 / 3.0), Lerp(_p1, _p2, 2.0 / 3.0), 2.0 / 3.0),
                     Lerp(Lerp(_p1, _p2, 2.0 / 3.0), Lerp(_p2, Point1, 2.0 / 3.0), 2.0 / 3.0), 1.0 / 3.0),
                Point(2.0 / 3.0), Color);
            c = new CubicSegment(Point(2.0 / 3.0),
                Lerp(Lerp(_p1, _p2, 2.0 / 3.0), Lerp(_p2, Point1, 2.0 / 3.0), 2.0 / 3.0),
                _p2 == Point1 ? Point1 : Lerp(_p2, Point1, 2.0 / 3.0),
                Point1, Color);
        }

        private static Vector2D Lerp(Vector2D a, Vector2D b, double t) => a + (b - a) * t;
        private static double Sign(double n) => n > 0 ? 1 : (n < 0 ? -1 : 0);
    }
}
