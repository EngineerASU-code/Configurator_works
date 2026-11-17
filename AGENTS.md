# Repository Guidelines

## Project Structure & Module Organization
Configurator is a C#/.NET 8 WPF app. App.xaml and App.xaml.cs bootstrap it; MainWindow/ is the shell, Modes/ contains screens, and FirstScreen/, Themes/, and ViewModels/ support the UI. Core/ owns architecture and project contracts; Application/ contains use cases; Services/ contains workflows; Infrastructure/ contains SQL and persistence. Tests live in tests/Configurator.Security.Tests/. The architecture map is docs/project/ARCHITECTURE_INDEX.md; task specs go in docs/tasks/ and manual evidence in testing/.

## Build, Test & Run
Run from the repository root. Restore packages on a fresh checkout:
```powershell
dotnet restore .\Configurator.slnx
dotnet build .\Configurator.csproj -c Release --no-restore
dotnet test .\tests\Configurator.Security.Tests\Configurator.Security.Tests.csproj -c Release --no-restore --logger 'console;verbosity=minimal' -v quiet
dotnet run --project .\Configurator.csproj
```
The app needs Windows and the .NET 8 Desktop Runtime. CI builds and tests Debug and Release on Windows.

## Coding Style & Naming
Use four spaces and follow nearby C# and XAML patterns. Use PascalCase for types and methods, _camelCase for private fields, I-prefixed interfaces, and the Async suffix for asynchronous methods. Keep changes scoped to the task; do not add dependencies or infrastructure without a demonstrated need. No dedicated linter, formatter, or coverage threshold is configured; builds provide compiler and SDK analyzer diagnostics.

## Testing Guidelines
Tests use xUnit; name files/classes *Tests.cs and add focused [Fact] or [Theory] cases. Before tests, isolate the temp root because ProjectPackageAdversarialTests deletes a fixed outside.txt there:
```powershell
$testTemp = Join-Path (Get-Location) ('artifacts\quality-temp-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
$env:TEMP = $testTemp; $env:TMP = $testTemp
```
Unit tests do not replace WPF manual checks or real SQL Server integration.

## Security & Architecture
Keep data access behind Core/Projects contracts. Offline must not connect to SQL; production SQL connection creation/opening goes through SqlProviderGuard. Preserve local project package validation and recovery. Never commit testing/GASS/SQL_Configuration.ini or credentials; use SQL_Configuration.example.ini. Do not run writes against a user's database during routine checks.

## Commits & Pull Requests
History uses concise imperative subjects, sometimes docs: or chore: prefixes; no strict convention or PR template exists. Describe purpose, report exact checks, link the task, and attach screenshots for UI changes. Create commits or push only on an explicit request.

## Graphify
Use docs/project/ARCHITECTURE_INDEX.md and Graphify to navigate relationships; verify the index commit and confirm findings in source. Start with the affected module and its callers instead of scanning the whole tree.