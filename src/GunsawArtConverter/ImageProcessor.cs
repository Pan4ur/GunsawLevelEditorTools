using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using GunsawImageCode;

namespace GunsawArtConverter;

internal static class ImageProcessor
{
    public static ArtDocument TextColumns(Bitmap source, int columnCount, float worldWidth, out Bitmap preview)
    {
        var sampleWidth = Math.Clamp(columnCount, 8, 2048);
        var verticalDensity = Math.Max(1.0, Math.Min(3.0, 384.0 / sampleWidth));
        var sampleHeight = Math.Max(2,
            (int)Math.Round(source.Height * sampleWidth / (double)source.Width / 3.0 * verticalDensity));
        using var sampled = Resize(source, sampleWidth, sampleHeight, InterpolationMode.HighQualityBilinear);
        var spacingCorrection = 1f + 2f * Math.Clamp((512f - sampleWidth) / 384f, 0f, 1f);
        var columnSpacing = worldWidth / 512f * spacingCorrection;
        var document = new ArtDocument { Mode = "text-columns", UnoptimizedObjectCount = sampleWidth };

        for (var x = 0; x < sampleWidth; x++)
        {
            var text = new StringBuilder(sampleHeight * 18);
            text.Append("<size=2><line-height=93%>");
            var currentColor = string.Empty;
            for (var y = 0; y < sampleHeight; y++)
            {
                var pixel = sampled.GetPixel(x, y);
                var alpha = pixel.A / 255f;
                var red = (int)Math.Round(pixel.R * alpha + 255f * (1f - alpha));
                var green = (int)Math.Round(pixel.G * alpha + 255f * (1f - alpha));
                var blue = (int)Math.Round(pixel.B * alpha + 255f * (1f - alpha));
                var color = $"{red:X2}{green:X2}{blue:X2}";
                if (color != currentColor)
                {
                    if (currentColor.Length != 0)
                        text.Append("</color>");
                    text.Append("<color=#").Append(color).Append('>');
                    currentColor = color;
                }

                text.Append('█');
                if (y + 1 < sampleHeight)
                    text.Append('\n');
            }

            if (currentColor.Length != 0)
                text.Append("</color>");

            document.Objects.Add(new ArtObject
            {
                Kind = ArtObjectKind.InfoScreen,
                X = columnSpacing * (x - (sampleWidth - 1) * 0.5f),
                Y = 0f,
                Width = 0.02f,
                Height = 0.02f,
                Rotation = 0f,
                Text = text.ToString()
            });
        }

        preview = new Bitmap(sampled);
        return document;
    }

    public static ArtDocument Contours(Bitmap source, int sampleWidth, int threshold, int detail, float worldWidth,
        float lineThickness, float smoothing, int minimumLength, int defectFilter, ArtObjectKind lineKind,
        int optimizationStrength, out Bitmap preview)
    {
        var sampleHeight = Math.Max(2, (int)Math.Round(source.Height * sampleWidth / (double)source.Width));
        using var sampled = Resize(source, sampleWidth, sampleHeight, InterpolationMode.HighQualityBilinear);
        var colors = ReadLabColors(sampled, smoothing);
        var paths = TraceColorTransitions(colors, sampleWidth, sampleHeight, threshold)
            .Where(path => PolylineLength(path.Points, path.Closed) >= minimumLength).ToList();
        paths = FilterIsolatedDefects(paths, defectFilter);
        var tolerance = Math.Max(0.05, (11 - detail) * 0.14);
        var unoptimizedObjectCount = CountSimplifiedSegments(paths, tolerance);
        if (optimizationStrength > 0)
        {
            var normalizedStrength = Math.Clamp(optimizationStrength / 100.0, 0.0, 1.0);
            paths = MergeAlignedPaths(paths, 0.6 + Math.Sqrt(normalizedStrength) * 1.5,
                0.75 + Math.Sqrt(normalizedStrength));
        }

        AssignPathComponents(paths);
        var document = new ArtDocument { Mode = "contour", UnoptimizedObjectCount = unoptimizedObjectCount };
        var segmentComponents = new List<int>();
        var scale = worldWidth / sampleWidth;
        var worldHeight = sampleHeight * scale;

        foreach (var path in paths)
        {
            var simplified =
                path.Closed ? SimplifyClosed(path.Points, tolerance) : SimplifyOpen(path.Points, tolerance);
            if (simplified.Count < 2)
                continue;

            if (optimizationStrength > 0)
            {
                var normalizedStrength = Math.Clamp(optimizationStrength / 100.0, 0.0, 1.0);
                var polygonTolerance = 0.15 + Math.Sqrt(normalizedStrength) * 6.0;
                simplified = path.Closed
                    ? SimplifyClosed(simplified, polygonTolerance)
                    : SimplifyOpen(simplified, polygonTolerance);
                simplified = OptimizeCollinearPoints(simplified, path.Closed, 2.0 + optimizationStrength * 0.12);
            }

            var segmentCount = path.Closed ? simplified.Count : simplified.Count - 1;
            for (var i = 0; i < segmentCount; i++)
            {
                var a = simplified[i];
                var b = simplified[(i + 1) % simplified.Count];
                var dx = (b.X - a.X) * scale;
                var dy = -(b.Y - a.Y) * scale;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 0.005)
                    continue;

                document.Objects.Add(new ArtObject
                {
                    Kind = lineKind,
                    X = (float)(((a.X + b.X) * 0.5 / sampleWidth - 0.5) * worldWidth),
                    Y = (float)((0.5 - (a.Y + b.Y) * 0.5 / sampleHeight) * worldHeight),
                    Width = (float)length,
                    Height = lineThickness,
                    Rotation = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI)
                });
                segmentComponents.Add(path.ComponentId);
            }
        }

        if (optimizationStrength >= 75)
            SimplifyLongLines(document, segmentComponents, sampleWidth, sampleHeight, worldWidth, worldHeight);

        preview = RenderContourPreview(document, sampleWidth, sampleHeight, worldWidth, worldHeight);
        return document;
    }

    private static Color PreviewColor(ArtObjectKind kind)
    {
        switch (kind)
        {
            case ArtObjectKind.Tile:
                return Color.FromArgb(20, 24, 28);
            case ArtObjectKind.Background:
                return Color.FromArgb(92, 96, 108);
            case ArtObjectKind.Ice:
                return Color.FromArgb(175, 230, 248);
            default:
                return Color.FromArgb(75, 190, 255);
        }
    }

    private static LabColor[,] ReadLabColors(Bitmap bitmap, float smoothing)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var red = new float[width, height];
        var green = new float[width, height];
        var blue = new float[width, height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            var alpha = color.A / 255f;
            red[x, y] = color.R * alpha + 255f * (1f - alpha);
            green[x, y] = color.G * alpha + 255f * (1f - alpha);
            blue[x, y] = color.B * alpha + 255f * (1f - alpha);
        }

        if (smoothing > 0.001f)
        {
            red = GaussianBlur(red, width, height, smoothing);
            green = GaussianBlur(green, width, height, smoothing);
            blue = GaussianBlur(blue, width, height, smoothing);
        }

        var result = new LabColor[width, height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            result[x, y] = RgbToLab(red[x, y], green[x, y], blue[x, y]);
        return result;
    }

    private static LabColor RgbToLab(float red, float green, float blue)
    {
        static double Linear(double value)
        {
            value /= 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var r = Linear(red);
        var g = Linear(green);
        var b = Linear(blue);
        var x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
        var y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        var z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;

        static double LabFunction(double value) =>
            value > 0.008856 ? Math.Pow(value, 1.0 / 3.0) : 7.787 * value + 16.0 / 116.0;

        var fx = LabFunction(x);
        var fy = LabFunction(y);
        var fz = LabFunction(z);
        return new LabColor((float)(116 * fy - 16), (float)(500 * (fx - fy)), (float)(200 * (fy - fz)));
    }

    private static double ColorDistance(LabColor a, LabColor b)
    {
        var dl = a.L - b.L;
        var da = a.A - b.A;
        var db = a.B - b.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    private static List<ContourPath> TraceColorTransitions(LabColor[,] colors, int width, int height, int sensitivity)
    {
        var edges = new HashSet<GridEdge>();
        var verticalStrength = new double[width + 1, height];
        for (var y = 0; y < height; y++)
        for (var x = 1; x < width; x++)
            verticalStrength[x, y] = ColorDistance(colors[x - 1, y], colors[x, y]);

        for (var y = 0; y < height; y++)
        for (var x = 1; x < width; x++)
        {
            var strength = verticalStrength[x, y];
            var before = x > 1 ? verticalStrength[x - 1, y] : 0;
            var after = x < width - 1 ? verticalStrength[x + 1, y] : 0;
            if (strength >= sensitivity && strength >= before && strength > after)
                edges.Add(new GridEdge(new Point(x, y), new Point(x, y + 1)));
        }

        var horizontalStrength = new double[width, height + 1];
        for (var y = 1; y < height; y++)
        for (var x = 0; x < width; x++)
            horizontalStrength[x, y] = ColorDistance(colors[x, y - 1], colors[x, y]);

        for (var y = 1; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var strength = horizontalStrength[x, y];
            var before = y > 1 ? horizontalStrength[x, y - 1] : 0;
            var after = y < height - 1 ? horizontalStrength[x, y + 1] : 0;
            if (strength >= sensitivity && strength >= before && strength > after)
                edges.Add(new GridEdge(new Point(x, y), new Point(x + 1, y)));
        }

        var adjacency = new Dictionary<Point, List<Point>>();
        foreach (var edge in edges)
        {
            AddNeighbor(adjacency, edge.A, edge.B);
            AddNeighbor(adjacency, edge.B, edge.A);
        }

        var used = new HashSet<GridEdge>();
        var paths = new List<ContourPath>();
        foreach (var pair in adjacency)
        {
            if (pair.Value.Count == 2)
                continue;
            foreach (var neighbor in pair.Value)
            {
                var edge = new GridEdge(pair.Key, neighbor);
                if (!used.Contains(edge))
                    paths.Add(FollowPath(pair.Key, neighbor, adjacency, used));
            }
        }

        foreach (var edge in edges)
            if (!used.Contains(edge))
                paths.Add(FollowPath(edge.A, edge.B, adjacency, used));
        return paths;
    }

    private static void AddNeighbor(Dictionary<Point, List<Point>> adjacency, Point point, Point neighbor)
    {
        if (!adjacency.TryGetValue(point, out var list))
            adjacency[point] = list = new List<Point>();
        list.Add(neighbor);
    }

    private static ContourPath FollowPath(Point start, Point next, Dictionary<Point, List<Point>> adjacency,
        HashSet<GridEdge> used)
    {
        var points = new List<PointF> { new PointF(start.X, start.Y) };
        var previous = start;
        var current = next;
        var closed = false;
        while (true)
        {
            used.Add(new GridEdge(previous, current));
            points.Add(new PointF(current.X, current.Y));
            if (current == start)
            {
                closed = true;
                break;
            }

            if (!adjacency.TryGetValue(current, out var neighbors) || neighbors.Count != 2)
                break;

            Point? following = null;
            foreach (var candidate in neighbors)
            {
                if (!used.Contains(new GridEdge(current, candidate)))
                {
                    following = candidate;
                    break;
                }
            }

            if (!following.HasValue)
                break;
            previous = current;
            current = following.Value;
        }

        if (closed && points.Count > 1)
            points.RemoveAt(points.Count - 1);
        return new ContourPath(points, closed);
    }

    private static double PolylineLength(List<PointF> points, bool closed)
    {
        double length = 0;
        var count = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            length += Math.Sqrt(dx * dx + dy * dy);
        }

        return length;
    }

    private static List<ContourPath> FilterIsolatedDefects(List<ContourPath> paths, int minimumClusterLength)
    {
        if (minimumClusterLength <= 0 || paths.Count <= 1)
            return paths;
        const float joinDistance = 2.25f;
        const int bucketSize = 8;
        var bounds = paths.Select(path => PathBounds.From(path.Points)).ToArray();
        var sets = new DisjointSet(paths.Count);
        var buckets = new Dictionary<long, List<int>>();

        for (var i = 0; i < paths.Count; i++)
        {
            var current = bounds[i];
            var minBucketX = (int)Math.Floor((current.MinX - joinDistance) / bucketSize);
            var maxBucketX = (int)Math.Floor((current.MaxX + joinDistance) / bucketSize);
            var minBucketY = (int)Math.Floor((current.MinY - joinDistance) / bucketSize);
            var maxBucketY = (int)Math.Floor((current.MaxY + joinDistance) / bucketSize);
            var candidates = new HashSet<int>();
            for (var by = minBucketY; by <= maxBucketY; by++)
            for (var bx = minBucketX; bx <= maxBucketX; bx++)
                if (buckets.TryGetValue(BucketKey(bx, by), out var bucket))
                    foreach (var candidate in bucket)
                        candidates.Add(candidate);

            foreach (var candidate in candidates)
                if (current.DistanceSquared(bounds[candidate]) <= joinDistance * joinDistance)
                    sets.Union(i, candidate);

            minBucketX = (int)Math.Floor(current.MinX / bucketSize);
            maxBucketX = (int)Math.Floor(current.MaxX / bucketSize);
            minBucketY = (int)Math.Floor(current.MinY / bucketSize);
            maxBucketY = (int)Math.Floor(current.MaxY / bucketSize);
            for (var by = minBucketY; by <= maxBucketY; by++)
            for (var bx = minBucketX; bx <= maxBucketX; bx++)
            {
                var key = BucketKey(bx, by);
                if (!buckets.TryGetValue(key, out var bucket))
                    buckets[key] = bucket = new List<int>();
                bucket.Add(i);
            }
        }

        var clusterLengths = new Dictionary<int, double>();
        for (var i = 0; i < paths.Count; i++)
        {
            var root = sets.Find(i);
            var length = PolylineLength(paths[i].Points, paths[i].Closed);
            clusterLengths[root] = clusterLengths.TryGetValue(root, out var current) ? current + length : length;
        }

        var result = new List<ContourPath>();
        for (var i = 0; i < paths.Count; i++)
            if (clusterLengths[sets.Find(i)] >= minimumClusterLength)
                result.Add(paths[i]);
        return result;
    }

    private static int CountSimplifiedSegments(List<ContourPath> paths, double tolerance)
    {
        var count = 0;
        foreach (var path in paths)
        {
            var simplified =
                path.Closed ? SimplifyClosed(path.Points, tolerance) : SimplifyOpen(path.Points, tolerance);
            if (simplified.Count < 2)
                continue;
            count += path.Closed ? simplified.Count : simplified.Count - 1;
        }

        return count;
    }

    private static void AssignPathComponents(List<ContourPath> paths)
    {
        var sets = new DisjointSet(paths.Count);
        var owners = new Dictionary<Point, int>();
        for (var i = 0; i < paths.Count; i++)
            foreach (var point in paths[i].Points)
            {
                var key = new Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
                if (owners.TryGetValue(key, out var owner))
                    sets.Union(i, owner);
                else
                    owners[key] = i;
            }

        for (var i = 0; i < paths.Count; i++)
            paths[i].ComponentId = sets.Find(i);
    }

    private static void SimplifyLongLines(ArtDocument document, List<int> components, int sampleWidth, int sampleHeight,
        float worldWidth, float worldHeight)
    {
        const int angleStep = 2;
        const double maximumSegmentLength = 12;
        const double maximumLineDistance = 1.5;
        const double maximumGap = 12;
        const double minimumLineLength = 24;
        var maximumRho = (int)Math.Ceiling(Math.Sqrt(sampleWidth * sampleWidth + sampleHeight * sampleHeight));
        var rhoStride = maximumRho * 2 + 1;
        var entries = new List<LineEntry>();
        for (var i = 0; i < document.Objects.Count; i++)
        {
            var item = document.Objects[i];
            var length = item.Width / worldWidth * sampleWidth;
            if (length > maximumSegmentLength)
                continue;
            var x = (item.X / worldWidth + 0.5f) * sampleWidth;
            var y = (0.5f - item.Y / worldHeight) * sampleHeight;
            var tile = ((int)(x / 128) << 16) | (int)(y / 128);
            entries.Add(new LineEntry(i, tile, x, y, length));
        }

        var accumulator = new Dictionary<long, double>();
        foreach (var entry in entries)
            for (var angle = 0; angle < 180; angle += angleStep)
            {
                if (angle < 45 || angle > 135 || angle > 75 && angle < 105)
                    continue;
                var radians = angle * Math.PI / 180.0;
                var rho = (int)Math.Round(entry.X * Math.Cos(radians) + entry.Y * Math.Sin(radians));
                var packed = angle / angleStep * rhoStride + rho + maximumRho;
                var key = ((long)entry.ComponentId << 32) | (uint)packed;
                accumulator[key] = accumulator.TryGetValue(key, out var weight) ? weight + entry.Length : entry.Length;
            }

        var consumed = new bool[document.Objects.Count];
        var replacements = new List<(ArtObject Object, int Component)>();
        var optimizedTiles = new HashSet<int>();
        foreach (var candidate in accumulator.OrderByDescending(pair => pair.Value).Take(1024))
        {
            var component = (int)(candidate.Key >> 32);
            if (optimizedTiles.Contains(component))
                continue;
            var packed = (int)(candidate.Key & uint.MaxValue);
            var angleIndex = packed / rhoStride;
            var rho = packed % rhoStride - maximumRho;
            var normalDegrees = angleIndex * angleStep;
            if (normalDegrees < 45 || normalDegrees > 135 || normalDegrees > 75 && normalDegrees < 105)
                continue;
            var normalAngle = angleIndex * angleStep * Math.PI / 180.0;
            var normalX = Math.Cos(normalAngle);
            var normalY = Math.Sin(normalAngle);
            var directionX = -normalY;
            var directionY = normalX;
            var supported = new List<(LineEntry Entry, double Projection)>();
            foreach (var entry in entries)
            {
                if (entry.ComponentId != component || consumed[entry.ObjectIndex])
                    continue;
                var distance = Math.Abs(entry.X * normalX + entry.Y * normalY - rho);
                if (distance <= maximumLineDistance)
                    supported.Add((entry, entry.X * directionX + entry.Y * directionY));
            }

            if (supported.Count < 6)
                continue;
            supported.Sort((a, b) => a.Projection.CompareTo(b.Projection));
            var bestStart = 0;
            var bestEnd = 0;
            var currentStart = 0;
            double bestLength = 0;
            for (var i = 1; i <= supported.Count; i++)
            {
                if (i < supported.Count &&
                    supported[i].Projection - supported[i - 1].Projection <= maximumGap)
                    continue;
                var length = supported[i - 1].Projection - supported[currentStart].Projection;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = currentStart;
                    bestEnd = i - 1;
                }

                currentStart = i;
            }

            if (bestLength < minimumLineLength)
                continue;

            var first = supported[bestStart];
            var last = supported[bestEnd];
            var height = 0f;
            for (var i = bestStart; i <= bestEnd; i++)
            {
                consumed[supported[i].Entry.ObjectIndex] = true;
                height = Math.Max(height, document.Objects[supported[i].Entry.ObjectIndex].Height);
            }

            var firstX = normalX * rho + directionX * first.Projection;
            var firstY = normalY * rho + directionY * first.Projection;
            var lastX = normalX * rho + directionX * last.Projection;
            var lastY = normalY * rho + directionY * last.Projection;
            var dx = (lastX - firstX) / sampleWidth * worldWidth;
            var dy = -(lastY - firstY) / sampleHeight * worldHeight;
            replacements.Add((new ArtObject
            {
                Kind = document.Objects[first.Entry.ObjectIndex].Kind,
                X = (float)(((firstX + lastX) * 0.5 / sampleWidth - 0.5) * worldWidth),
                Y = (float)((0.5 - (firstY + lastY) * 0.5 / sampleHeight) * worldHeight),
                Width = (float)Math.Sqrt(dx * dx + dy * dy),
                Height = height,
                Rotation = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI)
            }, component));
            optimizedTiles.Add(component);
        }

        if (replacements.Count == 0)
            return;
        var remaining = new List<ArtObject>(document.Objects.Count);
        var remainingComponents = new List<int>(components.Count);
        for (var i = 0; i < document.Objects.Count; i++)
            if (!consumed[i])
            {
                remaining.Add(document.Objects[i]);
                remainingComponents.Add(components[i]);
            }

        foreach (var replacement in replacements)
        {
            remaining.Add(replacement.Object);
            remainingComponents.Add(replacement.Component);
        }

        document.Objects.Clear();
        document.Objects.AddRange(remaining);
        components.Clear();
        components.AddRange(remainingComponents);
    }

    private static List<ContourPath> MergeAlignedPaths(List<ContourPath> paths, double maximumDeviation,
        double maximumGap)
    {
        var result = new List<ContourPath>(paths);
        while (true)
        {
            var endpoints = new Dictionary<Point, List<PathEndpoint>>();
            for (var i = 0; i < result.Count; i++)
            {
                var path = result[i];
                if (path.Closed || path.Points.Count < 2)
                    continue;
                AddEndpoint(endpoints, path, i, true);
                AddEndpoint(endpoints, path, i, false);
            }

            var merged = new bool[result.Count];
            var replacements = new Dictionary<int, ContourPath>();
            foreach (var candidates in endpoints.Values)
            {
                PathEndpoint? first = null;
                PathEndpoint? second = null;
                var bestDeviation = double.MaxValue;
                for (var i = 0; i < candidates.Count; i++)
                for (var j = i + 1; j < candidates.Count; j++)
                {
                    var endpointA = candidates[i];
                    var endpointB = candidates[j];
                    if (endpointA.PathIndex == endpointB.PathIndex || merged[endpointA.PathIndex] ||
                        merged[endpointB.PathIndex])
                        continue;
                    if (EndpointDistance(endpointA.Position, endpointB.Position) > maximumGap)
                        continue;
                    var joined = JoinPaths(result[endpointA.PathIndex], endpointA.AtStart, result[endpointB.PathIndex],
                        endpointB.AtStart);
                    var deviation = StraightnessDeviation(joined.Points);
                    if (deviation > maximumDeviation || deviation >= bestDeviation)
                        continue;
                    bestDeviation = deviation;
                    first = endpointA;
                    second = endpointB;
                }

                if (!first.HasValue || !second.HasValue)
                    continue;
                var a = first.Value;
                var b = second.Value;
                merged[a.PathIndex] = true;
                merged[b.PathIndex] = true;
                replacements[Math.Min(a.PathIndex, b.PathIndex)] =
                    JoinPaths(result[a.PathIndex], a.AtStart, result[b.PathIndex], b.AtStart);
            }

            if (replacements.Count == 0)
                return result;
            var next = new List<ContourPath>(result.Count - replacements.Count);
            for (var i = 0; i < result.Count; i++)
            {
                if (!merged[i])
                    next.Add(result[i]);
                else if (replacements.TryGetValue(i, out var replacement))
                    next.Add(replacement);
            }

            result = next;
        }
    }

    private static void AddEndpoint(Dictionary<Point, List<PathEndpoint>> endpoints, ContourPath path, int pathIndex,
        bool atStart)
    {
        var endpoint = atStart ? path.Points[0] : path.Points[path.Points.Count - 1];
        var item = new PathEndpoint(pathIndex, atStart, endpoint);
        var bucketX = (int)Math.Floor(endpoint.X / 2f);
        var bucketY = (int)Math.Floor(endpoint.Y / 2f);
        for (var y = bucketY - 1; y <= bucketY + 1; y++)
        for (var x = bucketX - 1; x <= bucketX + 1; x++)
        {
            var point = new Point(x, y);
            if (!endpoints.TryGetValue(point, out var list))
                endpoints[point] = list = new List<PathEndpoint>();
            list.Add(item);
        }
    }

    private static ContourPath JoinPaths(ContourPath first, bool firstAtStart, ContourPath second, bool secondAtStart)
    {
        var left = new List<PointF>(first.Points);
        var right = new List<PointF>(second.Points);
        if (firstAtStart)
            left.Reverse();
        if (!secondAtStart)
            right.Reverse();
        left.AddRange(right.Skip(1));
        return new ContourPath(left, false);
    }

    private static double StraightnessDeviation(List<PointF> points)
    {
        if (points.Count < 3)
            return 0;
        var first = points[0];
        var last = points[points.Count - 1];
        var greatestDistance = 0.0;
        for (var i = 1; i < points.Count - 1; i++)
            greatestDistance = Math.Max(greatestDistance, SegmentDistanceSquared(points[i], first, last));
        return Math.Sqrt(greatestDistance);
    }

    private static double EndpointDistance(PointF first, PointF second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static long BucketKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    private static List<PointF> OptimizeCollinearPoints(List<PointF> points, bool closed, double angleToleranceDegrees)
    {
        var result = new List<PointF>(points);
        if (result.Count < (closed ? 4 : 3))
            return result;
        var cosineLimit = Math.Cos(angleToleranceDegrees * Math.PI / 180.0);
        var changed = true;
        while (changed && result.Count >= (closed ? 4 : 3))
        {
            changed = false;
            var first = closed ? 0 : 1;
            var last = closed ? result.Count : result.Count - 1;
            for (var i = first; i < last; i++)
            {
                var previous = result[(i - 1 + result.Count) % result.Count];
                var current = result[i];
                var next = result[(i + 1) % result.Count];
                var ax = current.X - previous.X;
                var ay = current.Y - previous.Y;
                var bx = next.X - current.X;
                var by = next.Y - current.Y;
                var aLength = Math.Sqrt(ax * ax + ay * ay);
                var bLength = Math.Sqrt(bx * bx + by * by);
                if (aLength < 0.0001 || bLength < 0.0001 || (ax * bx + ay * by) / (aLength * bLength) >= cosineLimit)
                {
                    result.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }

        return result;
    }

    private static List<PointF> SimplifyOpen(List<PointF> points, double tolerance)
    {
        if (points.Count < 3)
            return new List<PointF>(points);
        var keep = new bool[points.Count];
        keep[0] = keep[points.Count - 1] = true;
        SimplifySection(points, 0, points.Count - 1, tolerance * tolerance, keep);
        var result = new List<PointF>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i])
                result.Add(points[i]);
        return result;
    }

    private static float[,] GaussianBlur(float[,] values, int width, int height, float amount)
    {
        var sigma = 0.4 + amount * 0.55;
        var radius = Math.Max(1, (int)Math.Ceiling(sigma * 2.25));
        var kernel = new double[radius * 2 + 1];
        double kernelSum = 0;
        for (var i = -radius; i <= radius; i++)
        {
            var value = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = value;
            kernelSum += value;
        }

        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= kernelSum;

        var horizontal = new float[width, height];
        var result = new float[width, height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            double sum = 0;
            for (var i = -radius; i <= radius; i++)
                sum += values[Math.Clamp(x + i, 0, width - 1), y] * kernel[i + radius];
            horizontal[x, y] = (float)sum;
        }

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            double sum = 0;
            for (var i = -radius; i <= radius; i++)
                sum += horizontal[x, Math.Clamp(y + i, 0, height - 1)] * kernel[i + radius];
            result[x, y] = (float)sum;
        }

        return result;
    }

    private static double PolygonArea(List<PointF> points)
    {
        double area = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return area * 0.5;
    }

    private static Bitmap RenderContourPreview(ArtDocument document, int width, int height, float worldWidth,
        float worldHeight)
    {
        var preview = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(preview);
        graphics.Clear(Color.FromArgb(20, 23, 28));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var tileBrush = new SolidBrush(PreviewColor(ArtObjectKind.Tile));
        using var infoBrush = new SolidBrush(PreviewColor(ArtObjectKind.InfoScreen));
        using var backgroundBrush = new SolidBrush(PreviewColor(ArtObjectKind.Background));
        using var iceBrush = new SolidBrush(PreviewColor(ArtObjectKind.Ice));

        foreach (var item in document.Objects)
        {
            var centerX = (item.X / worldWidth + 0.5f) * width;
            var centerY = (0.5f - item.Y / worldHeight) * height;
            var halfLength = item.Width / worldWidth * width * 0.5f;
            var halfThickness = Math.Max(0.5f, item.Height / worldHeight * height * 0.5f);
            var angle = -item.Rotation * Math.PI / 180.0;
            var ux = (float)Math.Cos(angle);
            var uy = (float)Math.Sin(angle);
            var vx = -uy;
            var vy = ux;
            var polygon = new[]
            {
                new PointF(centerX - ux * halfLength - vx * halfThickness,
                    centerY - uy * halfLength - vy * halfThickness),
                new PointF(centerX + ux * halfLength - vx * halfThickness,
                    centerY + uy * halfLength - vy * halfThickness),
                new PointF(centerX + ux * halfLength + vx * halfThickness,
                    centerY + uy * halfLength + vy * halfThickness),
                new PointF(centerX - ux * halfLength + vx * halfThickness,
                    centerY - uy * halfLength + vy * halfThickness)
            };
            var brush = item.Kind switch
            {
                ArtObjectKind.Tile => tileBrush,
                ArtObjectKind.Background => backgroundBrush,
                ArtObjectKind.Ice => iceBrush,
                _ => infoBrush
            };
            graphics.FillPolygon(brush, polygon);
        }

        return preview;
    }

    private static Bitmap Resize(Bitmap source, int width, int height, InterpolationMode interpolation)
    {
        var result = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = interpolation;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    private static List<List<PointF>> TraceBoundaryLoops(bool[,] filled, int width, int height)
    {
        var edges = new Dictionary<Point, List<Point>>();

        void Add(Point from, Point to)
        {
            if (!edges.TryGetValue(from, out var list))
                edges[from] = list = new List<Point>();
            list.Add(to);
        }

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!filled[x, y])
                continue;
            if (y == 0 || !filled[x, y - 1])
                Add(new Point(x, y), new Point(x + 1, y));
            if (x == width - 1 || !filled[x + 1, y])
                Add(new Point(x + 1, y), new Point(x + 1, y + 1));
            if (y == height - 1 || !filled[x, y + 1])
                Add(new Point(x + 1, y + 1), new Point(x, y + 1));
            if (x == 0 || !filled[x - 1, y])
                Add(new Point(x, y + 1), new Point(x, y));
        }

        var loops = new List<List<PointF>>();
        while (edges.Count > 0)
        {
            using var enumerator = edges.GetEnumerator();
            enumerator.MoveNext();
            var start = enumerator.Current.Key;
            var current = start;
            var loop = new List<PointF>();
            var guard = 0;
            do
            {
                loop.Add(new PointF(current.X, current.Y));
                if (!edges.TryGetValue(current, out var nexts) || nexts.Count == 0)
                    break;
                var next = nexts[nexts.Count - 1];
                nexts.RemoveAt(nexts.Count - 1);
                if (nexts.Count == 0)
                    edges.Remove(current);
                current = next;
            } while (current != start && guard++ < width * height * 8);

            if (current == start && loop.Count >= 4)
                loops.Add(loop);
        }

        return loops;
    }

    private static List<PointF> SimplifyClosed(List<PointF> points, double tolerance)
    {
        if (points.Count < 4)
            return points;
        var open = new List<PointF>(points) { points[0] };
        var keep = new bool[open.Count];
        keep[0] = keep[open.Count - 1] = true;
        SimplifySection(open, 0, open.Count - 1, tolerance * tolerance, keep);
        var result = new List<PointF>();
        for (var i = 0; i < open.Count - 1; i++)
            if (keep[i])
                result.Add(open[i]);
        return result;
    }

    private static void SimplifySection(List<PointF> points, int first, int last, double toleranceSquared, bool[] keep)
    {
        if (last <= first + 1)
            return;
        var a = points[first];
        var b = points[last];
        var bestDistance = -1.0;
        var bestIndex = 0;
        for (var i = first + 1; i < last; i++)
        {
            var distance = SegmentDistanceSquared(points[i], a, b);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestDistance <= toleranceSquared)
            return;
        keep[bestIndex] = true;
        SimplifySection(points, first, bestIndex, toleranceSquared, keep);
        SimplifySection(points, bestIndex, last, toleranceSquared, keep);
    }

    private static double SegmentDistanceSquared(PointF p, PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0)
            return (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y);
        var t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy)));
        var x = a.X + t * dx;
        var y = a.Y + t * dy;
        return (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
    }

    private readonly struct PathBounds
    {
        public readonly float MinX;
        public readonly float MinY;
        public readonly float MaxX;
        public readonly float MaxY;

        private PathBounds(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public static PathBounds From(List<PointF> points)
        {
            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;
            foreach (var point in points)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            return new PathBounds(minX, minY, maxX, maxY);
        }

        public float DistanceSquared(PathBounds other)
        {
            var dx = MaxX < other.MinX ? other.MinX - MaxX : other.MaxX < MinX ? MinX - other.MaxX : 0f;
            var dy = MaxY < other.MinY ? other.MinY - MaxY : other.MaxY < MinY ? MinY - other.MaxY : 0f;
            return dx * dx + dy * dy;
        }
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parents;
        private readonly byte[] _ranks;

        public DisjointSet(int count)
        {
            _parents = new int[count];
            _ranks = new byte[count];
            for (var i = 0; i < count; i++)
                _parents[i] = i;
        }

        public int Find(int value)
        {
            if (_parents[value] != value)
                _parents[value] = Find(_parents[value]);
            return _parents[value];
        }

        public void Union(int first, int second)
        {
            var a = Find(first);
            var b = Find(second);
            if (a == b)
                return;
            if (_ranks[a] < _ranks[b])
                _parents[a] = b;
            else
            {
                _parents[b] = a;
                if (_ranks[a] == _ranks[b])
                    _ranks[a]++;
            }
        }
    }

    private readonly struct LabColor
    {
        public readonly float L;
        public readonly float A;
        public readonly float B;

        public LabColor(float l, float a, float b)
        {
            L = l;
            A = a;
            B = b;
        }
    }

    private readonly struct GridEdge : IEquatable<GridEdge>
    {
        public readonly Point A;
        public readonly Point B;

        public GridEdge(Point first, Point second)
        {
            if (first.X < second.X || first.X == second.X && first.Y <= second.Y)
            {
                A = first;
                B = second;
            }
            else
            {
                A = second;
                B = first;
            }
        }

        public bool Equals(GridEdge other) => A == other.A && B == other.B;
        public override bool Equals(object? obj) => obj is GridEdge other && Equals(other);
        public override int GetHashCode() => unchecked(A.GetHashCode() * 397 ^ B.GetHashCode());
    }

    private sealed class ContourPath
    {
        public List<PointF> Points { get; }
        public bool Closed { get; }
        public int ComponentId { get; set; }

        public ContourPath(List<PointF> points, bool closed)
        {
            Points = points;
            Closed = closed;
        }
    }

    private readonly struct LineEntry
    {
        public readonly int ObjectIndex;
        public readonly int ComponentId;
        public readonly double X;
        public readonly double Y;
        public readonly double Length;

        public LineEntry(int objectIndex, int componentId, double x, double y, double length)
        {
            ObjectIndex = objectIndex;
            ComponentId = componentId;
            X = x;
            Y = y;
            Length = length;
        }
    }

    private readonly struct PathEndpoint
    {
        public readonly int PathIndex;
        public readonly bool AtStart;
        public readonly PointF Position;

        public PathEndpoint(int pathIndex, bool atStart, PointF position)
        {
            PathIndex = pathIndex;
            AtStart = atStart;
            Position = position;
        }
    }
}