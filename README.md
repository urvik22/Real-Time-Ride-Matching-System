# Real-Time Ride Matching System

A production-minded ASP.NET Core reference backend that matches riders to the nearest available driver, processes matching asynchronously, and pushes ride changes over SignalR.

## Run locally

Prerequisite: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet restore
dotnet run --project src/RideMatching.Api
```

Open `http://localhost:5073/swagger`, or run the requests in `RideMatching.http`. The service health endpoint is `GET /health`.

Run the automated tests with:

```powershell
dotnet test
```

## SignalR mobile-client simulation (bonus)

`tools/RideMatching.SignalRClient` is a small .NET client that demonstrates the same connection pattern a .NET MAUI rider/driver app uses. It:

1. opens a persistent SignalR connection and subscribes to rider, driver, and ride groups;
2. puts a driver online and submits a rider request;
3. receives the asynchronous `RideUpdated` match event;
4. streams five GPS coordinates through the duplex hub method `UpdateDriverLocation`;
5. receives `DriverLocationUpdated` events and completes the ride;
6. automatically reconnects and restores its subscriptions after a connection interruption.

Start the API in one terminal, then run the client in another:

```powershell
dotnet run --project src/RideMatching.Api
dotnet run --project tools/RideMatching.SignalRClient
```

To target another API URL:

```powershell
dotnet run --project tools/RideMatching.SignalRClient -- http://192.168.1.10:5073
```

The reusable MAUI integration points are `HubConnectionBuilder`, the three subscription calls, the `RideUpdated`/`DriverLocationUpdated` handlers, and `UpdateDriverLocation`. A production mobile app would call that hub method from its platform geolocation callback and bind received records to view models.

## Example flow

1. `PUT /api/drivers/{driverId}/online` with a location.
2. `PUT /api/drivers/{driverId}/location` as the driver moves.
3. `POST /api/rides` with rider, pickup, and destination. The API returns `202 Accepted` immediately.
4. The background worker finds and atomically reserves the nearest available driver.
5. `GET /api/rides/{rideId}` returns the latest state.
6. The assigned driver calls `/start` and `/complete`.

Swagger is the interactive API collection. The raw OpenAPI document is available at `http://localhost:5073/swagger/v1/swagger.json`. `RideMatching.http` is a portable alternative for Rider, Visual Studio, or VS Code REST Client, and `docs/RideMatching.postman_collection.json` can be imported directly into Postman.

## SQL Server scripts

Production-oriented MSSQL scripts are included under `database/`:

- `000_create_database.sql` creates the configured database.
- `001_schema.sql` creates Rider, Driver, Ride, status-history, and transactional-outbox tables.
- The driver location uses SQL Server `geography` with SRID 4326 and a spatial index.
- `dbo.usp_TryMatchRide` uses a short transaction plus `UPDLOCK`, `READPAST`, and conditional updates so concurrent workers cannot assign the same driver.
- `002_seed_demo.sql` inserts deterministic sample data and runs a match.
- `setup_and_seed.sql` runs all of the above in one execution.

Run the following from the repository root to create and preload the database using Windows authentication:

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i database/setup_and_seed.sql
```

The API then uses SQL Server automatically. It probes the configured connection and required schema at startup; if either is unavailable, it logs the reason and safely uses the in-memory adapters. Inspect the active choice with `GET /api/system/storage`. See `database/README.md` for full setup and connection-string instructions.

## Architecture

```text
HTTP / SignalR clients
        |
ASP.NET Core API ---- RideHub (RideUpdated events)
        |
  validate + enqueue
        |
 Channel match queue -> BackgroundService -> MatchingService
                                           /              \
                                 thread-safe rides   thread-safe drivers
```

- **Near-real-time:** every ride state change emits a SignalR `RideUpdated` event. Clients connect to `/hubs/rides` and invoke `SubscribeToRider`, `SubscribeToDriver`, or `SubscribeToRide`.
- **Async processing:** ride creation does not perform matching on the request thread. An in-process `Channel<Guid>` feeds a hosted worker and deduplicates pending jobs.
- **Concurrency:** per-entity locks and compare-and-transition operations make driver reservation atomic. Two concurrent rides cannot claim one driver. Failed ride assignment compensates by releasing the reservation.
- **Proximity:** candidates are ranked with the Haversine great-circle distance. This is appropriate for the small in-memory data set and hidden behind store/service boundaries.
- **API semantics:** resource-oriented routes, `202 Accepted` for queued work, explicit state-transition commands, validation responses, conflict responses, a health check, and OpenAPI.

## State model

```text
Driver: Offline -> Available -> Reserved -> OnTrip -> Available
Ride:   Searching -> Matched -> InProgress -> Completed
          |            |
          +----------> Cancelled
```

## Test coverage

The test project verifies nearest-driver selection, Haversine distance, full ride lifecycle, and the key race: concurrent ride requests cannot reserve the same driver.
