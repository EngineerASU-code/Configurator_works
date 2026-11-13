# Configurator

Source code for the GASS Configurator desktop application (C# / .NET 8 / WPF).

The solution and project are in this repository root. The source, tests, and project files are tracked; local build outputs, IDE state, test artifacts, reports, and workspace-specific instructions are excluded.

Build from the repository root:

```powershell
dotnet build Configurator.csproj -c Release --no-restore
```

Run the security tests from the repository root:

```powershell
dotnet test tests\Configurator.Security.Tests\Configurator.Security.Tests.csproj -c Release --no-restore --logger 'console;verbosity=minimal' -v quiet
```
