# Generates SHA-256 checksums for the two release DLLs.

param(
    [Parameter(Mandatory = $true)][string]$PluginPath,
    [Parameter(Mandatory = $true)][string]$CorePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$ProjectRoot = $PSScriptRoot
$RepositoryRoot = Split-Path -Parent $ProjectRoot
$CommonRoot = Split-Path -Parent $RepositoryRoot
$CommonScript = Join-Path $CommonRoot "API\CommonPublish.ps1"
. $CommonScript

Assert-FileExists -Path $PluginPath
Assert-FileExists -Path $CorePath

$Lines = foreach ($Path in @($PluginPath, $CorePath)) {
    $Hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $([System.IO.Path]::GetFileName($Path))"
}

$Directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($Directory)) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
}

Write-Utf8NoBom -Path $OutputPath -Content (($Lines -join [Environment]::NewLine) + [Environment]::NewLine)
Write-Host "SHA-256 checksums written to: $OutputPath"
