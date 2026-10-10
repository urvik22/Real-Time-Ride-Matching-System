namespace RideMatching.Api.Domain;

public sealed record GeoPoint(double Latitude, double Longitude);

public enum DriverStatus
{
    Offline,
    Available,
    Reserved,
    OnTrip
}

public enum RideStatus
{
    Searching,
    Matched,
    InProgress,
    Completed,
    Cancelled
}

public sealed record DriverSnapshot(
    Guid Id,
    DriverStatus Status,
    GeoPoint? Location,
    DateTimeOffset? LastLocationAt,
    Guid? CurrentRideId,
    long Version);

public sealed record RideSnapshot(
    Guid Id,
    Guid RiderId,
    GeoPoint Pickup,
    GeoPoint Destination,
    RideStatus Status,
    Guid? DriverId,
    double? DriverDistanceKm,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    long Version);
