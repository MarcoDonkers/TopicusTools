# OrangebeardApp

This console app reads `[dbo].[TestResults]` from SQL Server and reports each row to Orangebeard using the listener V3 client.

## Configuration

Set the source connection string and Orangebeard settings in `appsettings.json`, or override them with environment variables:

- `SOURCE_CONNECTION_STRING`
- `ORANGEBEARD_ENDPOINT`
- `ORANGEBEARD_TOKEN`
- `ORANGEBEARD_PROJECT`
- `ORANGEBEARD_TESTSET`
- `ORANGEBEARD_DESCRIPTION`
- `ORANGEBEARD_STATE_DB`

The app keeps a local SQLite state file named `orangebeard-sync-state.db` by default. Each source `Id` is recorded after a successful send so reruns skip rows that were already reported.

## Run

```bash
dotnet run --project OrangebeardApp.csproj
```

If you want to build first:

```bash
dotnet build OrangebeardApp.csproj
```
