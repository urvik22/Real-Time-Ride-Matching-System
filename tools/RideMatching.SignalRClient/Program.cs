using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

var baseUrl = args.FirstOrDefault()?.TrimEnd('/') ?? "http://localhost:5073";
var driverId = Guid.NewGuid();
var riderId = Guid.NewGuid();
var matchedRide = new TaskCompletionSource<RideView>(TaskCreationOptions.RunContinuationsAsynchronously);
var seenRideEvents = new ConcurrentDictionary<(Guid RideId, long Version), byte>();
var seenLocationEvents = new ConcurrentDictionary<DateTimeOffset, byte>();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
json.Converters.Add(new JsonStringEnumConverter());

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
await using var hub = new HubConnectionBuilder()
    .WithUrl($"{baseUrl}/hubs/rides")
    .WithAutomaticReconnect()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .Build();

hub.Reconnecting += error =>
{
    Write("HUB", $"Connection lost; reconnecting ({error?.Message})", ConsoleColor.Yellow);
    return Task.CompletedTask;
};
hub.Reconnected += async _ =>
{
    Write("HUB", "Reconnected and restoring subscriptions", ConsoleColor.Yellow);
    await hub.InvokeAsync("SubscribeToRider", riderId);
    await hub.InvokeAsync("SubscribeToDriver", driverId);
};

hub.On<RideView>("RideUpdated", ride =>
{
    if (!seenRideEvents.TryAdd((ride.Id, ride.Version), 0)) return;
    Write("RIDE", $"{ride.Id} -> {ride.Status}; driver={ride.DriverId}", ConsoleColor.Cyan);
    if (ride.Status == "Matched" && ride.DriverId == driverId) matchedRide.TrySetResult(ride);
});
hub.On<DriverLocationView>("DriverLocationUpdated", update =>
{
    if (!seenLocationEvents.TryAdd(update.RecordedAt, 0)) return;
    Write("GPS", $"{update.Location.Latitude:F5}, {update.Location.Longitude:F5}", ConsoleColor.DarkGreen);
});

try
{
    Write("HUB", $"Connecting to {baseUrl}/hubs/rides", ConsoleColor.Yellow);
    await hub.StartAsync();
    await hub.InvokeAsync("SubscribeToRider", riderId);
    await hub.InvokeAsync("SubscribeToDriver", driverId);
    Write("HUB", $"Connected as rider {riderId} and driver {driverId}", ConsoleColor.Green);

    var initialLocation = new Location(19.0760, 72.8777);
    var onlineResponse = await http.PutAsJsonAsync($"api/drivers/{driverId}/online", initialLocation, json);
    await EnsureSuccess(onlineResponse, "put driver online");
    Write("DRIVER", "Online in Mumbai", ConsoleColor.Green);

    var request = new CreateRideRequest(
        riderId,
        new(19.0761, 72.8778),
        new(19.1176, 72.9060));
    var createResponse = await http.PostAsJsonAsync("api/rides", request, json);
    await EnsureSuccess(createResponse, "create ride");
    var created = await createResponse.Content.ReadFromJsonAsync<RideView>(json)
        ?? throw new InvalidOperationException("Ride response was empty.");
    await hub.InvokeAsync("SubscribeToRide", created.Id);
    Write("RIDER", $"Requested ride {created.Id}; waiting for background match", ConsoleColor.Green);

    using var matchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var registration = matchTimeout.Token.Register(() =>
        matchedRide.TrySetCanceled(matchTimeout.Token));
    var matched = await matchedRide.Task;

    for (var step = 1; step <= 5; step++)
    {
        var latitude = initialLocation.Latitude + step * 0.00015;
        var longitude = initialLocation.Longitude + step * 0.00012;
        await hub.InvokeAsync<DriverView>("UpdateDriverLocation", driverId, latitude, longitude);
        Write("DRIVER", $"Streamed location #{step}", ConsoleColor.Magenta);
        await Task.Delay(500);
    }

    var driverCommand = new { driverId };
    var startResponse = await http.PostAsJsonAsync($"api/rides/{matched.Id}/start", driverCommand, json);
    await EnsureSuccess(startResponse, "start ride");
    Write("DRIVER", "Ride started", ConsoleColor.Green);

    var completeResponse = await http.PostAsJsonAsync($"api/rides/{matched.Id}/complete", driverCommand, json);
    await EnsureSuccess(completeResponse, "complete ride");
    Write("DRIVER", "Ride completed; live SignalR demo finished", ConsoleColor.Green);
    await Task.Delay(500); // allow the final hub event to print
}
catch (HttpRequestException exception)
{
    Write("ERROR", $"Cannot reach the API: {exception.Message}", ConsoleColor.Red);
    Environment.ExitCode = 1;
}
catch (OperationCanceledException)
{
    Write("ERROR", "Timed out waiting for this driver to be matched.", ConsoleColor.Red);
    Environment.ExitCode = 1;
}
catch (Exception exception)
{
    Write("ERROR", exception.Message, ConsoleColor.Red);
    Environment.ExitCode = 1;
}

static async Task EnsureSuccess(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode) return;
    var body = await response.Content.ReadAsStringAsync();
    throw new HttpRequestException($"Failed to {operation}: {(int)response.StatusCode} {body}");
}

static void Write(string source, string message, ConsoleColor color)
{
    lock (Console.Out)
    {
        Console.ForegroundColor = color;
        Console.Write($"[{DateTimeOffset.Now:HH:mm:ss}] {source,-6}");
        Console.ResetColor();
        Console.WriteLine($" {message}");
    }
}

internal sealed record Location(double Latitude, double Longitude);
internal sealed record CreateRideRequest(Guid RiderId, Location Pickup, Location Destination);
internal sealed record RideView(Guid Id, string Status, Guid? DriverId, long Version);
internal sealed record DriverView(Guid Id, string Status, Location? Location);
internal sealed record DriverLocationView(Guid DriverId, Location Location, DateTimeOffset RecordedAt, Guid? RideId);
