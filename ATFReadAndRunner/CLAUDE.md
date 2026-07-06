# ATFReadAndRunner

## After making code changes

Always publish after building so the `publish\` folder stays up to date:

```
dotnet publish ATFRerunTool\ATFRerunTool.csproj -c Release -o publish
```

The `publish\` folder is what gets run in practice — the `ATFRun\` logs and `appsettings.json` live next to the exe there.
