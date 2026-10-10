using System.ComponentModel.DataAnnotations;
using RideMatching.Api.Domain;

namespace RideMatching.Api.Contracts;

public sealed class LocationRequest : IValidatableObject
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Latitude is < -90 or > 90)
            yield return new("Latitude must be between -90 and 90.", [nameof(Latitude)]);
        if (Longitude is < -180 or > 180)
            yield return new("Longitude must be between -180 and 180.", [nameof(Longitude)]);
    }

    public GeoPoint ToGeoPoint() => new(Latitude, Longitude);
}

public sealed class CreateRideRequest : IValidatableObject
{
    public Guid RiderId { get; init; }
    [Required] public LocationRequest Pickup { get; init; } = null!;
    [Required] public LocationRequest Destination { get; init; } = null!;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RiderId == Guid.Empty)
            yield return new("RiderId is required.", [nameof(RiderId)]);
        if (Pickup is not null && Destination is not null &&
            Pickup.Latitude == Destination.Latitude && Pickup.Longitude == Destination.Longitude)
            yield return new("Pickup and destination must differ.", [nameof(Destination)]);
    }
}

public sealed class DriverCommandRequest
{
    public Guid DriverId { get; init; }
}
