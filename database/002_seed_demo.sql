/* Optional deterministic demo data. Run after 001_schema.sql. */
SET XACT_ABORT ON;
GO

DECLARE @DriverOne uniqueidentifier = '11111111-1111-1111-1111-111111111111';
DECLARE @DriverTwo uniqueidentifier = '11111111-1111-1111-1111-222222222222';
DECLARE @RideId uniqueidentifier = '33333333-3333-3333-3333-333333333333';
DECLARE @RiderId uniqueidentifier = '22222222-2222-2222-2222-222222222222';

IF NOT EXISTS (SELECT 1 FROM dbo.Riders WHERE Id = @RiderId)
    INSERT dbo.Riders(Id, DisplayName, PhoneNumber, Email)
    VALUES (@RiderId, N'Demo Rider', N'+91-9000000000', N'rider@example.test');

IF NOT EXISTS (SELECT 1 FROM dbo.Drivers WHERE Id = @DriverOne)
    EXEC dbo.usp_SetDriverOnline @DriverOne, 12.9717, 77.5947;

IF NOT EXISTS (SELECT 1 FROM dbo.Drivers WHERE Id = @DriverTwo)
    EXEC dbo.usp_SetDriverOnline @DriverTwo, 12.9352, 77.6245;

IF NOT EXISTS (SELECT 1 FROM dbo.Rides WHERE Id = @RideId)
    EXEC dbo.usp_CreateRide @RideId, @RiderId, 12.9716, 77.5946, 12.9352, 77.6245;

EXEC dbo.usp_TryMatchRide @RideId = @RideId, @MaximumDistanceKm = 25, @LocationFreshnessSeconds = 300;
GO
