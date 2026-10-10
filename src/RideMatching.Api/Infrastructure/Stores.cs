using System.Collections.Concurrent;
using RideMatching.Api.Domain;

namespace RideMatching.Api.Infrastructure;

public interface IDriverStore
{
    DriverSnapshot SetOnline(Guid driverId, GeoPoint location);
    DriverSnapshot? SetOffline(Guid driverId);
    DriverSnapshot? UpdateLocation(Guid driverId, GeoPoint location);
    DriverSnapshot? Get(Guid driverId);
    IReadOnlyList<DriverSnapshot> AvailableDrivers();
    bool TryReserve(Guid driverId, Guid rideId, out DriverSnapshot? driver);
    bool TryStartTrip(Guid driverId, Guid rideId, out DriverSnapshot? driver);
    bool TryRelease(Guid driverId, Guid rideId, out DriverSnapshot? driver);
}

public interface IRideStore
{
    RideSnapshot Create(Guid riderId, GeoPoint pickup, GeoPoint destination);
    RideSnapshot? Get(Guid rideId);
    IReadOnlyList<RideSnapshot> SearchingRides();
    bool TryAssign(Guid rideId, Guid driverId, double distanceKm, out RideSnapshot? ride);
    bool TryTransition(Guid rideId, RideStatus expected, RideStatus next, out RideSnapshot? ride);
}

public sealed class InMemoryDriverStore : IDriverStore
{
    private sealed class Entry
    {
        public required Guid Id { get; init; }
        public DriverStatus Status { get; set; }
        public GeoPoint? Location { get; set; }
        public DateTimeOffset? LastLocationAt { get; set; }
        public Guid? CurrentRideId { get; set; }
        public long Version { get; set; }
        public DriverSnapshot Snapshot() => new(Id, Status, Location, LastLocationAt, CurrentRideId, Version);
    }

    private readonly ConcurrentDictionary<Guid, Entry> _drivers = new();

    public DriverSnapshot SetOnline(Guid driverId, GeoPoint location)
    {
        var entry = _drivers.GetOrAdd(driverId, id => new Entry { Id = id });
        lock (entry)
        {
            if (entry.Status is DriverStatus.Reserved or DriverStatus.OnTrip)
                return entry.Snapshot();
            entry.Status = DriverStatus.Available;
            entry.Location = location;
            entry.LastLocationAt = DateTimeOffset.UtcNow;
            entry.Version++;
            return entry.Snapshot();
        }
    }

    public DriverSnapshot? SetOffline(Guid driverId)
    {
        if (!_drivers.TryGetValue(driverId, out var entry)) return null;
        lock (entry)
        {
            if (entry.Status is DriverStatus.Reserved or DriverStatus.OnTrip) return entry.Snapshot();
            entry.Status = DriverStatus.Offline;
            entry.Version++;
            return entry.Snapshot();
        }
    }

    public DriverSnapshot? UpdateLocation(Guid driverId, GeoPoint location)
    {
        if (!_drivers.TryGetValue(driverId, out var entry)) return null;
        lock (entry)
        {
            if (entry.Status == DriverStatus.Offline) return null;
            entry.Location = location;
            entry.LastLocationAt = DateTimeOffset.UtcNow;
            entry.Version++;
            return entry.Snapshot();
        }
    }

    public DriverSnapshot? Get(Guid driverId)
    {
        if (!_drivers.TryGetValue(driverId, out var entry)) return null;
        lock (entry) return entry.Snapshot();
    }

    public IReadOnlyList<DriverSnapshot> AvailableDrivers() => _drivers.Values
        .Select(entry => { lock (entry) return entry.Snapshot(); })
        .Where(driver => driver.Status == DriverStatus.Available && driver.Location is not null)
        .ToArray();

    public bool TryReserve(Guid driverId, Guid rideId, out DriverSnapshot? driver)
    {
        driver = null;
        if (!_drivers.TryGetValue(driverId, out var entry)) return false;
        lock (entry)
        {
            if (entry.Status != DriverStatus.Available) return false;
            entry.Status = DriverStatus.Reserved;
            entry.CurrentRideId = rideId;
            entry.Version++;
            driver = entry.Snapshot();
            return true;
        }
    }

    public bool TryStartTrip(Guid driverId, Guid rideId, out DriverSnapshot? driver)
    {
        driver = null;
        if (!_drivers.TryGetValue(driverId, out var entry)) return false;
        lock (entry)
        {
            if (entry.Status != DriverStatus.Reserved || entry.CurrentRideId != rideId) return false;
            entry.Status = DriverStatus.OnTrip;
            entry.Version++;
            driver = entry.Snapshot();
            return true;
        }
    }

    public bool TryRelease(Guid driverId, Guid rideId, out DriverSnapshot? driver)
    {
        driver = null;
        if (!_drivers.TryGetValue(driverId, out var entry)) return false;
        lock (entry)
        {
            if (entry.CurrentRideId != rideId) return false;
            entry.Status = DriverStatus.Available;
            entry.CurrentRideId = null;
            entry.Version++;
            driver = entry.Snapshot();
            return true;
        }
    }
}

public sealed class InMemoryRideStore : IRideStore
{
    private sealed class Entry
    {
        public required Guid Id { get; init; }
        public required Guid RiderId { get; init; }
        public required GeoPoint Pickup { get; init; }
        public required GeoPoint Destination { get; init; }
        public RideStatus Status { get; set; }
        public Guid? DriverId { get; set; }
        public double? DriverDistanceKm { get; set; }
        public required DateTimeOffset RequestedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public long Version { get; set; }
        public RideSnapshot Snapshot() => new(Id, RiderId, Pickup, Destination, Status, DriverId,
            DriverDistanceKm, RequestedAt, UpdatedAt, Version);
    }

    private readonly ConcurrentDictionary<Guid, Entry> _rides = new();

    public RideSnapshot Create(Guid riderId, GeoPoint pickup, GeoPoint destination)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new Entry { Id = Guid.NewGuid(), RiderId = riderId, Pickup = pickup,
            Destination = destination, Status = RideStatus.Searching, RequestedAt = now, UpdatedAt = now };
        _rides[entry.Id] = entry;
        return entry.Snapshot();
    }

    public RideSnapshot? Get(Guid rideId)
    {
        if (!_rides.TryGetValue(rideId, out var entry)) return null;
        lock (entry) return entry.Snapshot();
    }

    public IReadOnlyList<RideSnapshot> SearchingRides() => _rides.Values
        .Select(entry => { lock (entry) return entry.Snapshot(); })
        .Where(ride => ride.Status == RideStatus.Searching)
        .OrderBy(ride => ride.RequestedAt)
        .ToArray();

    public bool TryAssign(Guid rideId, Guid driverId, double distanceKm, out RideSnapshot? ride)
    {
        ride = null;
        if (!_rides.TryGetValue(rideId, out var entry)) return false;
        lock (entry)
        {
            if (entry.Status != RideStatus.Searching) return false;
            entry.Status = RideStatus.Matched;
            entry.DriverId = driverId;
            entry.DriverDistanceKm = Math.Round(distanceKm, 3);
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.Version++;
            ride = entry.Snapshot();
            return true;
        }
    }

    public bool TryTransition(Guid rideId, RideStatus expected, RideStatus next, out RideSnapshot? ride)
    {
        ride = null;
        if (!_rides.TryGetValue(rideId, out var entry)) return false;
        lock (entry)
        {
            if (entry.Status != expected) return false;
            entry.Status = next;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.Version++;
            ride = entry.Snapshot();
            return true;
        }
    }
}
