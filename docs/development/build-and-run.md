# Build and run

Prepare the [development environment](../../README.md#development-environment), then run the following from PowerShell at the repository root.

```powershell
dotnet build .\src\Chronicler\Chronicler.csproj
dotnet run --project .\src\Chronicler\Chronicler.csproj
```

On Windows, you can run `script\build-debug.bat` or `script\build-release.bat` from the repository to build the Debug or Release configuration, respectively. The batch files resolve the project relative to their own location, so they can be run from any working directory.
