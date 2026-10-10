using RideMatching.Api.Domain;
using RideMatching.Api.Hubs;
using RideMatching.Api.Infrastructure;
using RideMatching.Api.Services;
using Xunit;

namespace RideMatching.Tests;

public sealed class MatchingServiceTests
{
    [Fact]
    public async Task Match_selects_nearest_available_driver()
    {
        var drivers = new InMemoryDriverStore();
        var rides = new InMemoryRideStore();
        var far = Guid.NewGuid();
        var near = Guid.NewGuid();
        drivers.SetOnline(far, new GeoPoint(12.9716, 77.6946));
        drivers.SetOnline(near, new GeoPoint(12.9717, 77.5947));
        var ride = rides.Create(Guid.NewGuid(), new GeoPoint(12.9716, 77.5946), new GeoPoint(12.9352, 77.6245));

        var result = await new MatchingService(drivers, rides, new NullNotifier())
            .TryMatchAsync(ride.Id, CancellationToken.None);

        Assert.Equal(RideStatus.Matched, result!.Status);
        Assert.Equal(near, result.DriverId);
        Assert.Equal(DriverStatus.Reserved, drivers.Get(near)!.Status);
        Assert.Equal(DriverStatus.Available, drivers.Get(far)!.Status);
    }

    [Fact]
    public async Task Concurrent_rides_cannot_reserve_the_same_driver()
    {
        var drivers = new InMemoryDriverStore();
        var rides = new InMemoryRideStore();
        var driverId = Guid.NewGuid();
        drivers.SetOnline(driverId, new GeoPoint(12.9716, 77.5946));
        var first = rides.Create(Guid.NewGuid(), new GeoPoint(12.97, 77.59), new GeoPoint(13, 77.6));
        var second = rides.Create(Guid.NewGuid(), new GeoPoint(12.97, 77.59), new GeoPoint(13, 77.6));
        var service = new MatchingService(drivers, rides, new NullNotifier());

        var results = await Task.WhenAll(
            service.TryMatchAsync(first.Id, CancellationToken.None),
            service.TryMatchAsync(second.Id, CancellationToken.None));

        Assert.Single(results, ride => ride!.Status == RideStatus.Matched);
        Assert.Single(results, ride => ride!.Status == RideStatus.Searching);
    }

    [Fact]
    public async Task Completing_a_ride_makes_driver_available_again()
    {
        var drivers = new InMemoryDriverStore();
        var rides = new InMemoryRideStore();
        var notifier = new NullNotifier();
        var driverId = Guid.NewGuid();
        drivers.SetOnline(driverId, new GeoPoint(12.97, 77.59));
        var ride = rides.Create(Guid.NewGuid(), new GeoPoint(12.97, 77.59), new GeoPoint(13, 77.6));
        await new MatchingService(drivers, rides, notifier).TryMatchAsync(ride.Id, CancellationToken.None);
        var lifecycle = new RideLifecycleService(rides, drivers, notifier);

        var started = await lifecycle.StartAsync(ride.Id, driverId, CancellationToken.None);
        var completed = await lifecycle.CompleteAsync(ride.Id, driverId, CancellationToken.None);

        Assert.Equal(CommandResult.Success, started.Result);
        Assert.Equal(RideStatus.Completed, completed.Ride!.Status);
        Assert.Equal(DriverStatus.Available, drivers.Get(driverId)!.Status);
    }

    [Fact]
    public void Haversine_returns_expected_distance()
    {
        var distance = GeoDistance.Kilometres(new GeoPoint(12.9716, 77.5946), new GeoPoint(13.0827, 80.2707));
        Assert.InRange(distance, 289, 292);
    }

    private sealed class NullNotifier : IRideNotifier
    {
        public Task RideUpdatedAsync(RideSnapshot ride, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
