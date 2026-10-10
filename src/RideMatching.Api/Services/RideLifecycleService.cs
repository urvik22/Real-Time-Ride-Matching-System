using RideMatching.Api.Domain;
using RideMatching.Api.Hubs;
using RideMatching.Api.Infrastructure;

namespace RideMatching.Api.Services;

public enum CommandResult { Success, NotFound, Conflict }

public sealed class RideLifecycleService(IRideStore rides, IDriverStore drivers, IRideNotifier notifier)
{
    public async Task<(CommandResult Result, RideSnapshot? Ride)> CancelAsync(Guid rideId, CancellationToken token)
    {
        var current = rides.Get(rideId);
        if (current is null) return (CommandResult.NotFound, null);
        if (current.Status is not (RideStatus.Searching or RideStatus.Matched)) return (CommandResult.Conflict, current);
        if (!rides.TryTransition(rideId, current.Status, RideStatus.Cancelled, out var cancelled))
            return (CommandResult.Conflict, rides.Get(rideId));
        if (cancelled!.DriverId is { } driverId) drivers.TryRelease(driverId, rideId, out _);
        await notifier.RideUpdatedAsync(cancelled, token);
        return (CommandResult.Success, cancelled);
    }

    public async Task<(CommandResult Result, RideSnapshot? Ride)> StartAsync(Guid rideId, Guid driverId, CancellationToken token)
    {
        var current = rides.Get(rideId);
        if (current is null) return (CommandResult.NotFound, null);
        if (current.Status != RideStatus.Matched || current.DriverId != driverId)
            return (CommandResult.Conflict, current);
        if (!drivers.TryStartTrip(driverId, rideId, out _)) return (CommandResult.Conflict, current);
        if (!rides.TryTransition(rideId, RideStatus.Matched, RideStatus.InProgress, out var started))
        {
            drivers.TryRelease(driverId, rideId, out _);
            return (CommandResult.Conflict, rides.Get(rideId));
        }
        await notifier.RideUpdatedAsync(started!, token);
        return (CommandResult.Success, started);
    }

    public async Task<(CommandResult Result, RideSnapshot? Ride)> CompleteAsync(Guid rideId, Guid driverId, CancellationToken token)
    {
        var current = rides.Get(rideId);
        if (current is null) return (CommandResult.NotFound, null);
        if (current.Status != RideStatus.InProgress || current.DriverId != driverId)
            return (CommandResult.Conflict, current);
        if (!rides.TryTransition(rideId, RideStatus.InProgress, RideStatus.Completed, out var completed))
            return (CommandResult.Conflict, rides.Get(rideId));
        drivers.TryRelease(driverId, rideId, out _);
        await notifier.RideUpdatedAsync(completed!, token);
        return (CommandResult.Success, completed);
    }
}
