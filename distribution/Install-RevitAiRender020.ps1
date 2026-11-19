#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('2024','2025')][string]$RevitYear,
    [switch]$Update,
    [switch]$AfterRevitCloses,
    [switch]$EnableMcp,
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
$installRoot = [IO.Path]::GetFullPath((Join-Path $root "installed/$RevitYear"))
$installed = Test-Path -LiteralPath $manifestPath -PathType Leaf
if ($installed) {
    if (!$Update) { throw 'AI Render is already registered. Use -Update.' }
    $manifestBefore = [IO.File]::ReadAllText($manifestPath)
    [xml]$current = $manifestBefore
    $entry = $current.RevitAddIns.AddIn
    if ($entry.AddInId -ne $manifest.RevitAddIns.AddIn.AddInId -or
        $entry.FullClassName -ne $manifest.RevitAddIns.AddIn.FullClassName) {
        throw 'The existing manifest does not belong to AI Render.'
    }
    $dll = [IO.Path]::GetFullPath([string]$entry.Assembly)
    $slot = Split-Path -Parent $dll
    if ([IO.Path]::GetFileName($dll) -ne 'RevitAiRender020.dll' -or
        (Split-Path -Parent $slot) -ne $installRoot -or
        (Split-Path -Leaf $slot) -notmatch '^(active|[a-f0-9]{32})$' -or
        !(Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'The existing assembly is outside the AI Render installation or is missing.'
    }
} else {
    $dll = Join-Path $installRoot 'active/RevitAiRender020.dll'
}
$mfluxValues = @($MfluxPython, $MfluxCli, $MfluxModel) | Where-Object { $_ }
if (!(Test-Path -LiteralPath $settingsPath) -and $mfluxValues.Count -in @(1, 2)) {
    throw 'Provide -MfluxPython, -MfluxCli and -MfluxModel together to enable local edits.'
}
if (!(Test-Path -LiteralPath $settingsPath) -and !$SshTarget -and $mfluxValues.Count -gt 0) {
    throw 'Provide -SshTarget with the MFLUX paths, or configure the connection in Revit.'
}
if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Plugin DLL not found: $binary" }
$mcpBinary = Join-Path $PSScriptRoot 'mcp/RevitAiRender.Mcp.exe'
if ($EnableMcp -and !(Test-Path -LiteralPath $mcpBinary -PathType Leaf)) { throw "MCP executable not found: $mcpBinary" }
[IO.Directory]::CreateDirectory($installRoot) | Out-Null
$installLock = [IO.File]::Open((Join-Path $installRoot 'install.lock'), [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $registered = Test-Path -LiteralPath $manifestPath -PathType Leaf
    if ($registered -ne $installed -or
        ($installed -and [IO.File]::ReadAllText($manifestPath) -cne $manifestBefore)) {
        throw 'The registration changed. Rerun the installer to use the current registration.'
    }

    $running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
    if ($installed -and $running.Count -gt 0 -and !$AfterRevitCloses) {
        throw 'Revit is still open. Use -AfterRevitCloses to apply the update when you close Revit.'
    }

    $operation = Join-Path $installRoot "backups/$([guid]::NewGuid().ToString('N'))"
    [IO.Directory]::CreateDirectory($operation) | Out-Null
    $pending = Join-Path $operation 'RevitAiRender020.pending'
    [IO.File]::Copy($binary, $pending, $false)
    if ($EnableMcp) {
        $mcpExe = Join-Path $operation 'RevitAiRender.Mcp.exe'
        [IO.File]::Copy($mcpBinary, $mcpExe, $false)
    }

    if ($installed -and $running.Count -gt 0) {
        Write-Host 'Update staged. It will apply when you close all Revit windows. Leave this PowerShell open.'
        do {
            $running | Wait-Process -ErrorAction SilentlyContinue
            $running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
        } while ($running.Count -gt 0)
        if ([IO.File]::ReadAllText($manifestPath) -cne $manifestBefore) {
            throw 'The registration changed while waiting. Rerun the installer.'
        }
    }

    if (!(Test-Path -LiteralPath $settingsPath) -and $SshTarget) {
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
    if ($installed) {
        $backup = Join-Path $operation 'RevitAiRender020.previous.dll'
        [IO.File]::Replace($pending, $dll, $backup)
        Write-Host "Previous assembly: $backup"
    } else {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $dll)) | Out-Null
        [IO.Directory]::CreateDirectory($addinDirectory) | Out-Null
        if (Test-Path -LiteralPath $dll -PathType Leaf) {
            if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath $pending).Hash) {
                throw 'The installation path already contains a different assembly.'
            }
        } else {
            [IO.File]::Move($pending, $dll)
        }

        $manifest.SelectSingleNode('/RevitAddIns/AddIn/Assembly').InnerText = [string]$dll
        $stagedManifest = Join-Path $addinDirectory "RevitAiRender020.$([guid]::NewGuid().ToString('N')).pending"
        $stream = [IO.File]::Open($stagedManifest, [IO.FileMode]::CreateNew)
        try { $manifest.Save($stream) } finally { $stream.Dispose() }
        [IO.File]::Move($stagedManifest, $manifestPath)
    }

    if ($EnableMcp) {
        $marker = Join-Path $root 'mcp.enabled'
        [IO.File]::Open($marker, [IO.FileMode]::OpenOrCreate).Dispose()
        Write-Host "MCP executable: $mcpExe"
    }
    Write-Host "AI Render will load the installed version at the next Revit $RevitYear launch."
    Write-Host $manifestPath
} finally {
    $installLock.Dispose()
}
