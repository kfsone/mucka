#!/usr/bin/env pwsh
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$info = Get-Content (Join-Path $Root 'version.json') -Raw | ConvertFrom-Json
Write-Output ([string]$info.version)

