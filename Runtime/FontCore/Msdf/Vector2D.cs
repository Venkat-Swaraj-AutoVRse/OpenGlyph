using System;
using System.Runtime.CompilerServices;

namespace LightSide.Msdf
{
    /// <summary>
    /// Double-precision 2D vector used throughout MSDF generation.
    /// Ported from msdfgen's <c>Vector2</c> (Viktor Chlumsky, MIT). See Third-Party Notices.txt.
    /// </summary>
    public readonly struct Vector2D
    {
        public readonly double X;
        public readonly double Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2D(double x, double y) { X = x; Y = y; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2D(double v) { X = v; Y = v; }

        public double Length() => Math.Sqrt(X * X + Y * Y);
        public double SquaredLength() => X * X + Y * Y;

        /// <summary>Unit vector; returns (0,0) for a zero vector (matches msdfgen normalize()).</summary>
        public Vector2D Normalize(bool allowZero = false)
        {
            double len = Length();
            if (len == 0)
                return allowZero ? new Vector2D(0, 0) : new Vector2D(0, 1);
            return new Vector2D(X / len, Y / len);
        }

        /// <summary>Vector rotated 90° counter-clockwise (the left normal).</summary>
        public Vector2D GetOrthogonal(bool polarity = true) =>
            polarity ? new Vector2D(-Y, X) : new Vector2D(Y, -X);

        public Vector2D GetOrthonormal(bool polarity = true, bool allowZero = false)
        {
            double len = Length();
            if (len == 0)
                return polarity
                    ? new Vector2D(0, allowZero ? 0 : 1)
                    : new Vector2D(0, allowZero ? 0 : -1);
            return polarity ? new Vector2D(-Y / len, X / len) : new Vector2D(Y / len, -X / len);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2D operator +(Vector2D a, Vector2D b) => new Vector2D(a.X + b.X, a.Y + b.Y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2D operator -(Vector2D a, Vector2D b) => new Vector2D(a.X - b.X, a.Y - b.Y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2D operator *(Vector2D a, double s) => new Vector2D(a.X * s, a.Y * s);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2D operator *(double s, Vector2D a) => new Vector2D(a.X * s, a.Y * s);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2D operator /(Vector2D a, double s) => new Vector2D(a.X / s, a.Y / s);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2D operator -(Vector2D a) => new Vector2D(-a.X, -a.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Vector2D a, Vector2D b) => a.X == b.X && a.Y == b.Y;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Vector2D a, Vector2D b) => a.X != b.X || a.Y != b.Y;

        public bool Equals(Vector2D other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Vector2D v && Equals(v);
        public override int GetHashCode() => (X, Y).GetHashCode();

        /// <summary>Dot product.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Dot(Vector2D a, Vector2D b) => a.X * b.X + a.Y * b.Y;

        /// <summary>Z-component of the 3D cross product (signed area of the parallelogram).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Cross(Vector2D a, Vector2D b) => a.X * b.Y - a.Y * b.X;

        public override string ToString() => $"({X:R}, {Y:R})";
    }

    /// <summary>
    /// Signed distance with an orthogonality measure, used to resolve ties between edges.
    /// Ported from msdfgen's <c>SignedDistance</c> (MIT). Smaller absolute distance wins;
    /// on a tie the larger <see cref="Dot"/> (more perpendicular incidence) wins.
    /// </summary>
    public readonly struct SignedDistance
    {
        public readonly double Distance;
        public readonly double Dot;

        public static readonly SignedDistance Infinite = new SignedDistance(double.NegativeInfinity, 0);

        public SignedDistance(double distance, double dot)
        {
            Distance = distance;
            Dot = dot;
        }

        public static bool operator <(SignedDistance a, SignedDistance b) =>
            Math.Abs(a.Distance) < Math.Abs(b.Distance) ||
            (Math.Abs(a.Distance) == Math.Abs(b.Distance) && a.Dot < b.Dot);

        public static bool operator >(SignedDistance a, SignedDistance b) =>
            Math.Abs(a.Distance) > Math.Abs(b.Distance) ||
            (Math.Abs(a.Distance) == Math.Abs(b.Distance) && a.Dot > b.Dot);

        public static bool operator <=(SignedDistance a, SignedDistance b) =>
            Math.Abs(a.Distance) < Math.Abs(b.Distance) ||
            (Math.Abs(a.Distance) == Math.Abs(b.Distance) && a.Dot <= b.Dot);

        public static bool operator >=(SignedDistance a, SignedDistance b) =>
            Math.Abs(a.Distance) > Math.Abs(b.Distance) ||
            (Math.Abs(a.Distance) == Math.Abs(b.Distance) && a.Dot >= b.Dot);
    }

    /// <summary>Solvers for quadratic and cubic equations (msdfgen equation-solver.cpp, MIT).</summary>
    internal static class EquationSolver
    {
        private const double TooLargeRatio = 1e12;

        public static int SolveQuadratic(double[] x, double a, double b, double c)
        {
            // a == 0 -> linear
            if (Math.Abs(a) < 1e-14 || Math.Abs(b) > TooLargeRatio * Math.Abs(a))
            {
                if (Math.Abs(b) < 1e-14 || Math.Abs(c) > TooLargeRatio * Math.Abs(b))
                    return (c == 0) ? -1 : 0;
                x[0] = -c / b;
                return 1;
            }
            double dscr = b * b - 4 * a * c;
            if (dscr > 0)
            {
                dscr = Math.Sqrt(dscr);
                x[0] = (-b + dscr) / (2 * a);
                x[1] = (-b - dscr) / (2 * a);
                return 2;
            }
            if (dscr == 0)
            {
                x[0] = -b / (2 * a);
                return 1;
            }
            return 0;
        }

        private static int SolveCubicNormed(double[] x, double a, double b, double c)
        {
            double a2 = a * a;
            double q = (a2 - 3 * b) / 9;
            double r = (a * (2 * a2 - 9 * b) + 27 * c) / 54;
            double r2 = r * r;
            double q3 = q * q * q;
            if (r2 < q3)
            {
                double t = r / Math.Sqrt(q3);
                if (t < -1) t = -1;
                if (t > 1) t = 1;
                t = Math.Acos(t);
                a /= 3; q = -2 * Math.Sqrt(q);
                x[0] = q * Math.Cos(t / 3) - a;
                x[1] = q * Math.Cos((t + 2 * Math.PI) / 3) - a;
                x[2] = q * Math.Cos((t - 2 * Math.PI) / 3) - a;
                return 3;
            }
            else
            {
                double u = (r < 0 ? 1 : -1) * Math.Pow(Math.Abs(r) + Math.Sqrt(r2 - q3), 1.0 / 3.0);
                double v = (u == 0) ? 0 : q / u;
                x[0] = (u + v) - a / 3;
                if (u == v || Math.Abs(u - v) < 1e-12 * Math.Abs(u + v))
                {
                    x[1] = -0.5 * (u + v) - a / 3;
                    return 2;
                }
                return 1;
            }
        }

        public static int SolveCubic(double[] x, double a, double b, double c, double d)
        {
            if (Math.Abs(a) < 1e-14)
                return SolveQuadratic(x, b, c, d);
            if (Math.Abs(b) > TooLargeRatio * Math.Abs(a) ||
                Math.Abs(c) > TooLargeRatio * Math.Abs(a) ||
                Math.Abs(d) > TooLargeRatio * Math.Abs(a))
                return SolveQuadratic(x, b, c, d);
            return SolveCubicNormed(x, b / a, c / a, d / a);
        }
    }
}
