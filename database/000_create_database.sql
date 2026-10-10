/* Run with SQLCMD mode enabled. Override with: -v DatabaseName="YourDatabase" */
:setvar DatabaseName "RideMatching"

USE [master];
GO

IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
    DECLARE @CreateDatabaseSql nvarchar(max) =
        N'CREATE DATABASE ' + QUOTENAME(N'$(DatabaseName)') + N';';
    EXEC sys.sp_executesql @CreateDatabaseSql;
END;
GO

USE [$(DatabaseName)];
GO
