using Microsoft.AspNetCore.Mvc;
using RideMatching.Api.Contracts;
using RideMatching.Api.Domain;
using RideMatching.Api.Hubs;
using RideMatching.Api.Infrastructure;
using RideMatching.Api.Services;

namespace RideMatching.Api.Controllers;

[ApiController]
[Route("api/rides")]
public sealed class RidesController(
    IRideStore rides,
    IMatchQueue queue,
    RideLifecycleService lifecycle,
    IRideNotifier notifier) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<RideSnapshot>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<RideSnapshot>> Create(CreateRideRequest request, CancellationToken token)
    {
        var ride = rides.Create(request.RiderId, request.Pickup.ToGeoPoint(), request.Destination.ToGeoPoint());
        await notifier.RideUpdatedAsync(ride, token);
        await queue.EnqueueAsync(ride.Id, token);
        return AcceptedAtAction(nameof(Get), new { rideId = ride.Id }, ride);
    }

    [HttpGet("{rideId:guid}")]
    public ActionResult<RideSnapshot> Get(Guid rideId) => rides.Get(rideId) is { } ride ? Ok(ride) : NotFound();

    [HttpGet("searching")]
    public ActionResult<IReadOnlyList<RideSnapshot>> GetSearching() => Ok(rides.SearchingRides());

    [HttpPost("{rideId:guid}/cancel")]
    public async Task<ActionResult<RideSnapshot>> Cancel(Guid rideId, CancellationToken token) =>
        ToActionResult(await lifecycle.CancelAsync(rideId, token));

    [HttpPost("{rideId:guid}/start")]
    public async Task<ActionResult<RideSnapshot>> Start(Guid rideId, DriverCommandRequest request, CancellationToken token) =>
        ToActionResult(await lifecycle.StartAsync(rideId, request.DriverId, token));

    [HttpPost("{rideId:guid}/complete")]
    public async Task<ActionResult<RideSnapshot>> Complete(Guid rideId, DriverCommandRequest request, CancellationToken token) =>
        ToActionResult(await lifecycle.CompleteAsync(rideId, request.DriverId, token));

    private ActionResult<RideSnapshot> ToActionResult((CommandResult Result, RideSnapshot? Ride) result) => result.Result switch
    {
        CommandResult.Success => Ok(result.Ride),
        CommandResult.NotFound => NotFound(),
        _ => Conflict(new ProblemDetails { Title = "Invalid ride state transition",
            Detail = $"The ride is currently {result.Ride?.Status}." })
    };
}
