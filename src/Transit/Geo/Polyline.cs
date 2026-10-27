namespace Transit.Geo;

/// <summary>
/// Equirectangular projection to meters about a fixed origin. Scale error stays under 0.5%
/// across a metro area, well below GPS noise, and keeps projection math to a multiply.
/// </summary>
public sealed class LocalProjection(double originLat, double originLon)
{
    const double EarthRadius = 6371008.8;
    const double DegToRad = Math.PI / 180;
    readonly double _metersPerDegLon = EarthRadius * DegToRad * Math.Cos(originLat * DegToRad);
    readonly double _metersPerDegLat = EarthRadius * DegToRad;

    public (double X, double Y) Project(double lat, double lon) =>
        ((lon - originLon) * _metersPerDegLon, (lat - originLat) * _metersPerDegLat);
}

/// <summary>A point's nearest position on a polyline: distance along it, and perpendicular offset from it.</summary>
public readonly record struct LineMatch(double Distance, double Offset);

/// <summary>
/// A route shape in local meters. The distance coordinate is GTFS shape_dist_traveled, so matched
/// distances line up exactly with the stop distances in stop_times.txt.
/// </summary>
public sealed class Polyline
{
    readonly double[] _x, _y, _dist;

    public Polyline(double[] x, double[] y, double[] dist)
    {
        if (x.Length != y.Length || x.Length != dist.Length)
            throw new ArgumentException("Coordinate arrays must be the same length.");
        if (x.Length < 2)
            throw new ArgumentException("A polyline needs at least two points.");
        for (var i = 1; i < dist.Length; i++)
            if (dist[i] < dist[i - 1])
                throw new ArgumentException($"Distance decreases at point {i}.");
        (_x, _y, _dist) = (x, y, dist);
    }

    public double Length => _dist[^1];

    /// <summary>Distance coordinate of each point (GTFS shape_dist_traveled).</summary>
    public double[] Distances => _dist;

    /// <summary>
    /// Nearest point on the line, considering only the part between <paramref name="minDist"/> and
    /// <paramref name="maxDist"/>. Restricting the window is what stops a ping from snapping to the
    /// other leg of a loop or an overlapping street.
    /// </summary>
    public LineMatch? Match(double x, double y, double minDist = double.NegativeInfinity, double maxDist = double.PositiveInfinity)
    {
        var start = Array.BinarySearch(_dist, Math.Max(minDist, _dist[0]));
        start = Math.Max(0, (start < 0 ? ~start : start) - 1);

        LineMatch? best = null;
        for (var i = start; i < _dist.Length - 1 && _dist[i] <= maxDist; i++)
        {
            var (d0, d1) = (_dist[i], _dist[i + 1]);
            if (d1 < minDist) continue;

            var dx = _x[i + 1] - _x[i];
            var dy = _y[i + 1] - _y[i];
            var lengthSq = dx * dx + dy * dy;
            var f = lengthSq == 0 ? 0 : ((x - _x[i]) * dx + (y - _y[i]) * dy) / lengthSq;

            // Keep the foot of the perpendicular inside both the segment and the window.
            var span = d1 - d0;
            var fMin = span > 0 ? Math.Clamp((minDist - d0) / span, 0, 1) : 0;
            var fMax = span > 0 ? Math.Clamp((maxDist - d0) / span, 0, 1) : 1;
            f = Math.Clamp(f, fMin, fMax);

            var px = _x[i] + f * dx - x;
            var py = _y[i] + f * dy - y;
            var offset = Math.Sqrt(px * px + py * py);
            if (best is null || offset < best.Value.Offset)
                best = new LineMatch(d0 + f * span, offset);
        }
        return best;
    }
}
