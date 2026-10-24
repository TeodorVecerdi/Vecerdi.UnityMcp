#Requires -Version 7
<#
.SYNOPSIS
    Publish the Unity MCP bridge to its stable location, even while MCP clients are running it.

.DESCRIPTION
    Publishes into a staging folder first, then copies the result over the published bridge. A running
    client keeps its unity-mcp.exe and DLLs open, and a file in use can't be overwritten, but it can be
    renamed. So each locked file that changed is moved aside as '<name>.<timestamp>.old' and the new
    one copied in. Running clients keep the build they started with, and the next client launch gets
    the new one. Leftover .old files are deleted on the next publish once nothing holds them. Restart
    clients reasonably soon after a publish that changes dependencies: a running bridge that loads an
    assembly for the first time after the swap gets the new copy.

    The destination defaults to the project's own PublishDir, which follows the artifacts layout: a
    hosting repository that sets ArtifactsPath decides where it is, otherwise Bridge~/artifacts/. Either
    way it ends in publish/UnityMcp/release/ (see Directory.Build.props and README.md).

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER OutputPath
    Publish here instead of the project's PublishDir.

.EXAMPLE
    ./publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src/UnityMcp/UnityMcp.csproj'

if (-not $OutputPath) {
    $OutputPath = (& dotnet msbuild $project -nologo "-getProperty:PublishDir" "-p:Configuration=$Configuration").Trim()
    if ($LASTEXITCODE -ne 0 -or -not $OutputPath) {
        throw "Could not resolve the bridge's PublishDir (dotnet msbuild exited with $LASTEXITCODE)."
    }
}

$destination = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputPath)) + [IO.Path]::DirectorySeparatorChar
New-Item -ItemType Directory -Force $destination | Out-Null

# Clear out files set aside by earlier publishes; any still held by a running client stay until next time.
Get-ChildItem $destination -Recurse -File -Filter '*.old' | ForEach-Object {
    try { Remove-Item $_.FullName -Force } catch { }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) "unity-mcp-publish-$([guid]::NewGuid().ToString('N'))"

try {
    Write-Host "Publishing the Unity MCP bridge ($Configuration)..." -ForegroundColor Cyan
    & dotnet publish $project -c $Configuration -o $staging -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE; the published bridge was left untouched."
    }

    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $updated = 0
    $setAside = [System.Collections.Generic.List[string]]::new()

    foreach ($file in Get-ChildItem $staging -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($staging, $file.FullName)
        $target = Join-Path $destination $relative

        if (Test-Path $target) {
            $existing = Get-Item $target
            # Unchanged files (most dependencies) stay as they are, so they don't need renaming when locked.
            if ($existing.Length -eq $file.Length -and
                (Get-FileHash $existing.FullName).Hash -eq (Get-FileHash $file.FullName).Hash) {
                continue
            }

            try {
                Copy-Item $file.FullName $target -Force
            } catch [System.IO.IOException] {
                Rename-Item $target "$($existing.Name).$stamp.old"
                Copy-Item $file.FullName $target
                $setAside.Add($relative)
            }
        } else {
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $file.FullName $target
        }

        $updated++
    }
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($updated -eq 0) {
    Write-Host "Already up to date: " -NoNewline -ForegroundColor Green
} else {
    Write-Host "Published $updated changed file(s) to " -NoNewline -ForegroundColor Green
}
Write-Host $destination

if ($setAside.Count -gt 0) {
    Write-Host "In use, so set aside as .old: $($setAside -join ', ')" -ForegroundColor DarkGray
}

# Clients started before this publish keep running the previous build until they restart.
$running = @(Get-Process -Name 'unity-mcp*' -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($destination, [StringComparison]::OrdinalIgnoreCase)
})
if ($updated -gt 0 -and $running.Count -gt 0) {
    Write-Host ""
    Write-Host "Still running the previous build (restart their MCP clients to pick up this one):" -ForegroundColor Yellow
    foreach ($process in $running) {
        Write-Host "  PID $($process.Id), started $($process.StartTime)"
    }
}
