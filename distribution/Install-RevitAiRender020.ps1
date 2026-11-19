#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('2024','2025')][string]$RevitYear,
    [switch]$Update,
    [ValidatePattern('^(?:[a-zA-Z0-9._-]+@)?[a-zA-Z0-9][a-zA-Z0-9._-]*$')][string]$SshTarget,
    [ValidatePattern('^/[^\r\n]+$')][string]$SilicontrolPath,
    [ValidatePattern('^/[^\r\n]+$')][string]$MfluxPython,
    [ValidatePattern('^/[^\r\n]+$')][string]$MfluxCli,
    [ValidatePattern('^/[^\r\n]+$')][string]$MfluxModel
)
$ErrorActionPreference = 'Stop'
$legacy = Join-Path $env:LOCALAPPDATA 'RevitAiRender/settings.json'
if (Test-Path -LiteralPath $legacy -PathType Leaf) {
    $previous = Get-Content -LiteralPath $legacy -Raw | ConvertFrom-Json
    if (!$SshTarget) { $SshTarget = $previous.sshTarget }
    if (!$SilicontrolPath) { $SilicontrolPath = $previous.silicontrolPath }
}
if (!$SilicontrolPath) { $SilicontrolPath = '/Applications/Siliconed.app/Contents/Helpers/silicontrol' }

$addinDirectory = Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitYear"
$manifestPath = Join-Path $addinDirectory 'RevitAiRender020.addin'
$root = Join-Path $env:LOCALAPPDATA 'RevitAiRender020'
$settingsPath = Join-Path $root 'settings.json'
$binary = Join-Path $PSScriptRoot "$RevitYear/RevitAiRender020.dll"
[xml]$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'RevitAiRender020.addin'))
$installed = Test-Path -LiteralPath $manifestPath -PathType Leaf
if ($installed) {
    if (!$Update) { throw "AI Render 0.2 is already registered. Use -Update to keep a backup and switch versions." }
    [xml]$current = [IO.File]::ReadAllText($manifestPath)
    $entry = $current.RevitAddIns.AddIn
    if ($entry.AddInId -ne $manifest.RevitAddIns.AddIn.AddInId -or
        $entry.FullClassName -ne $manifest.RevitAddIns.AddIn.FullClassName) {
        throw "The existing manifest does not belong to AI Render 0.2."
    }
}
if (!(Test-Path -LiteralPath $settingsPath) -and
    $SshTarget -notmatch '^(?:[a-zA-Z0-9._-]+@)?[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
    throw 'Provide -SshTarget as a configured SSH alias or user@host.'
}
$mfluxValues = @($MfluxPython, $MfluxCli, $MfluxModel) | Where-Object { $_ }
if (!(Test-Path -LiteralPath $settingsPath) -and $mfluxValues.Count -in @(1, 2)) {
    throw 'Provide -MfluxPython, -MfluxCli and -MfluxModel together to enable local edits.'
}
if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Plugin DLL not found: $binary" }
$installDirectory = Join-Path $root "installed/$RevitYear/$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($installDirectory) | Out-Null
[IO.Directory]::CreateDirectory($addinDirectory) | Out-Null
$dll = Join-Path $installDirectory 'RevitAiRender020.dll'
[IO.File]::Copy($binary, $dll, $false)

if (!(Test-Path -LiteralPath $settingsPath)) {
    $config = @{ sshTarget = $SshTarget; silicontrolPath = $SilicontrolPath }
    if ($mfluxValues.Count -eq 3) {
        $config.mfluxPython = $MfluxPython
        $config.mfluxCli = $MfluxCli
        $config.mfluxModel = $MfluxModel
    }
    $json = $config | ConvertTo-Json
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.File]::Open($settingsPath, [IO.FileMode]::CreateNew)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

$manifest.RevitAddIns.AddIn.Assembly = $dll
if ($installed) {
    $staged = Join-Path $installDirectory 'RevitAiRender020.pending'
    $backup = Join-Path $installDirectory 'RevitAiRender020.previous.addin'
    $stream = [IO.File]::Open($staged, [IO.FileMode]::CreateNew)
    try { $manifest.Save($stream) } finally { $stream.Dispose() }
    [IO.File]::Replace($staged, $manifestPath, $backup)
    Write-Host "Previous registration: $backup"
} else {
    $stream = [IO.File]::Open($manifestPath, [IO.FileMode]::CreateNew)
    try { $manifest.Save($stream) } finally { $stream.Dispose() }
}
Write-Host "Installed Revit AI Render 0.2.1 for Revit $RevitYear"
Write-Host $manifestPath
