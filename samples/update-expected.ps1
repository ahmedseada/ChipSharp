<#
.SYNOPSIS
    Writes <Name>.expected.txt next to every sample <Name>.cs: what the sample prints when real .NET runs it.

.DESCRIPTION
    ChipSharp's output for each sample must match these files exactly. To add a sample, drop a new <Name>.cs
    (a class Program with static void Main) into this folder and run this script.

    Every sample is compiled with the .NET SDK in a temporary console project under the system temp folder,
    run with dotnet, and its standard output saved byte for byte. The temporary folder is deleted at the end;
    nothing but the .expected.txt files is written here. An .expected.txt whose .cs is gone is deleted.

    The samples run in the invariant culture, as ChipSharp's programs print (e.g. 1.5, not 1,5), so the regional
    settings of the PC that runs this script never change the expected files.

    Run it on Windows: the expected files hold .NET's Windows output, whose lines end with \r\n.

.PARAMETER TargetFramework
    The .NET version the samples are compiled for (default net10.0, like the rest of ChipSharp).

.EXAMPLE
    .\samples\update-expected.ps1
#>
[CmdletBinding()]
param(
    [string] $TargetFramework = 'net10.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'Run this script on Windows: the expected files hold .NET''s Windows output (lines end with \r\n).'
}

$samplesFolder = $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Runs a program and returns its exit code and exactly what it wrote, standard output and error kept apart.
function Invoke-Program([string] $fileName, [string] $arguments, [string] $folder) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $fileName
    $start.Arguments = $arguments
    $start.WorkingDirectory = $folder
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [System.Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Errors = $errors.Result }
}

$samples = @(Get-ChildItem -Path $samplesFolder -Filter '*.cs' -File | Sort-Object Name)
if ($samples.Count -eq 0) {
    throw "There are no samples (*.cs) in '$samplesFolder'."
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('chipsharp-samples-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # One console project, built once per sample with that sample as its only source file. The empty
    # Directory.Build files stop MSBuild from looking for settings in the folders above the temp folder.
    # InvariantGlobalization: every culture is the invariant culture when the sample runs (the setting goes into
    # its runtimeconfig.json, which no environment variable overrides).
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$TargetFramework</TargetFramework>
    <AssemblyName>Sample</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
"@
    [System.IO.File]::WriteAllText((Join-Path $work 'Sample.csproj'), $project, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $work 'Directory.Build.props'), '<Project />', $utf8)
    [System.IO.File]::WriteAllText((Join-Path $work 'Directory.Build.targets'), '<Project />', $utf8)
    $program = Join-Path $work "bin\Release\$TargetFramework\Sample.dll"

    foreach ($sample in $samples) {
        $name = $sample.BaseName
        Copy-Item -LiteralPath $sample.FullName -Destination (Join-Path $work 'Program.cs') -Force

        # --no-incremental: the copied file keeps the sample's date, which may be older than the last build
        $build = Invoke-Program 'dotnet' 'build -c Release --no-incremental --nologo -v quiet -clp:NoSummary' $work
        if ($build.ExitCode -ne 0) {
            throw "Sample $name does not compile:`n$($build.Output)$($build.Errors)"
        }

        $run = Invoke-Program 'dotnet' "`"$program`"" $work
        if ($run.ExitCode -ne 0) {
            throw "Sample $name failed (exit code $($run.ExitCode)):`n$($run.Errors)"
        }

        $expected = Join-Path $samplesFolder "$name.expected.txt"
        [System.IO.File]::WriteAllText($expected, $run.Output, $utf8)
        $lines = ([regex]::Matches($run.Output, "`r`n")).Count
        Write-Host ("{0,-16} {1,3} lines -> {2}" -f $name, $lines, (Split-Path -Leaf $expected))
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($orphan in Get-ChildItem -Path $samplesFolder -Filter '*.expected.txt' -File) {
    $source = Join-Path $samplesFolder ($orphan.Name -replace '\.expected\.txt$', '.cs')
    if (-not (Test-Path -LiteralPath $source)) {
        Remove-Item -LiteralPath $orphan.FullName
        Write-Host "deleted $($orphan.Name): its sample is gone"
    }
}
