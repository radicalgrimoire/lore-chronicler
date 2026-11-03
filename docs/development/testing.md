# Testing

Run this from PowerShell at the repository root.

```powershell
dotnet test .\tests\Chronicler.Tests\Chronicler.Tests.csproj -c Release
```

Automated tests use temporary folders and substitute implementations for login and repository operations. They also verify offline revision synchronization through the SDK. They do not include login to an actual server or Entra environment, actual cloning, or retrieving from a server.
