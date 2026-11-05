using Transit.Backtest;

namespace Transit.Tests;

public sealed class BeByMarginsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("beby-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    const double TenMinutes = 600;

    /// <summary>Adds samples so that exactly <paramref name="gone"/> of every 100 riders' buses left at <paramref name="residual"/> and the rest a minute late.</summary>
    static void Add(BeByHistogram h, string route, bool onTrip, double lead, int gone, double residual, int hundreds = 1)
    {
        for (var i = 0; i < hundreds; i++)
        {
            for (var k = 0; k < gone; k++) h.Add(route, onTrip, lead, residual);
            for (var k = gone; k < 100; k++) h.Add(route, onTrip, lead, 60);
        }
    }

    [Fact]
    public void MarginCoversTheEarliestOneInTwentyBuses()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, TenMinutes, gone: 6, residual: -200, hundreds: 3);

        Assert.Equal(200, h.Fit().Margin("R", true, TenMinutes));
    }

    [Fact]
    public void BusesThatRunLateNeedNoMargin()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, TenMinutes, gone: 0, residual: 0, hundreds: 3);

        Assert.Equal(0, h.Fit().Margin("R", true, TenMinutes));
    }

    [Fact]
    public void TripsThatHaveNotStartedLearnTheirOwnMargin()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, TenMinutes, gone: 6, residual: -100, hundreds: 3);
        Add(h, "R", false, TenMinutes, gone: 6, residual: -300, hundreds: 3);
        var margins = h.Fit();

        Assert.Equal(100, margins.Margin("R", true, TenMinutes));
        Assert.Equal(300, margins.Margin("R", false, TenMinutes));
    }

    [Fact]
    public void LongerLeadsLearnTheirOwnMargin()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, 240, gone: 6, residual: -60, hundreds: 3);    // 4 minutes shown
        Add(h, "R", true, 1500, gone: 6, residual: -240, hundreds: 3);  // 25 minutes shown
        var margins = h.Fit();

        Assert.Equal(60, margins.Margin("R", true, 240));
        Assert.Equal(240, margins.Margin("R", true, 1500));
    }

    [Fact]
    public void ALeadLongerThanAnythingLoggedUsesTheLongestThatWas()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, TenMinutes, gone: 6, residual: -150, hundreds: 3);

        Assert.Equal(150, h.Fit().Margin("R", true, 80 * 60));
    }

    [Fact]
    public void ALeadShorterThanAnythingLoggedUsesTheShortestThatWas()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, TenMinutes, gone: 6, residual: -150, hundreds: 3);

        Assert.Equal(150, h.Fit().Margin("R", true, 30));
    }

    [Fact]
    public void AnUnreliableRouteRaisesItsOwnMargin()
    {
        var h = new BeByHistogram();
        Add(h, "steady", true, TenMinutes, gone: 6, residual: -100, hundreds: 20);
        Add(h, "wild", true, TenMinutes, gone: 6, residual: -500, hundreds: 5);
        var margins = h.Fit();

        Assert.Equal(500, margins.Margin("wild", true, TenMinutes));
    }

    [Fact]
    public void ARouteThatLookedReliableNeverGoesBelowTheSharedMargin()
    {
        // One good day isn't evidence that a route is safer than the rest: that's how a rider gets caught out.
        var h = new BeByHistogram();
        Add(h, "typical", true, TenMinutes, gone: 6, residual: -300, hundreds: 30);
        Add(h, "lucky", true, TenMinutes, gone: 6, residual: -40, hundreds: 5);
        var margins = h.Fit();

        Assert.Equal(300, margins.Margin("lucky", true, TenMinutes));
    }

    [Fact]
    public void ARouteWithTooFewSamplesUsesTheSharedMargin()
    {
        var h = new BeByHistogram();
        Add(h, "typical", true, TenMinutes, gone: 6, residual: -100, hundreds: 30);
        for (var i = 0; i < BeByHistogram.MinRouteSamples - 1; i++) h.Add("rare", true, TenMinutes, -900);

        Assert.Equal(100, h.Fit().Margin("rare", true, TenMinutes));
    }

    [Fact]
    public void ARouteNeverSeenUsesTheSharedMargin()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, TenMinutes, gone: 6, residual: -100, hundreds: 3);

        Assert.Equal(100, h.Fit().Margin("new-route", true, TenMinutes));
    }

    [Fact]
    public void WithNoDataThereIsNoMargin() =>
        Assert.Equal(0, new BeByHistogram().Fit().Margin("R", true, TenMinutes));

    [Fact]
    public void BeByIsThePredictionLessTheMargin()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, TenMinutes, gone: 6, residual: -200, hundreds: 3);

        Assert.Equal(1000 + TenMinutes - 200, h.Fit().BeBy("R", true, predicted: 1000 + TenMinutes, now: 1000));
    }

    [Fact]
    public void BeByIsNeverInThePast()
    {
        var h = new BeByHistogram();
        Add(h, "R", true, 240, gone: 6, residual: -400, hundreds: 3);

        Assert.Equal(1000, h.Fit().BeBy("R", true, predicted: 1240, now: 1000));
    }

    [Fact]
    public void MarginsSurviveASaveAndLoad()
    {
        var h = new BeByHistogram();
        Add(h, "wild", false, TenMinutes, gone: 6, residual: -500, hundreds: 3);
        var path = Path.Combine(_dir, "sub", "margins.json");

        h.Fit("20261103", "20261104").Save(path);
        var loaded = BeByMargins.TryLoad(path);

        Assert.NotNull(loaded);
        Assert.Equal(500, loaded.Margin("wild", false, TenMinutes));
        Assert.Equal(("20261103", "20261104", 300L), (loaded.From, loaded.To, loaded.Samples));
    }

    [Fact]
    public void AMissingOrDamagedFileLoadsAsNoMargins()
    {
        var damaged = Path.Combine(_dir, "bad.json");
        File.WriteAllText(damaged, "{ not json");

        Assert.Null(BeByMargins.TryLoad(Path.Combine(_dir, "absent.json")));
        Assert.Null(BeByMargins.TryLoad(damaged));
    }

    // ---- turning the log into samples: what a rider saw, against what the bus did ----

    static readonly IReadOnlyList<LiveTripPrediction> OneStop = [new("20260101", "T1", [(2, 1600)])];

    List<(string Route, bool OnTrip, double Lead, double Residual)> Samples(double now, double actual, double firstKnown = double.PositiveInfinity, IReadOnlyList<LiveTripPrediction>? trips = null)
    {
        var seen = new List<(string, bool, double, double)>();
        BeByFit.Visit(now, trips ?? OneStop, (trip, sequence) => trip == "T1" && sequence == 2 ? ("R", actual) : null, _ => firstKnown,
            (route, onTrip, lead, residual) => seen.Add((route, onTrip, lead, residual)));
        return seen;
    }

    [Fact]
    public void ASampleComparesThePredictionShownWithTheActualDeparture() =>
        Assert.Equal(("R", false, 600.0, -50.0), Assert.Single(Samples(now: 1000, actual: 1550)));

    [Fact]
    public void ABusAlreadyGoneWhenTheRiderLookedTeachesNothing() =>
        Assert.Empty(Samples(now: 1000, actual: 990));

    [Fact]
    public void ADepartureNeverObservedTeachesNothing() =>
        Assert.Empty(Samples(now: 1000, actual: 1550, trips: [new("20260101", "T9", [(2, 1600)])]));

    [Fact]
    public void ATripCountsAsOnTripOnlyOnceItsFirstDepartureWasKnowable()
    {
        Assert.False(Assert.Single(Samples(now: 1000, actual: 1550, firstKnown: 1001)).OnTrip);
        Assert.True(Assert.Single(Samples(now: 1000, actual: 1550, firstKnown: 1000)).OnTrip);
    }
}
