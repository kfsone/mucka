#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Computes the application version from version.json + git history + Conventional Commits.
.DESCRIPTION
  Outputs a single semver string to stdout, e.g. "0.2.0-alpha.3" or "0.2.0".
  Stages map depth to pre-release number ranges:
    alpha  :       depth    (0–499)
    beta   : 500 + depth    (500–999)
    rc     : 1000 + depth   (1000–9998)
    release: no pre-release (clean M.m.P, ready for the git release tag)

  Version bump follows Conventional Commits:
    BREAKING CHANGE / type!: → major
    feat:                    → minor
    fix: / anything else     → patch

.PARAMETER Root
  Repository root. Defaults to the directory containing scripts/.
#>
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ─── Read version descriptor ────────────────────────────────────────────────
$versionFile = Join-Path $Root 'version.json'
if (-not (Test-Path $versionFile)) {
    Write-Error "version.json not found at '$versionFile'"
    exit 1
}
$info        = Get-Content $versionFile -Raw | ConvertFrom-Json
$stage       = [string]$info.stage
$baseVersion = [string]$info.baseVersion

if ($baseVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    Write-Error "version.json: baseVersion '$baseVersion' must be M.m.P (e.g. 0.1.1)"
    exit 1
}
$baseMajor = [int]$Matches[1]
$baseMinor = [int]$Matches[2]
$basePatch = [int]$Matches[3]

# ─── Depth from base release tag ────────────────────────────────────────────
$tag = "v$baseVersion"
# Use -contains for robust array membership test; trim each line to guard
# against CRLF line endings that can cause string equality to silently fail.
$tagList   = @(git -C $Root tag --list $tag 2>$null | ForEach-Object { $_.Trim() })
$tagExists = $tagList -contains $tag

if ($tagExists) {
    $depth = [int](git -C $Root rev-list "${tag}..HEAD" --count 2>$null)
} else {
    # No release tag yet — count all reachable commits as the depth
    $depth = [int](git -C $Root rev-list HEAD --count 2>$null)
}

# ─── Conventional Commits bump analysis ─────────────────────────────────────
$bumpMajor = $false
$bumpMinor = $false
$range     = if ($tagExists) { "${tag}..HEAD" } else { 'HEAD' }

# Check commit subjects for bump type
git -C $Root log $range --format='%s' 2>$null | ForEach-Object {
    # type!: or BREAKING-CHANGE in subject signals a major bump
    if ($_ -match '^[a-zA-Z]+(\([^)]*\))?!:') { $bumpMajor = $true }
    elseif ($_ -match 'BREAKING[\s-]CHANGE')  { $bumpMajor = $true }
    elseif ($_ -match '^feat(\([^)]*\))?:')   { $bumpMinor = $true }
}

# Also scan commit bodies for the BREAKING CHANGE footer token
if (-not $bumpMajor) {
    git -C $Root log $range --format='%b' 2>$null | ForEach-Object {
        if ($_ -match '^BREAKING[\s-]CHANGE:') { $bumpMajor = $true }
    }
}

# ─── Compute next version ────────────────────────────────────────────────────
if ($bumpMajor) {
    $next = "$($baseMajor + 1).0.0"
} elseif ($bumpMinor) {
    $next = "$baseMajor.$($baseMinor + 1).0"
} else {
    $next = "$baseMajor.$baseMinor.$($basePatch + 1)"
}

# ─── Map stage + depth → version string ─────────────────────────────────────
switch ($stage) {
    'alpha'   { Write-Output "$next-alpha.$([Math]::Min($depth, 499))" }
    'beta'    { Write-Output "$next-beta.$(500  + [Math]::Min($depth, 499))" }
    'rc'      { Write-Output "$next-rc.$(1000 + [Math]::Min($depth, 8998))" }
    'release' { Write-Output $next }
    default   { Write-Error "version.json: unknown stage '$stage'"; exit 1 }
}
