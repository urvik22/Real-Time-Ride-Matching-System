using Microsoft.AspNetCore.SignalR;
using RideMatching.Api.Domain;
using RideMatching.Api.Infrastructure;
using RideMatching.Api.Services;

namespace RideMatching.Api.Hubs;

public sealed class RideHub(IDriverStore drivers, IRideStore rides, IMatchQueue matchQueue) : Hub
{
    public Task SubscribeToRider(Guid riderId) => Groups.AddToGroupAsync(Context.ConnectionId, RiderGroup(riderId));
    public Task SubscribeToDriver(Guid driverId) => Groups.AddToGroupAsync(Context.ConnectionId, DriverGroup(driverId));
    public Task SubscribeToRide(Guid rideId) => Groups.AddToGroupAsync(Context.ConnectionId, RideGroup(rideId));

    /// <summary>
    /// Receives high-frequency driver GPS updates over the duplex SignalR connection.
    /// The update is persisted/cached, waiting rides are reconsidered, and an assigned
    /// rider receives DriverLocationUpdated without polling.
    /// </summary>
    public async Task<DriverSnapshot> UpdateDriverLocation(Guid driverId, double latitude, double longitude)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new HubException("Invalid latitude or longitude.");

        var updated = drivers.UpdateLocation(driverId, new GeoPoint(latitude, longitude));
        if (updated is null)
            throw new HubException("Driver must be online before streaming location updates.");

        foreach (var waitingRide in rides.SearchingRides())
            await matchQueue.EnqueueAsync(waitingRide.Id, Context.ConnectionAborted);

        var groups = new List<string> { DriverGroup(driverId) };
        if (updated.CurrentRideId is { } rideId)
        {
            groups.Add(RideGroup(rideId));
            if (rides.Get(rideId) is { } ride) groups.Add(RiderGroup(ride.RiderId));
        }

        var locationEvent = new DriverLocationEvent(
            driverId, new(latitude, longitude), updated.LastLocationAt!.Value, updated.CurrentRideId);
        await Clients.Groups(groups).SendAsync(
            "DriverLocationUpdated", locationEvent, Context.ConnectionAborted);
        return updated;
    }

    internal static string RiderGroup(Guid id) => $"rider:{id:N}";
    internal static string DriverGroup(Guid id) => $"driver:{id:N}";
    internal static string RideGroup(Guid id) => $"ride:{id:N}";
}

public sealed record DriverLocationEvent(
    Guid DriverId,
    GeoPoint Location,
    DateTimeOffset RecordedAt,
    Guid? RideId);

public interface IRideNotifier
{
    Task RideUpdatedAsync(RideSnapshot ride, CancellationToken cancellationToken);
}

public sealed class SignalRRideNotifier(IHubContext<RideHub> hub) : IRideNotifier
{
    public Task RideUpdatedAsync(RideSnapshot ride, CancellationToken cancellationToken)
    {
        var groups = new List<string> { RideHub.RiderGroup(ride.RiderId), RideHub.RideGroup(ride.Id) };
        if (ride.DriverId is { } driverId) groups.Add(RideHub.DriverGroup(driverId));
        return hub.Clients.Groups(groups).SendAsync("RideUpdated", ride, cancellationToken);
    }
}
