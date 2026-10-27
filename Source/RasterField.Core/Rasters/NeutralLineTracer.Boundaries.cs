using System;
using System.Collections.Generic;

namespace RasterField.Rasters
{
    public static partial class NeutralLineTracer
    {
        private readonly struct BoundaryCandidate
        {
            public BoundaryCandidate(double angle, double length) { Angle = angle; Length = length; }
            public double Angle { get; }
            public double Length { get; }
        }

        private sealed partial class Surface
        {
            public bool GradientAhead(double x, double y, double directionX, double directionY, out double gx, out double gy)
            {
                // Pick the outgoing patch only when the point is actually on a grid edge.
                // The elevation is still evaluated at the original coordinates.
                var pixel = _geo.WorldToPixel(x, y);
                double u = pixel.Column - 0.5, v = pixel.Row - 0.5;
                bool edge = Math.Abs(u - Math.Round(u)) < 1e-8 || Math.Abs(v - Math.Round(v)) < 1e-8;
                double epsilon = edge ? CellScale * 1e-7 : 0;
                return At(x + epsilon * directionX, y + epsilon * directionY, out _, out gx, out gy);
            }

            public List<BoundaryCandidate> BoundaryCandidates(double x, double y, double maximumLength,
                double targetStart, double grade, double heading)
            {
                var result = new List<BoundaryCandidate>();
                double epsilon = CellScale * 1e-7;
                var pixel = _geo.WorldToPixel(x + epsilon * Math.Cos(heading), y + epsilon * Math.Sin(heading));
                int col = (int)Math.Floor(pixel.Column - 0.5), row = (int)Math.Floor(pixel.Row - 0.5);
                if (col < 0 || row < 0 || col >= _raster.Width - 1 || row >= _raster.Height - 1) return result;
                AddEdge(col, row, col + 1, row);
                AddEdge(col, row + 1, col + 1, row + 1);
                AddEdge(col, row, col, row + 1);
                AddEdge(col + 1, row, col + 1, row + 1);
                return result;

                void AddEdge(int c0, int r0, int c1, int r1)
                {
                    double zA = _raster[r0, c0], zB = _raster[r1, c1];
                    if (!Valid(zA) || !Valid(zB)) return;
                    var a = _geo.CellCentreToWorld(c0, r0);
                    var b = _geo.CellCentreToWorld(c1, r1);
                    double vx = a.X - x, vy = a.Y - y, wx = b.X - a.X, wy = b.Y - a.Y;
                    double z = zA - targetStart, dz = zB - zA, gg = grade * grade;
                    if (grade == 0)
                    {
                        // Do not square the contour equation: that would turn a simple
                        // crossing into a numerically ill-conditioned double root.
                        if (dz != 0) AddRoot(-z / dz);
                        else if (z == 0) { AddRoot(0); AddRoot(1); }
                        return;
                    }
                    // Along a patch edge z is linear. Square z(t)=grade*distance(t)
                    // to obtain a quadratic, then reject extraneous roots using its sign.
                    double qa = dz * dz - gg * (wx * wx + wy * wy);
                    double qb = 2 * (z * dz - gg * (vx * wx + vy * wy));
                    double qc = z * z - gg * (vx * vx + vy * vy);
                    double scale = Math.Abs(qa) + Math.Abs(qb) + Math.Abs(qc);
                    if (Math.Abs(qa) <= 1e-14 * scale)
                    {
                        if (Math.Abs(qb) > 1e-14 * scale) AddRoot(-qc / qb);
                        else if (Math.Abs(qc) <= 1e-14 * scale) { AddRoot(0); AddRoot(1); }
                        return;
                    }
                    double discriminant = qb * qb - 4 * qa * qc;
                    double rounding = 1e-14 * (qb * qb + Math.Abs(4 * qa * qc));
                    if (discriminant < -rounding) return;
                    double root = Math.Sqrt(Math.Max(0, discriminant));
                    double q = -0.5 * (qb + (qb >= 0 ? root : -root));
                    if (q == 0) AddRoot(-qb / (2 * qa));
                    else { AddRoot(q / qa); AddRoot(qc / q); }

                    void AddRoot(double t)
                    {
                        if (!Finite(t) || t < -1e-10 || t > 1 + 1e-10) return;
                        t = Math.Max(0, Math.Min(1, t));
                        double dx = vx + t * wx, dy = vy + t * wy;
                        double length = Math.Sqrt(dx * dx + dy * dy);
                        if (length < CellScale * 1e-7 || length > maximumLength) return;
                        double residual = z + t * dz - grade * length;
                        if (Math.Abs(residual) > Math.Max(1e-10, 1e-12 * Math.Abs(targetStart))) return;
                        result.Add(new BoundaryCandidate(Math.Atan2(dy, dx), length));
                    }
                }
            }
        }
    }
}
