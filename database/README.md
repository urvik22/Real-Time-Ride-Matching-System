# SQL Server database scripts

The scripts target SQL Server 2019+ and Azure SQL Database.

## Create and preload a local database

The simplest option creates the database, creates or upgrades every table and procedure, then inserts deterministic demo data:

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i database/setup_and_seed.sql
```

Run it from the repository root. It is safe to execute again: database objects and seed rows are guarded by existence checks.

To select another database name:

```powershell
sqlcmd -S localhost -E -v DatabaseName="MyRideMatching" -i database/setup_and_seed.sql
```

### Run scripts separately

Using `sqlcmd` with Windows authentication:

```powershell
sqlcmd -S localhost -E -Q "IF DB_ID('RideMatching') IS NULL CREATE DATABASE RideMatching"
sqlcmd -S localhost -E -d RideMatching -i database/001_schema.sql
sqlcmd -S localhost -E -d RideMatching -i database/002_seed_demo.sql
```

For SQL authentication, replace `-E` with `-U <username> -P <password>`.

`001_schema.sql` contains Riders, Drivers, Rides, geospatial and filtered indexes, audit history, transactional outbox, creation procedures, and an atomic nearest-driver matching procedure. `002_seed_demo.sql` is optional sample data.

## Windows authentication connection string

The default API configuration is:

```text
Server=(localdb)\MSSQLLocalDB;Database=RideMatching;Integrated Security=True;TrustServerCertificate=True;MultipleActiveResultSets=True;Connect Timeout=3
```

Change the server in `appsettings.json` for a full SQL Server instance, for example `Server=localhost` or `Server=.\SQLEXPRESS`. No username or password is needed with Windows authentication.

At startup the API opens the connection and verifies the three core tables. A successful check activates the SQL stores. A missing/invalid connection, unavailable server, or missing schema activates the in-memory stores instead. Confirm the selected provider at `GET /api/system/storage`.

The API currently defaults to its in-memory store so reviewers can run it without infrastructure. The `IDriverStore` and `IRideStore` boundaries are the extension points for a SQL-backed implementation.
