# Release a locally-built SourceGit build to a GitHub release.
#
# Packages build/SourceGit (EXE + DLLs, PDBs stripped) into a zip, creates an
# annotated git tag v<version>, pushes it to origin, then creates/reuses the
# GitHub release for that tag and uploads the zip as an asset.
#
#   Version  defaults to the content of the repo's VERSION file (e.g. 2026.19)
#   Runtime  defaults to win-x64, matching the CI naming sourcegit_<ver>.<runtime>.zip
#   Repo     defaults to owner/repo parsed from the origin remote URL
#   Token    :Token, else $env:GITHUB_TOKEN, else `gh auth token`, else the
#            credential stored for github.com (read via `git credential fill`).
#            A GIT_TOKEN=... line in build/scripts/.release-env takes top priority
#            (that file is git-ignored).
#
# Examples
#   ./release.win.ps1
#   ./release.win.ps1 -Draft                                     # draft release
#   ./release.win.ps1 -Version 2026.20                            # override version
#   ./release.win.ps1 -Token ghp_xxx -Proxy http://127.0.0.1:7890
#   ./release.win.ps1 -Notes "Custom release body" -DryRun        # preview only
#
# Network note: GitHub API calls (api.github.com) hit GitHub directly, not the
# git proxy. If your network cannot reach api.github.com, pass -Proxy (the same
# one your git proxy uses) or export HTTPS_PROXY.
#
[cmdletbinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [string]$Repo,
    [string]$Token,
    [string]$Proxy,
    [string]$Notes,
    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
# GitHub API over TLS 1.2 for Windows PowerShell 5.1.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Result([int]$exit) { exit $exit }

# --- resolve version / repo --------------------------------------------------
if (-not $Version) {
    $Version = ([System.IO.File]::ReadAllText((Join-Path $root 'VERSION'))).Trim()
}
$Version = $Version.TrimStart('v')
$tag = "v$Version"

if (-not $Repo) {
    $originUrl = (git -C $root remote get-url origin 2>$null)
    if ($originUrl -notmatch 'github\.com[/:]([^/]+)/([^/]+?)(\.git)?$') {
        Write-Error "Could not parse owner/repo from origin URL: $originUrl`nPass -Repo owner/name."
        Result 1
    }
    $Repo = "$($Matches[1])/$($Matches[2])"
}
Write-Host "Repo  : $Repo"
Write-Host "Tag   : $tag"
Write-Host "Ver   : $Version"

$x64Exe = Join-Path $root 'build\SourceGit\SourceGit.exe'

# --- package build/SourceGit -> zip ------------------------------------------
$zipPath = Join-Path $root "build\sourcegit_$Version.$Runtime.zip"
$srcDir  = Join-Path $root 'build\SourceGit'
if (-not (Test-Path $x64Exe)) {
    Write-Error "Missing $x64Exe`nBuild first:  dotnet publish -c Release -r $Runtime -o build/SourceGit src/SourceGit.csproj"
    Result 1
}

$tmpDir = Join-Path $env:TEMP ("sourcegit_pkg_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir | Out-Null
try {
    Copy-Item (Join-Path $srcDir '*') $tmpDir -Recurse -Force
    Get-ChildItem $tmpDir -Filter *.pdb -Recurse | Remove-Item -Force
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $tmpDir -DestinationPath $zipPath -CompressionLevel Optimal
} finally {
    Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}
$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "Package: $zipPath ($zipSize MB)"

# --- token resolution --------------------------------------------------------
function Resolve-Token {
    if ($Token) { return $Token }
    # Prefer GIT_TOKEN in build/scripts/.release-env (kept out of git via .gitignore).
    $envFile = Join-Path $PSScriptRoot '.release-env'
    if (Test-Path $envFile) {
        foreach ($line in Get-Content $envFile) {
            if ($line -match '^\s*GIT_TOKEN\s*=\s*(.+)\s*$') { return $Matches[1] }
        }
    }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    try { $gh = Get-Command gh -ErrorAction Stop; $t = (& gh auth token 2>$null) | Select-Object -First 1; if ($t) { return $t } } catch { }
    # Last resort: read the credential stored for github.com (host is not the proxy).
    $entry = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    if ($entry -match 'password=(.+)') { return $Matches[1] }
    return $null
}
$token = Resolve-Token
if (-not $token) {
    Write-Error "No GitHub token. Export GITHUB_TOKEN, or run:  git credential approve < ~/.git-credentials"
    Result 1
}

# --- GitHub API helper -------------------------------------------------------
function Invoke-GhApi {
    param([string]$Method, [string]$Uri, [object]$Body, [string]$InFile, [string]$InType)
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; ErrorAction = 'Stop' }
    if ($Proxy) { $params.Proxy = $Proxy }
    if ($Body) { $params.Body = ($Body | ConvertTo-Json -Depth 6 -Compress); $params.ContentType = 'application/json' }
    if ($InFile) { $params.InFile = $InFile; $params.ContentType = $InType }
    try {
        return Invoke-RestMethod @params
    } catch {
        $status = $null; $msg = $_.Exception.Message
        try { $status = [int]$_.Exception.Response.StatusCode } catch { }
        if ($status -eq 404 -and $Method -eq 'GET' -and $Uri -like '*releases/tags/*') { return $null }
        try {
            if ($_.Exception.Response.Content) {
                $msg = $_.Exception.Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            } elseif ($_.Exception.Response.GetResponseStream()) {
                $r = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream()); $msg = $r.ReadToEnd()
            }
        } catch { }
        throw "GitHub API $Method $Uri`nHTTP $status`n$msg"
    }
}

# --- create local tag if missing ---------------------------------------------
$sha = (git -C $root rev-parse HEAD).Trim()
# Peel annotated tags to their commit so we compare commit to commit.
$localTagSha = git -C $root rev-parse -q --verify "refs/tags/$tag^{commit}" 2>$null
if (-not $localTagSha) {
    if (-not $DryRun) {
        git -C $root tag -a $tag $sha -m "Release $Version"
    }
    Write-Host "Set up local tag $tag -> $sha"
} elseif ($localTagSha -ne $sha) {
    Write-Error "Local tag $tag already points to $localTagSha (not HEAD $sha). Delete it or reset it first."
    Result 1
} else {
    Write-Host "Local tag $tag already up to date"
}

# --- push tag ----------------------------------------------------------------
# git will refuse to overwrite an existing remote tag pointing elsewhere, so we
# just push and inspect the result. If the tag already exists and is identical,
# git reports "everything up-to-date" which we treat as success.
if (-not $DryRun) {
    $push = git -C $root push origin "refs/tags/$tag" 2>&1
    $push | ForEach-Object { Write-Host "  $_" }
    $remoteTagSha = git -C $root ls-remote --tags origin "refs/tags/$tag" 2>$null | ForEach-Object { ($_ -split '\t')[0] }
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to push tag $tag (it may already exist on origin pointing to a different commit)."
        Result 1
    }
    if ($remoteTagSha) {
        Write-Host "Remote tag $tag is now up to date"
    } else {
        Write-Host "Pushed tag $tag"
    }
} else {
    $remoteTagSha = git -C $root ls-remote --tags origin "refs/tags/$tag" 2>$null | ForEach-Object { ($_ -split '\t')[0] }
    if ($remoteTagSha) {
        Write-Host "Remote tag $tag already exists"
    } else {
        Write-Host "Pushing tag $tag ..."
    }
}

# --- release notes -----------------------------------------------------------
if (-not $Notes) {
    $prev = git -C $root describe --tags --abbrev=0 "$sha^" 2>$null
    if ($prev) {
        $log = (git -C $root log --oneline --no-merges "$prev..$sha") -join "`n"
        $Notes = "Release $Version`n`nChanges since $prev`n$log"
    } else {
        $Notes = "Release $Version"
    }
}
if ($DryRun) { Write-Host "DRY-RUN: would create release for $tag and upload $zipPath"; Result 0 }

# --- create / reuse release --------------------------------------------------
$api = "https://api.github.com/repos/$Repo"
$release = Invoke-GhApi -Method GET -Uri "$api/releases/tags/$tag"
if (-not $release) {
    Write-Host "Creating release $tag ..."
    $body = @{
        tag_name = $tag
        target_commitish = $sha
        name = $Version
        body = $Notes
        draft = [bool]$Draft
        prerelease = [bool]$Prerelease
    }
    $release = Invoke-GhApi -Method POST -Uri "$api/releases" -Body $body
} else {
    Write-Host "Release $tag already exists (id $($release.id)); will reuse it."
}

# --- upload asset (replace existing asset of the same name) ------------------
$assetName = Split-Path $zipPath -Leaf
foreach ($a in @($release.assets)) {
    if ($a.name -eq $assetName) {
        Write-Host "Removing existing asset $assetName ..."
        Invoke-GhApi -Method DELETE -Uri "$api/releases/assets/$($a.id)" | Out-Null
    }
}
Write-Host "Uploading $assetName ..."
$uploadUrl = ($release.upload_url -replace '\{\?.*$', '') + "?name=$assetName"
Invoke-GhApi -Method POST -Uri $uploadUrl -InFile $zipPath -InType 'application/zip' | Out-Null
Write-Host "Done: https://github.com/$Repo/releases/tag/$tag"

Result 0
