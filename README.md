# Configurator

Source code for the GASS Configurator desktop application (C# / .NET 8 / WPF).

The solution and project are in this repository root. Source, tests, runtime images, contributor instructions, and project documentation are tracked. Build outputs, IDE state, test artifacts, local reports, and credentials are excluded.

`MainWindow/`, `Modes/`, and `FirstScreen/` contain the WPF interface; `Core/` defines architecture and workspace contracts; `Application/`, `Services/`, and `Infrastructure/` implement workflows and storage. Shared UI resources live in `Themes/`, `Helpers/`, `ViewModels/`, and `Images/`.

See [the architecture index](docs/project/ARCHITECTURE_INDEX.md), [the preparation report](docs/project/PROJECT_PREPARATION_2026-11-17.md), and [manual acceptance materials](testing/README.md). Open task requirements remain in `docs/tasks/` and `tasks/`. This is a development snapshot; WPF/SQL acceptance and legacy round-trip compatibility remain open.

Use Windows, the .NET 10 SDK (for the `.slnx` solution), and the .NET 8 Windows Desktop Runtime. Restore from a fresh checkout:

```powershell
dotnet restore Configurator.slnx
```

Build from the repository root:

```powershell
dotnet build Configurator.csproj -c Release --no-restore
```

Run the security tests from the repository root:

```powershell
$testTemp = Join-Path (Get-Location) ('artifacts\quality-temp-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
$env:TEMP = $testTemp; $env:TMP = $testTemp
dotnet test tests\Configurator.Security.Tests\Configurator.Security.Tests.csproj -c Release --no-restore --logger 'console;verbosity=minimal' -v quiet
```

Run the tests in a separate PowerShell session so the temporary environment changes stay isolated. To create a local runnable package:

```powershell
dotnet publish Configurator.csproj -c Release --no-restore -o artifacts/Configurator-release
```

Run `artifacts/Configurator-release/Configurator.exe` with its neighboring files. SQL workflows require a configured test database; routine checks must not write to a production database.
