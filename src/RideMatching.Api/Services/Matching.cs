using System.Collections.Concurrent;
using System.Threading.Channels;
using RideMatching.Api.Domain;
using RideMatching.Api.Hubs;
using RideMatching.Api.Infrastructure;

namespace RideMatching.Api.Services;

public interface IMatchQueue
{
    ValueTask EnqueueAsync(Guid rideId, CancellationToken cancellationToken = default);
    ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class MatchQueue : IMatchQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    public ValueTask EnqueueAsync(Guid rideId, CancellationToken cancellationToken = default)
    {
        if (!_queued.TryAdd(rideId, 0)) return ValueTask.CompletedTask;
        return WriteAsync(rideId, cancellationToken);
    }

    private async ValueTask WriteAsync(Guid rideId, CancellationToken cancellationToken)
    {
        try { await _channel.Writer.WriteAsync(rideId, cancellationToken); }
        catch { _queued.TryRemove(rideId, out _); throw; }
    }

    public async ValueTask<Guid> DequeueAsync(CancellationToken cancellationToken)
    {
        var rideId = await _channel.Reader.ReadAsync(cancellationToken);
        _queued.TryRemove(rideId, out _);
        return rideId;
    }
}

public sealed class MatchingService(IDriverStore drivers, IRideStore rides, IRideNotifier notifier)
{
    public async Task<RideSnapshot?> TryMatchAsync(Guid rideId, CancellationToken cancellationToken)
    {
        var ride = rides.Get(rideId);
        if (ride is null || ride.Status != RideStatus.Searching) return ride;

        var candidates = drivers.AvailableDrivers()
            .Select(driver => (Driver: driver, Distance: GeoDistance.Kilometres(ride.Pickup, driver.Location!)))
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Driver.Id);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!drivers.TryReserve(candidate.Driver.Id, rideId, out _)) continue;
            if (rides.TryAssign(rideId, candidate.Driver.Id, candidate.Distance, out var matched))
            {
                await notifier.RideUpdatedAsync(matched!, cancellationToken);
                return matched;
            }

            drivers.TryRelease(candidate.Driver.Id, rideId, out _);
            return rides.Get(rideId);
        }

        return ride;
    }
}

public sealed class MatchingWorker(
    IMatchQueue queue,
    MatchingService matching,
    ILogger<MatchingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rideId = await queue.DequeueAsync(stoppingToken);
                await matching.TryMatchAsync(rideId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process a ride matching job");
            }
        }
    }
}

public static class GeoDistance
{
    private const double EarthRadiusKm = 6371.0088;

    public static double Kilometres(GeoPoint first, GeoPoint second)
    {
        var lat1 = DegreesToRadians(first.Latitude);
        var lat2 = DegreesToRadians(second.Latitude);
        var latitudeDelta = DegreesToRadians(second.Latitude - first.Latitude);
        var longitudeDelta = DegreesToRadians(second.Longitude - first.Longitude);
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}
