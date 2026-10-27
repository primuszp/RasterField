using System;
using System.Collections.Generic;
using System.Threading;

namespace RasterField.Rasters
{
    public static partial class NeutralLineTracer
    {
        /// <summary>
        /// Searches a continuous bilinear terrain for a complete, signed constant-grade connection.
        /// Candidate states retain chainage and incoming direction. Every edge and the exact goal
        /// connection pass the same full-segment checker as Trace. The bounded angular/spatial
        /// search is deliberately incomplete: NoRouteFound is not a mathematical impossibility proof.
        /// Sharp bends are permitted; road curvature design is a separate operation.
        /// </summary>
        public static NeutralRouteResult FindRoute(Raster raster, RasterGeoReference geo,
            double startX, double startY, double endX, double endY, NeutralRouteOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!Finite(startX) || !Finite(startY) || !Finite(endX) || !Finite(endY) || !Finite(options.GradePercent) ||
                !Positive(options.MaximumLength) || !Positive(options.HeightTolerance) ||
                !Positive(options.GradeTolerancePercent) || options.MaximumStates < 1 || options.MaximumStoredStates < 1)
                throw new ArgumentOutOfRangeException(nameof(options));
            var surface = CreateSurface(raster, geo);
            cancellationToken.ThrowIfCancellationRequested();
            double z0 = 0;
            double? required = null;
            int expanded = 0;
            if (!surface.At(startX, startY, out z0, out _, out _) || !surface.At(endX, endY, out double endZ, out _, out _))
                return Failure(NeutralRouteStatus.EndpointNoData);
            double distance = Distance(startX, startY, endX, endY);
            if (distance <= surface.CellScale * 1e-9) return Failure(NeutralRouteStatus.CoincidentEndpoints);
            double grade = options.GradePercent / 100;
            double maximumLength = options.MaximumLength;
            if (grade == 0)
            {
                if (Math.Abs(endZ - z0) > options.HeightTolerance) return Failure(NeutralRouteStatus.IncompatibleGrade);
            }
            else
            {
                required = (endZ - z0) / grade;
                double longestCompatible = required.Value + options.HeightTolerance / Math.Abs(grade);
                if (longestCompatible < distance) return Failure(NeutralRouteStatus.IncompatibleGrade);
                double shortestCompatible = required.Value - options.HeightTolerance / Math.Abs(grade);
                if (shortestCompatible > options.MaximumLength) return Failure(NeutralRouteStatus.LengthLimit);
                maximumLength = Math.Min(maximumLength, longestCompatible);
            }
            if (distance > maximumLength) return Failure(NeutralRouteStatus.LengthLimit);

            double step = surface.CellScale;
            double numericalLength = 1e-9 * Math.Max(1, maximumLength);
            var start = new RouteNode(startX, startY, 0, 0, null, null, 0);
            var frontier = new RouteHeap();
            frontier.Push(start, distance);
            var visited = new Dictionary<(long X, long Y, long S, int Heading), double>();
            int stored = 1;
            while (frontier.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expanded >= options.MaximumStates) return Failure(NeutralRouteStatus.SearchLimit);
                var node = frontier.Pop();
                double toGoal = Distance(node.X, node.Y, endX, endY);
                if (node.S + toGoal > maximumLength + numericalLength) continue;
                expanded++;
                if (node.X == endX && node.Y == endY) return Complete(node);

                // The goal is always the actual supplied coordinate, never a nearby grid cell.
                if (node.Parent == null || toGoal <= 2 * step ||
                    (required.HasValue && Math.Abs(required.Value - node.S - toGoal) <= options.HeightTolerance / Math.Max(Math.Abs(grade), 1e-15)))
                {
                    var direct = Checked(node, endX, endY);
                    if (direct != null) return Complete(direct);
                }
                if (node.Parent == null || toGoal <= 3 * step)
                {
                    var connection = ConnectViaEllipse(node, toGoal);
                    if (connection != null) return Complete(connection);
                }

                var candidates = new List<(double X, double Y)>();
                var headings = new List<double>();
                // Different incident patches can have different gradients on a grid edge.
                for (int direction = 0; direction < 8; direction++)
                {
                    double angle = direction * Math.PI / 4;
                    if (!surface.GradientAhead(node.X, node.Y, Math.Cos(angle), Math.Sin(angle), out double gx, out double gy)) continue;
                    double q = Math.Sqrt(gx * gx + gy * gy);
                    if (q < Math.Abs(grade) - 1e-12 || q < 1e-12) continue;
                    double along = grade / q, across = Math.Sqrt(Math.Max(0, 1 - along * along));
                    AddHeading(Math.Atan2(along * gy + across * gx, along * gx - across * gy));
                    AddHeading(Math.Atan2(along * gy - across * gx, along * gx + across * gy));
                }
                double h = Math.Min(step, maximumLength - node.S);
                if (h < surface.CellScale * 1e-7) continue;
                foreach (double heading in headings)
                {
                    foreach (var boundary in surface.BoundaryCandidates(node.X, node.Y, h, z0 + grade * node.S, grade, heading))
                        candidates.Add((node.X + boundary.Length * Math.Cos(boundary.Angle), node.Y + boundary.Length * Math.Sin(boundary.Angle)));
                    if (surface.CorrectAngle(node.X, node.Y, h, z0 + grade * (node.S + h), heading,
                        RootTolerance(z0 + grade * (node.S + h), h), out double corrected, out _))
                        candidates.Add((node.X + h * Math.Cos(corrected), node.Y + h * Math.Sin(corrected)));
                }
                // A second radius improves coverage near tangencies and curved patch interiors.
                foreach (double radius in new[] { h, h * 0.5 })
                    foreach (double angle in surface.CircleRoots(node.X, node.Y, radius, z0 + grade * (node.S + radius),
                        RootTolerance(z0 + grade * (node.S + radius), radius), cancellationToken))
                        candidates.Add((node.X + radius * Math.Cos(angle), node.Y + radius * Math.Sin(angle)));

                foreach (var point in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = Checked(node, point.X, point.Y);
                    if (next == null || next.S + Distance(next.X, next.Y, endX, endY) > maximumLength + numericalLength) continue;
                    var pixel = geo.WorldToPixel(next.X, next.Y);
                    int headingBin = (int)Math.Floor(((next.Heading + 2 * Math.PI) % (2 * Math.PI)) * 16 / Math.PI);
                    var key = ((long)Math.Round(pixel.Column * 8), (long)Math.Round(pixel.Row * 8),
                        grade == 0 ? 0L : (long)Math.Round(next.S / (step * 0.125)), headingBin);
                    double cost = next.S + next.TurnCost;
                    if (visited.TryGetValue(key, out double previous) && previous <= cost) continue;
                    if (stored >= options.MaximumStoredStates) return Failure(NeutralRouteStatus.SearchLimit);
                    visited[key] = cost;
                    stored++;
                    frontier.Push(next, cost + Distance(next.X, next.Y, endX, endY));
                }

                void AddHeading(double heading)
                {
                    foreach (double existing in headings)
                        if (AngleDistance(existing, heading) < 1e-7) return;
                    headings.Add(heading);
                }
            }
            return Failure(NeutralRouteStatus.NoRouteFound);

            double RootTolerance(double target, double length) => Math.Max(32 * 2.220446049250313e-16 * Math.Max(1, Math.Abs(target)),
                Math.Min(options.HeightTolerance * 0.001, length * 1e-8));

            RouteNode? Checked(RouteNode from, double x, double y)
            {
                double length = Distance(from.X, from.Y, x, y);
                if (length < step * 1e-7 || from.S + length > maximumLength + numericalLength) return null;
                var check = surface.Check(from.X, from.Y, x, y, from.S, z0, grade);
                if (check == null || check.HeightError > options.HeightTolerance || check.GradeError * 100 > options.GradeTolerancePercent) return null;
                double heading = Math.Atan2(y - from.Y, x - from.X);
                double turn = from.Parent == null ? 0 : step * 0.1 * (1 - Math.Cos(heading - from.Heading));
                return new RouteNode(x, y, from.S + length, heading, from, check, from.TurnCost + turn);
            }

            RouteNode? ConnectViaEllipse(RouteNode from, double toGoal)
            {
                // With a prescribed remaining length, a two-segment connector's bend lies
                // on an ellipse whose foci are the current point and the exact endpoint.
                // Intersect that ellipse with the global grade surface, then validate both
                // complete legs. This also permits sharp switchbacks without moving endpoints.
                double remaining = required.HasValue ? required.Value - from.S : 0;
                var lengths = required.HasValue ? new[] { remaining } : new[] { toGoal * 1.25, toGoal * 1.5, toGoal * 2 };
                foreach (double length in lengths)
                {
                    if (length <= toGoal + numericalLength || from.S + length > maximumLength + numericalLength) continue;
                    double a = length / 2, focal = toGoal / 2, b = Math.Sqrt(Math.Max(0, (a - focal) * (a + focal)));
                    double cx = (from.X + endX) / 2, cy = (from.Y + endY) / 2;
                    double ux = (endX - from.X) / toGoal, uy = (endY - from.Y) / toGoal;
                    var values = new (bool Valid, double Value, double X, double Y)[129];
                    for (int i = 0; i <= 128; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        values[i] = Evaluate(i * 2 * Math.PI / 128);
                        if (values[i].Valid && Math.Abs(values[i].Value) <= options.HeightTolerance * 0.001)
                        {
                            var result = Connect(values[i].X, values[i].Y);
                            if (result != null) return result;
                        }
                        if (i == 0 || !values[i-1].Valid || !values[i].Valid || values[i-1].Value * values[i].Value >= 0) continue;
                        double left = (i-1) * 2 * Math.PI / 128, right = i * 2 * Math.PI / 128, fl = values[i-1].Value;
                        for (int k = 0; k < 60; k++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            double middle = (left + right) / 2;
                            var value = Evaluate(middle);
                            if (!value.Valid) break;
                            if (Math.Abs(value.Value) <= RootTolerance(z0 + grade * from.S, step))
                            {
                                var result = Connect(value.X, value.Y);
                                if (result != null) return result;
                                break;
                            }
                            if ((fl < 0) == (value.Value < 0)) { left = middle; fl = value.Value; } else right = middle;
                        }
                    }
                    (bool Valid, double Value, double X, double Y) Evaluate(double angle)
                    {
                        double x = cx + a * Math.Cos(angle) * ux - b * Math.Sin(angle) * uy;
                        double y = cy + a * Math.Cos(angle) * uy + b * Math.Sin(angle) * ux;
                        bool valid = surface.At(x, y, out double z, out _, out _);
                        return (valid, z - z0 - grade * (from.S + Distance(from.X, from.Y, x, y)), x, y);
                    }
                    RouteNode? Connect(double x, double y)
                    {
                        var first = Checked(from, x, y);
                        return first == null ? null : Checked(first, endX, endY);
                    }
                }
                return null;
            }

            NeutralRouteResult Complete(RouteNode last)
            {
                var nodes = new Stack<RouteNode>();
                for (var current = last; current.Parent != null; current = current.Parent) nodes.Push(current);
                var samples = new List<ProfileSample> { new ProfileSample(0, startX, startY, (float)z0) };
                double heightError = 0, gradeError = 0;
                while (nodes.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = nodes.Pop();
                    samples.AddRange(node.Edge!.Samples);
                    heightError = Math.Max(heightError, node.Edge.HeightError);
                    gradeError = Math.Max(gradeError, node.Edge.GradeError * 100);
                }
                return new NeutralRouteResult(NeutralRouteStatus.Success, samples.AsReadOnly(), z0, required, expanded, heightError, gradeError);
            }
            NeutralRouteResult Failure(NeutralRouteStatus status) =>
                new NeutralRouteResult(status, Array.Empty<ProfileSample>(), z0, required, expanded);
        }

        private static double Distance(double ax, double ay, double bx, double by) => Math.Sqrt((bx-ax)*(bx-ax)+(by-ay)*(by-ay));

        private sealed class RouteNode
        {
            public RouteNode(double x, double y, double s, double heading, RouteNode? parent, SegmentCheck? edge, double turnCost)
            { X = x; Y = y; S = s; Heading = heading; Parent = parent; Edge = edge; TurnCost = turnCost; }
            public double X { get; }
            public double Y { get; }
            public double S { get; }
            public double Heading { get; }
            public RouteNode? Parent { get; }
            public SegmentCheck? Edge { get; }
            public double TurnCost { get; }
        }

        // Stable binary heap keeps Core portable to netstandard2.0.
        private sealed class RouteHeap
        {
            private readonly List<(RouteNode Node, double Priority, long Sequence)> _items = new List<(RouteNode, double, long)>();
            private long _sequence;
            public int Count => _items.Count;
            public void Push(RouteNode node, double priority)
            {
                var item = (Node: node, Priority: priority, Sequence: _sequence++);
                int index = _items.Count;
                _items.Add(item);
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (!Before(item, _items[parent])) break;
                    _items[index] = _items[parent]; index = parent;
                }
                _items[index] = item;
            }
            public RouteNode Pop()
            {
                var result = _items[0].Node;
                var last = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0) return result;
                int index = 0;
                while (2 * index + 1 < _items.Count)
                {
                    int child = 2 * index + 1;
                    if (child + 1 < _items.Count && Before(_items[child + 1], _items[child])) child++;
                    if (!Before(_items[child], last)) break;
                    _items[index] = _items[child]; index = child;
                }
                _items[index] = last;
                return result;
            }
            private static bool Before((RouteNode Node, double Priority, long Sequence) a, (RouteNode Node, double Priority, long Sequence) b) =>
                a.Priority < b.Priority || (a.Priority == b.Priority && a.Sequence < b.Sequence);
        }
    }
}
