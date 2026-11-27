# Packs the chipsharp command as a .NET tool and installs it for the current user, so `chipsharp` works in any folder.
# Run it again after changing ChipSharp: it packs the current code and replaces the installed tool.
# Usage:  powershell -ExecutionPolicy Bypass -File .\tools\install-tool.ps1
# Remove: dotnet tool uninstall --global ChipSharp
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$packages = Join-Path $repo "artifacts\nupkg"

dotnet pack (Join-Path $repo "src\ChipSharp.Cli") -c Release -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# the version doesn't change with every edit, so take the installed one away first and install the fresh package
if (dotnet tool list --global | Select-String -Pattern "^chipsharp\s" -Quiet) {
    dotnet tool uninstall --global ChipSharp | Out-Null
}
dotnet tool install --global ChipSharp --add-source $packages --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Installed: try  chipsharp targets list"
Write-Host "(If the command isn't found, open a new terminal: the .NET tools folder joins the PATH there.)"
