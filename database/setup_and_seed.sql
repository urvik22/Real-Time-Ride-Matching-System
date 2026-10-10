/*
  One-command local setup from the repository root:
  sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i database/setup_and_seed.sql
*/
:on error exit
:r database/000_create_database.sql
:r database/001_schema.sql
:r database/002_seed_demo.sql

PRINT N'RideMatching database, schema, and demo data are ready.';
GO
