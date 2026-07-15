#requires -Version 5.1
<#
.SYNOPSIS
    Verifies the NuGate.Build .nupkg has the correct task-package shape.

.DESCRIPTION
    A NuGet MSBuild task package must ship its task assembly plus its full runtime dependency
    closure under tasks/<tfm>/, and its props/targets under build/. This script inspects the packed
    .nupkg (a zip) and asserts:
      * build/ contains NuGate.Build.props and NuGate.Build.targets
      * tasks/netstandard2.0/ contains the task assembly + NuGate.Core + the System.Text.Json
        netstandard2.0 closure
      * no Microsoft.Build.* assembly is packed (those are provided by the MSBuild host)
    Exits 0 on success, 1 on any failure. Integration can rerun this after a fresh pack.

.PARAMETER NupkgPath
    Path to the .nupkg. Defaults to the newest NuGate.Build.*.nupkg under ../../artifacts.
#>
[CmdletBinding()]
param(
    [string]$NupkgPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $NupkgPath) {
    $artifacts = Join-Path $PSScriptRoot '..\..\artifacts'
    $candidate = Get-ChildItem -Path $artifacts -Filter 'NuGate.Build.*.nupkg' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) {
        Write-Error "No NuGate.Build.*.nupkg found under '$artifacts'. Run: dotnet pack src/NuGate.Build/NuGate.Build.csproj -c Release -o artifacts/"
        exit 1
    }
    $NupkgPath = $candidate.FullName
}

if (-not (Test-Path $NupkgPath)) {
    Write-Error "Package not found: $NupkgPath"
    exit 1
}

Write-Host "Inspecting: $NupkgPath"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
} finally {
    $zip.Dispose()
}

$tfm = 'netstandard2.0'
$taskDir = "tasks/$tfm"

# The exact runtime closure verified from build output (System.Text.Json 8.x on netstandard2.0).
$expectedTasks = @(
    "$taskDir/NuGate.Build.dll",
    "$taskDir/NuGate.Core.dll",
    "$taskDir/System.Text.Json.dll",
    "$taskDir/System.Memory.dll",
    "$taskDir/System.Buffers.dll",
    "$taskDir/System.Numerics.Vectors.dll",
    "$taskDir/System.Runtime.CompilerServices.Unsafe.dll",
    "$taskDir/System.Threading.Tasks.Extensions.dll",
    "$taskDir/System.Text.Encodings.Web.dll",
    "$taskDir/Microsoft.Bcl.AsyncInterfaces.dll"
)

$expectedBuild = @(
    'build/NuGate.Build.props',
    'build/NuGate.Build.targets'
)

$failures = New-Object System.Collections.Generic.List[string]

foreach ($expected in ($expectedTasks + $expectedBuild)) {
    if ($entries -notcontains $expected) {
        $failures.Add("MISSING: $expected")
    } else {
        Write-Host "  ok  $expected"
    }
}

# Microsoft.Build.* must never be packed — the host provides it.
$hostProvided = $entries | Where-Object { $_ -like "$taskDir/Microsoft.Build*" }
foreach ($bad in $hostProvided) {
    $failures.Add("UNEXPECTED (host-provided, must not pack): $bad")
}

# Nothing should land under lib/ for a task package.
$libEntries = $entries | Where-Object { $_ -like 'lib/*' }
foreach ($bad in $libEntries) {
    $failures.Add("UNEXPECTED (task package must not ship lib/): $bad")
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host 'PACKAGE VERIFICATION FAILED:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "PACKAGE OK: $($expectedTasks.Count) task assemblies + $($expectedBuild.Count) build files, no host-provided assemblies packed." -ForegroundColor Green
exit 0
