/*
  Real-Time Ride Matching System - SQL Server schema
  Target: SQL Server 2019+ / Azure SQL Database

  Run this script against an empty database. It is intentionally repeatable for
  object creation, but it does not drop or overwrite existing data.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.Riders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Riders
    (
        Id            uniqueidentifier NOT NULL,
        DisplayName   nvarchar(150) NOT NULL,
        PhoneNumber   nvarchar(30) NULL,
        Email         nvarchar(320) NULL,
        IsActive      bit NOT NULL CONSTRAINT DF_Riders_IsActive DEFAULT (1),
        CreatedAt     datetime2(3) NOT NULL CONSTRAINT DF_Riders_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt     datetime2(3) NOT NULL CONSTRAINT DF_Riders_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        Version       rowversion NOT NULL,
        CONSTRAINT PK_Riders PRIMARY KEY CLUSTERED (Id)
    );
END;
GO

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Drivers
    (
        Id                 uniqueidentifier NOT NULL,
        Status             tinyint NOT NULL CONSTRAINT DF_Drivers_Status DEFAULT (0),
        CurrentLocation    geography NULL,
        LocationUpdatedAt  datetime2(3) NULL,
        CurrentRideId      uniqueidentifier NULL,
        CreatedAt          datetime2(3) NOT NULL CONSTRAINT DF_Drivers_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt          datetime2(3) NOT NULL CONSTRAINT DF_Drivers_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        Version            rowversion NOT NULL,
        CONSTRAINT PK_Drivers PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_Drivers_Status CHECK (Status IN (0, 1, 2, 3)),
        CONSTRAINT CK_Drivers_Location_SRID CHECK (CurrentLocation IS NULL OR CurrentLocation.STSrid = 4326),
        CONSTRAINT CK_Drivers_ActiveRide CHECK
        (
            (Status IN (0, 1) AND CurrentRideId IS NULL)
            OR (Status IN (2, 3) AND CurrentRideId IS NOT NULL)
        )
    );
END;
GO

IF OBJECT_ID(N'dbo.Rides', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Rides
    (
        Id                 uniqueidentifier NOT NULL,
        RiderId            uniqueidentifier NOT NULL,
        DriverId           uniqueidentifier NULL,
        PickupLocation     geography NOT NULL,
        DestinationLocation geography NOT NULL,
        Status             tinyint NOT NULL CONSTRAINT DF_Rides_Status DEFAULT (0),
        DriverDistanceKm   decimal(9,3) NULL,
        RequestedAt        datetime2(3) NOT NULL CONSTRAINT DF_Rides_RequestedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt          datetime2(3) NOT NULL CONSTRAINT DF_Rides_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        StartedAt          datetime2(3) NULL,
        CompletedAt        datetime2(3) NULL,
        CancelledAt        datetime2(3) NULL,
        Version            rowversion NOT NULL,
        CONSTRAINT PK_Rides PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Rides_Riders FOREIGN KEY (RiderId) REFERENCES dbo.Riders(Id),
        CONSTRAINT FK_Rides_Drivers FOREIGN KEY (DriverId) REFERENCES dbo.Drivers(Id),
        CONSTRAINT CK_Rides_Status CHECK (Status IN (0, 1, 2, 3, 4)),
        CONSTRAINT CK_Rides_Pickup_SRID CHECK (PickupLocation.STSrid = 4326),
        CONSTRAINT CK_Rides_Destination_SRID CHECK (DestinationLocation.STSrid = 4326),
        CONSTRAINT CK_Rides_DriverAssignment CHECK
        (
            (Status = 0 AND DriverId IS NULL)
            OR (Status IN (1, 2, 3) AND DriverId IS NOT NULL)
            OR Status = 4
        )
    );
END;
GO

/* Upgrade path for databases created before the Riders table was introduced. */
INSERT dbo.Riders(Id, DisplayName)
SELECT DISTINCT r.RiderId, CONCAT(N'Rider-', LEFT(CONVERT(nvarchar(36), r.RiderId), 8))
  FROM dbo.Rides AS r
 WHERE NOT EXISTS (SELECT 1 FROM dbo.Riders AS existing WHERE existing.Id = r.RiderId);
GO

IF NOT EXISTS
(
    SELECT 1
      FROM sys.foreign_keys
     WHERE parent_object_id = OBJECT_ID(N'dbo.Rides')
       AND name = N'FK_Rides_Riders'
)
    ALTER TABLE dbo.Rides WITH CHECK
        ADD CONSTRAINT FK_Rides_Riders FOREIGN KEY (RiderId) REFERENCES dbo.Riders(Id);
GO

IF NOT EXISTS
(
    SELECT 1
      FROM sys.foreign_keys
     WHERE parent_object_id = OBJECT_ID(N'dbo.Drivers')
       AND name = N'FK_Drivers_CurrentRide'
)
    ALTER TABLE dbo.Drivers
        ADD CONSTRAINT FK_Drivers_CurrentRide
        FOREIGN KEY (CurrentRideId) REFERENCES dbo.Rides(Id);
GO

IF OBJECT_ID(N'dbo.RideStatusHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RideStatusHistory
    (
        Id          bigint IDENTITY(1,1) NOT NULL,
        RideId      uniqueidentifier NOT NULL,
        Status      tinyint NOT NULL,
        OccurredAt  datetime2(3) NOT NULL CONSTRAINT DF_RideStatusHistory_OccurredAt DEFAULT (SYSUTCDATETIME()),
        Metadata    nvarchar(1000) NULL,
        CONSTRAINT PK_RideStatusHistory PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_RideStatusHistory_Rides FOREIGN KEY (RideId) REFERENCES dbo.Rides(Id),
        CONSTRAINT CK_RideStatusHistory_Status CHECK (Status IN (0, 1, 2, 3, 4))
    );
END;
GO

IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxMessages
    (
        Id            uniqueidentifier NOT NULL,
        AggregateId   uniqueidentifier NOT NULL,
        EventType     nvarchar(200) NOT NULL,
        Payload       nvarchar(max) NOT NULL,
        OccurredAt    datetime2(3) NOT NULL CONSTRAINT DF_OutboxMessages_OccurredAt DEFAULT (SYSUTCDATETIME()),
        ProcessedAt   datetime2(3) NULL,
        RetryCount    int NOT NULL CONSTRAINT DF_OutboxMessages_RetryCount DEFAULT (0),
        CONSTRAINT PK_OutboxMessages PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_OutboxMessages_PayloadJson CHECK (ISJSON(Payload) = 1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Drivers') AND name = N'IX_Drivers_Available')
    CREATE INDEX IX_Drivers_Available
        ON dbo.Drivers(Status, LocationUpdatedAt)
        INCLUDE (CurrentRideId)
        WHERE Status = 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Drivers') AND name = N'UX_Drivers_CurrentRideId')
    CREATE UNIQUE INDEX UX_Drivers_CurrentRideId
        ON dbo.Drivers(CurrentRideId)
        WHERE CurrentRideId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Rides') AND name = N'IX_Rides_Searching')
    CREATE INDEX IX_Rides_Searching
        ON dbo.Rides(Status, RequestedAt)
        INCLUDE (RiderId, DriverId)
        WHERE Status = 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Rides') AND name = N'IX_Rides_RiderId')
    CREATE INDEX IX_Rides_RiderId ON dbo.Rides(RiderId, RequestedAt DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Riders') AND name = N'UX_Riders_Email')
    CREATE UNIQUE INDEX UX_Riders_Email ON dbo.Riders(Email) WHERE Email IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.RideStatusHistory') AND name = N'IX_RideStatusHistory_RideId')
    CREATE INDEX IX_RideStatusHistory_RideId ON dbo.RideStatusHistory(RideId, OccurredAt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OutboxMessages') AND name = N'IX_OutboxMessages_Unprocessed')
    CREATE INDEX IX_OutboxMessages_Unprocessed
        ON dbo.OutboxMessages(OccurredAt)
        INCLUDE (EventType, RetryCount)
        WHERE ProcessedAt IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Drivers') AND name = N'SIX_Drivers_CurrentLocation')
    CREATE SPATIAL INDEX SIX_Drivers_CurrentLocation
        ON dbo.Drivers(CurrentLocation)
        USING GEOGRAPHY_AUTO_GRID
        WITH (CELLS_PER_OBJECT = 16);
GO

/*
  Status values:
    Driver: 0 Offline, 1 Available, 2 Reserved, 3 OnTrip
    Ride:   0 Searching, 1 Matched, 2 InProgress, 3 Completed, 4 Cancelled
*/

CREATE OR ALTER PROCEDURE dbo.usp_SetDriverOnline
    @DriverId uniqueidentifier,
    @Latitude float,
    @Longitude float
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Latitude NOT BETWEEN -90 AND 90 OR @Longitude NOT BETWEEN -180 AND 180
        THROW 50001, 'Invalid latitude or longitude.', 1;

    DECLARE @Location geography = geography::Point(@Latitude, @Longitude, 4326);

    UPDATE dbo.Drivers WITH (UPDLOCK)
       SET Status = 1,
           CurrentLocation = @Location,
           LocationUpdatedAt = SYSUTCDATETIME(),
           UpdatedAt = SYSUTCDATETIME()
     WHERE Id = @DriverId
       AND Status IN (0, 1);

    IF @@ROWCOUNT = 0
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.Drivers WHERE Id = @DriverId)
            THROW 50002, 'A reserved or on-trip driver cannot be put online again.', 1;

        INSERT dbo.Drivers(Id, Status, CurrentLocation, LocationUpdatedAt)
        VALUES (@DriverId, 1, @Location, SYSUTCDATETIME());
    END;

    SELECT Id, Status, CurrentLocation.Lat AS Latitude, CurrentLocation.Long AS Longitude,
           LocationUpdatedAt, CurrentRideId, Version
      FROM dbo.Drivers
     WHERE Id = @DriverId;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_CreateRide
    @RideId uniqueidentifier,
    @RiderId uniqueidentifier,
    @PickupLatitude float,
    @PickupLongitude float,
    @DestinationLatitude float,
    @DestinationLongitude float
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @PickupLatitude NOT BETWEEN -90 AND 90 OR @DestinationLatitude NOT BETWEEN -90 AND 90
       OR @PickupLongitude NOT BETWEEN -180 AND 180 OR @DestinationLongitude NOT BETWEEN -180 AND 180
        THROW 50003, 'Invalid pickup or destination coordinates.', 1;

    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.Riders WITH (UPDLOCK, HOLDLOCK) WHERE Id = @RiderId)
        INSERT dbo.Riders(Id, DisplayName)
        VALUES (@RiderId, CONCAT(N'Rider-', LEFT(CONVERT(nvarchar(36), @RiderId), 8)));

    INSERT dbo.Rides(Id, RiderId, PickupLocation, DestinationLocation, Status)
    VALUES
    (
        @RideId,
        @RiderId,
        geography::Point(@PickupLatitude, @PickupLongitude, 4326),
        geography::Point(@DestinationLatitude, @DestinationLongitude, 4326),
        0
    );

    INSERT dbo.RideStatusHistory(RideId, Status) VALUES (@RideId, 0);

    INSERT dbo.OutboxMessages(Id, AggregateId, EventType, Payload)
    VALUES
    (
        NEWID(),
        @RideId,
        N'RideRequested',
        CONCAT(N'{"rideId":"', CONVERT(nvarchar(36), @RideId), N'","riderId":"',
               CONVERT(nvarchar(36), @RiderId), N'"}')
    );

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_TryMatchRide
    @RideId uniqueidentifier,
    @MaximumDistanceKm decimal(9,3) = 25.000,
    @LocationFreshnessSeconds int = 30
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Pickup geography;
    DECLARE @DriverId uniqueidentifier;
    DECLARE @DistanceMetres float;

    BEGIN TRANSACTION;

    SELECT @Pickup = PickupLocation
      FROM dbo.Rides WITH (UPDLOCK, HOLDLOCK)
     WHERE Id = @RideId
       AND Status = 0;

    IF @Pickup IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit) AS Matched, CAST(NULL AS uniqueidentifier) AS DriverId,
               CAST(NULL AS decimal(9,3)) AS DistanceKm;
        RETURN;
    END;

    SELECT TOP (1)
           @DriverId = Id,
           @DistanceMetres = CurrentLocation.STDistance(@Pickup)
      FROM dbo.Drivers WITH (UPDLOCK, READPAST, ROWLOCK)
     WHERE Status = 1
       AND CurrentRideId IS NULL
       AND CurrentLocation IS NOT NULL
       AND LocationUpdatedAt >= DATEADD(SECOND, -@LocationFreshnessSeconds, SYSUTCDATETIME())
       AND CurrentLocation.STDistance(@Pickup) <= @MaximumDistanceKm * 1000.0
     ORDER BY CurrentLocation.STDistance(@Pickup), Id;

    IF @DriverId IS NULL
    BEGIN
        COMMIT TRANSACTION;
        SELECT CAST(0 AS bit) AS Matched, CAST(NULL AS uniqueidentifier) AS DriverId,
               CAST(NULL AS decimal(9,3)) AS DistanceKm;
        RETURN;
    END;

    UPDATE dbo.Drivers
       SET Status = 2,
           CurrentRideId = @RideId,
           UpdatedAt = SYSUTCDATETIME()
     WHERE Id = @DriverId
       AND Status = 1
       AND CurrentRideId IS NULL;

    IF @@ROWCOUNT <> 1
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50004, 'Driver reservation lost due to a concurrent update.', 1;
    END;

    UPDATE dbo.Rides
       SET Status = 1,
           DriverId = @DriverId,
           DriverDistanceKm = CAST(@DistanceMetres / 1000.0 AS decimal(9,3)),
           UpdatedAt = SYSUTCDATETIME()
     WHERE Id = @RideId
       AND Status = 0;

    IF @@ROWCOUNT <> 1
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50005, 'Ride assignment lost due to a concurrent update.', 1;
    END;

    INSERT dbo.RideStatusHistory(RideId, Status, Metadata)
    VALUES (@RideId, 1, CONCAT(N'{"driverId":"', CONVERT(nvarchar(36), @DriverId), N'"}'));

    INSERT dbo.OutboxMessages(Id, AggregateId, EventType, Payload)
    VALUES
    (
        NEWID(),
        @RideId,
        N'RideMatched',
        CONCAT(N'{"rideId":"', CONVERT(nvarchar(36), @RideId), N'","driverId":"',
               CONVERT(nvarchar(36), @DriverId), N'"}')
    );

    COMMIT TRANSACTION;

    SELECT CAST(1 AS bit) AS Matched, @DriverId AS DriverId,
           CAST(@DistanceMetres / 1000.0 AS decimal(9,3)) AS DistanceKm;
END;
GO
