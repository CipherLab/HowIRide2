namespace Transit.Backtest;

/// <summary>
/// Learns how far a predictor misses, as a function of what a rider's app knows when it reads a prediction:
/// the route and the lead time (predicted departure minus now). Applied to Metro Transit's predictions it uses
/// them as a feature and only learns where they're wrong; applied to our own models it calibrates them to the
/// same early-departure budget so they can be compared fairly.
/// <para>
/// Shifting by the median residual minimizes absolute error but sends half of all buses out before the predicted
/// time; on the first day's data the early rate went from ~6% to ~23%. With <c>maxEarlyRate</c> set, each bin's
/// shift is capped so that at most that share of training buses would have left more than 60 s early.
/// </para>
/// </summary>
public sealed class LeadTimeCorrection
{
    /// <summary>Upper edges of the lead-time bins, in minutes; the last bin is open-ended.</summary>
    public static readonly double[] LeadBinEdgesMinutes = [2, 5, 10, 15, 20, 30];

    /// <summary>A route-specific bin needs this many training samples; otherwise the all-routes bin is used.</summary>
    public const int MinRouteSamples = 50;

    /// <summary>An all-routes bin needs this many samples; otherwise no correction is applied in it.</summary>
    public const int MinGlobalSamples = 20;

    readonly double[] _global;
    readonly Dictionary<string, double[]> _byRoute;

    LeadTimeCorrection(double[] global, Dictionary<string, double[]> byRoute, int samples)
    {
        _global = global;
        _byRoute = byRoute;
        TrainingSamples = samples;
    }

    public int TrainingSamples { get; }
    public int RoutesWithOwnCorrection => _byRoute.Count;

    public static int Bin(double leadSeconds)
    {
        var minutes = leadSeconds / 60;
        for (var i = 0; i < LeadBinEdgesMinutes.Length; i++)
            if (minutes < LeadBinEdgesMinutes[i])
                return i;
        return LeadBinEdgesMinutes.Length;
    }

    /// <summary>A bus that leaves more than this before the predicted time has been missed.</summary>
    public const double EarlyThresholdSeconds = 60;

    /// <param name="samples">Residual is actual minus predicted departure; lead is predicted departure minus the time the prediction was read.</param>
    /// <param name="maxEarlyRate">Cap on the training early rate after correction (e.g. 0.05), or null for the plain median shift.</param>
    public static LeadTimeCorrection Fit(IEnumerable<(string RouteId, double LeadSeconds, double Residual)> samples, double? maxEarlyRate = null)
    {
        var bins = LeadBinEdgesMinutes.Length + 1;
        var global = Enumerable.Range(0, bins).Select(_ => new List<double>()).ToArray();
        var byRoute = new Dictionary<string, List<double>[]>();
        var count = 0;
        foreach (var (route, lead, residual) in samples)
        {
            var b = Bin(lead);
            global[b].Add(residual);
            if (!byRoute.TryGetValue(route, out var routeBins))
                byRoute[route] = routeBins = Enumerable.Range(0, bins).Select(_ => new List<double>()).ToArray();
            routeBins[b].Add(residual);
            count++;
        }

        // Corrected prediction p + c counts as early when actual < p + c - 60, i.e. residual < c - 60.
        // Capping c at the maxEarlyRate quantile of the residual plus 60 holds that share to the cap.
        double Shift(List<double> residuals)
        {
            residuals.Sort();
            var median = Quantile(residuals, 0.5);
            return maxEarlyRate is { } rate ? Math.Min(median, Quantile(residuals, rate) + EarlyThresholdSeconds) : median;
        }

        var globalCorrection = global.Select(r => r.Count >= MinGlobalSamples ? Shift(r) : 0).ToArray();
        var routeCorrection = new Dictionary<string, double[]>();
        foreach (var (route, routeBins) in byRoute)
        {
            if (routeBins.All(r => r.Count < MinRouteSamples)) continue;
            routeCorrection[route] = routeBins.Select((r, b) => r.Count >= MinRouteSamples ? Shift(r) : globalCorrection[b]).ToArray();
        }
        return new LeadTimeCorrection(globalCorrection, routeCorrection, count);
    }

    public double Correction(string routeId, double leadSeconds)
    {
        var b = Bin(leadSeconds);
        return _byRoute.TryGetValue(routeId, out var route) ? route[b] : _global[b];
    }

    /// <summary>Training samples from Metro's predictions at every horizon for the given targets.</summary>
    public static IEnumerable<(string RouteId, double LeadSeconds, double Residual)> Samples(
        IReadOnlyList<BacktestTarget> targets, PredictionTable metro, IEnumerable<int> indices)
    {
        foreach (var i in indices)
        {
            var actual = targets[i].Actual.ToUnixTimeMilliseconds() / 1000.0;
            for (var h = 0; h < Horizons.Minutes.Length; h++)
            {
                var predicted = metro[i, h];
                if (double.IsNaN(predicted)) continue;
                var readAt = actual - Horizons.Minutes[h] * 60;
                yield return (targets[i].RouteId, predicted - readAt, actual - predicted);
            }
        }
    }

    /// <summary>Metro's prediction plus the learned correction for its route and lead time at the moment it was read.</summary>
    public PredictionTable Apply(IReadOnlyList<BacktestTarget> targets, PredictionTable metro)
    {
        var corrected = new PredictionTable(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            var actual = targets[i].Actual.ToUnixTimeMilliseconds() / 1000.0;
            for (var h = 0; h < Horizons.Minutes.Length; h++)
            {
                var predicted = metro[i, h];
                if (double.IsNaN(predicted)) continue;
                // The read time is the query instant (the backtest samples it relative to the actual departure,
                // but a live app knows it as "now"); nothing here uses the actual departure itself.
                var readAt = actual - Horizons.Minutes[h] * 60;
                corrected[i, h] = predicted + Correction(targets[i].RouteId, predicted - readAt);
            }
        }
        return corrected;
    }

    /// <summary>Linear-interpolated quantile of an already sorted list.</summary>
    static double Quantile(List<double> sorted, double p)
    {
        var pos = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
    }
}
