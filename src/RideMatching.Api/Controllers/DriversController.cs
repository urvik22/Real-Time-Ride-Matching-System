using Microsoft.AspNetCore.Mvc;
using RideMatching.Api.Contracts;
using RideMatching.Api.Domain;
using RideMatching.Api.Infrastructure;
using RideMatching.Api.Services;

namespace RideMatching.Api.Controllers;

[ApiController]
[Route("api/drivers")]
public sealed class DriversController(IDriverStore drivers, IRideStore rides, IMatchQueue queue) : ControllerBase
{
    [HttpPut("{driverId:guid}/online")]
    [ProducesResponseType<DriverSnapshot>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DriverSnapshot>> GoOnline(Guid driverId, LocationRequest request, CancellationToken token)
    {
        if (driverId == Guid.Empty) return BadRequest();
        var driver = drivers.SetOnline(driverId, request.ToGeoPoint());
        if (driver.Status == DriverStatus.Available) await QueueWaitingRides(token);
        return Ok(driver);
    }

    [HttpPut("{driverId:guid}/location")]
    public async Task<ActionResult<DriverSnapshot>> UpdateLocation(Guid driverId, LocationRequest request, CancellationToken token)
    {
        var driver = drivers.UpdateLocation(driverId, request.ToGeoPoint());
        if (driver is null) return NotFound(new ProblemDetails { Title = "Driver is not online" });
        if (driver.Status == DriverStatus.Available) await QueueWaitingRides(token);
        return Ok(driver);
    }

    [HttpPut("{driverId:guid}/offline")]
    public ActionResult<DriverSnapshot> GoOffline(Guid driverId)
    {
        var current = drivers.Get(driverId);
        if (current is null) return NotFound();
        if (current.Status is DriverStatus.Reserved or DriverStatus.OnTrip)
            return Conflict(new ProblemDetails { Title = "Driver cannot go offline during an active ride" });
        return Ok(drivers.SetOffline(driverId));
    }

    [HttpGet("{driverId:guid}")]
    public ActionResult<DriverSnapshot> Get(Guid driverId) => drivers.Get(driverId) is { } driver ? Ok(driver) : NotFound();

    private async Task QueueWaitingRides(CancellationToken token)
    {
        foreach (var ride in rides.SearchingRides()) await queue.EnqueueAsync(ride.Id, token);
    }
}
