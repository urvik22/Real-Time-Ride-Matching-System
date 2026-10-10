using System.Data;
using Microsoft.Data.SqlClient;
using RideMatching.Api.Domain;

namespace RideMatching.Api.Infrastructure;

public sealed record PersistenceSelection(bool UsesSqlServer, string Provider, string Detail);

public abstract class SqlStore(string connectionString)
{
    protected SqlConnection OpenConnection()
    {
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    protected static void AddGuid(SqlCommand command, string name, Guid value) =>
        command.Parameters.Add(name, SqlDbType.UniqueIdentifier).Value = value;

    protected static long ReadVersion(SqlDataReader reader, int ordinal)
    {
        var bytes = (byte[])reader.GetValue(ordinal);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToInt64(bytes, 0);
    }

    protected static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}

public sealed class SqlDriverStore(string connectionString) : SqlStore(connectionString), IDriverStore
{
    private const string SelectColumns = """
        SELECT Id, Status,
               CASE WHEN CurrentLocation IS NULL THEN NULL ELSE CurrentLocation.Lat END AS Latitude,
               CASE WHEN CurrentLocation IS NULL THEN NULL ELSE CurrentLocation.Long END AS Longitude,
               LocationUpdatedAt, CurrentRideId, Version
        FROM dbo.Drivers
        """;

    public DriverSnapshot SetOnline(Guid driverId, GeoPoint location)
    {
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.usp_SetDriverOnline", connection) { CommandType = CommandType.StoredProcedure };
        AddGuid(command, "@DriverId", driverId);
        command.Parameters.Add("@Latitude", SqlDbType.Float).Value = location.Latitude;
        command.Parameters.Add("@Longitude", SqlDbType.Float).Value = location.Longitude;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new DataException("SQL Server did not return the updated driver.");
        return ReadDriver(reader);
    }

    public DriverSnapshot? SetOffline(Guid driverId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.Drivers SET Status = 0, UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @Id AND Status IN (0, 1);
            """;
        AddGuid(command, "@Id", driverId);
        command.ExecuteNonQuery();
        return Get(driverId);
    }

    public DriverSnapshot? UpdateLocation(Guid driverId, GeoPoint location)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.Drivers
               SET CurrentLocation = geography::Point(@Latitude, @Longitude, 4326),
                   LocationUpdatedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
             WHERE Id = @Id AND Status <> 0;
            """;
        AddGuid(command, "@Id", driverId);
        command.Parameters.Add("@Latitude", SqlDbType.Float).Value = location.Latitude;
        command.Parameters.Add("@Longitude", SqlDbType.Float).Value = location.Longitude;
        return command.ExecuteNonQuery() == 1 ? Get(driverId) : null;
    }

    public DriverSnapshot? Get(Guid driverId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE Id = @Id;";
        AddGuid(command, "@Id", driverId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDriver(reader) : null;
    }

    public IReadOnlyList<DriverSnapshot> AvailableDrivers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE Status = 1 AND CurrentLocation IS NOT NULL;";
        using var reader = command.ExecuteReader();
        var results = new List<DriverSnapshot>();
        while (reader.Read()) results.Add(ReadDriver(reader));
        return results;
    }

    public bool TryReserve(Guid driverId, Guid rideId, out DriverSnapshot? driver) =>
        TryChangeState(driverId, rideId, "Status = 2, CurrentRideId = @RideId", "Status = 1 AND CurrentRideId IS NULL", out driver);

    public bool TryStartTrip(Guid driverId, Guid rideId, out DriverSnapshot? driver) =>
        TryChangeState(driverId, rideId, "Status = 3", "Status = 2 AND CurrentRideId = @RideId", out driver);

    public bool TryRelease(Guid driverId, Guid rideId, out DriverSnapshot? driver) =>
        TryChangeState(driverId, rideId, "Status = 1, CurrentRideId = NULL", "Status IN (2, 3) AND CurrentRideId = @RideId", out driver);

    private bool TryChangeState(Guid driverId, Guid rideId, string changes, string predicate, out DriverSnapshot? driver)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE dbo.Drivers SET {changes}, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id AND {predicate};";
        AddGuid(command, "@Id", driverId);
        AddGuid(command, "@RideId", rideId);
        if (command.ExecuteNonQuery() != 1) { driver = null; return false; }
        driver = Get(driverId);
        return true;
    }

    private static DriverSnapshot ReadDriver(SqlDataReader reader)
    {
        GeoPoint? location = reader.IsDBNull(2) ? null : new(reader.GetDouble(2), reader.GetDouble(3));
        return new(reader.GetGuid(0), (DriverStatus)reader.GetByte(1), location,
            reader.IsDBNull(4) ? null : ReadUtc(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5), ReadVersion(reader, 6));
    }
}

public sealed class SqlRideStore(string connectionString) : SqlStore(connectionString), IRideStore
{
    private const string SelectColumns = """
        SELECT Id, RiderId, PickupLocation.Lat, PickupLocation.Long,
               DestinationLocation.Lat, DestinationLocation.Long, Status, DriverId,
               DriverDistanceKm, RequestedAt, UpdatedAt, Version
        FROM dbo.Rides
        """;

    public RideSnapshot Create(Guid riderId, GeoPoint pickup, GeoPoint destination)
    {
        var rideId = Guid.NewGuid();
        using var connection = OpenConnection();
        using var command = new SqlCommand("dbo.usp_CreateRide", connection) { CommandType = CommandType.StoredProcedure };
        AddGuid(command, "@RideId", rideId);
        AddGuid(command, "@RiderId", riderId);
        command.Parameters.Add("@PickupLatitude", SqlDbType.Float).Value = pickup.Latitude;
        command.Parameters.Add("@PickupLongitude", SqlDbType.Float).Value = pickup.Longitude;
        command.Parameters.Add("@DestinationLatitude", SqlDbType.Float).Value = destination.Latitude;
        command.Parameters.Add("@DestinationLongitude", SqlDbType.Float).Value = destination.Longitude;
        command.ExecuteNonQuery();
        return Get(rideId) ?? throw new DataException("SQL Server did not persist the new ride.");
    }

    public RideSnapshot? Get(Guid rideId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE Id = @Id;";
        AddGuid(command, "@Id", rideId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRide(reader) : null;
    }

    public IReadOnlyList<RideSnapshot> SearchingRides()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE Status = 0 ORDER BY RequestedAt;";
        using var reader = command.ExecuteReader();
        var results = new List<RideSnapshot>();
        while (reader.Read()) results.Add(ReadRide(reader));
        return results;
    }

    public bool TryAssign(Guid rideId, Guid driverId, double distanceKm, out RideSnapshot? ride)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.Rides
               SET Status = 1, DriverId = @DriverId, DriverDistanceKm = @DistanceKm,
                   UpdatedAt = SYSUTCDATETIME()
             WHERE Id = @Id AND Status = 0;
            """;
        AddGuid(command, "@Id", rideId);
        AddGuid(command, "@DriverId", driverId);
        var distanceParameter = command.Parameters.Add("@DistanceKm", SqlDbType.Decimal);
        distanceParameter.Precision = 9;
        distanceParameter.Scale = 3;
        distanceParameter.Value = Math.Round(distanceKm, 3);
        if (command.ExecuteNonQuery() != 1) { ride = null; return false; }
        ride = Get(rideId);
        return true;
    }

    public bool TryTransition(Guid rideId, RideStatus expected, RideStatus next, out RideSnapshot? ride)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dbo.Rides
               SET Status = @Next, UpdatedAt = SYSUTCDATETIME(),
                   StartedAt = CASE WHEN @Next = 2 THEN SYSUTCDATETIME() ELSE StartedAt END,
                   CompletedAt = CASE WHEN @Next = 3 THEN SYSUTCDATETIME() ELSE CompletedAt END,
                   CancelledAt = CASE WHEN @Next = 4 THEN SYSUTCDATETIME() ELSE CancelledAt END
             WHERE Id = @Id AND Status = @Expected;
            """;
        AddGuid(command, "@Id", rideId);
        command.Parameters.Add("@Expected", SqlDbType.TinyInt).Value = (byte)expected;
        command.Parameters.Add("@Next", SqlDbType.TinyInt).Value = (byte)next;
        if (command.ExecuteNonQuery() != 1) { ride = null; return false; }
        ride = Get(rideId);
        return true;
    }

    private static RideSnapshot ReadRide(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), new(reader.GetDouble(2), reader.GetDouble(3)),
        new(reader.GetDouble(4), reader.GetDouble(5)), (RideStatus)reader.GetByte(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : (double)reader.GetDecimal(8),
        ReadUtc(reader, 9), ReadUtc(reader, 10), ReadVersion(reader, 11));
}
