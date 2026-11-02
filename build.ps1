#requires -Version 5.1
[CmdletBinding(PositionalBinding = $false)]
param(
    [Alias('Tag')]
    [ValidateNotNullOrEmpty()]
    [string] $ImageTag = $(if ($env:MAHJONG_IMAGE) { $env:MAHJONG_IMAGE } else { 'mahjong-unity:local' }),
    [ValidateNotNullOrEmpty()]
    [string] $Platform = $(if ($env:MAHJONG_PLATFORM) { $env:MAHJONG_PLATFORM } else { 'linux/amd64' }),
    [string] $Archive,
    [switch] $NoCache,
    [switch] $SkipUnityBuild,
    [switch] $AllowSourceDirty,
    [switch] $Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ($Help) {
    Write-Output 'Usage: ./build.ps1 [-Tag IMAGE] [-Platform linux/ARCH] [-Archive FILE] [-NoCache] [-SkipUnityBuild] [-AllowSourceDirty]'
    Write-Output 'Builds and loads a Linux image locally. Does not deploy or push.'
    Write-Output 'Defaults: MAHJONG_IMAGE=mahjong-unity:local, MAHJONG_PLATFORM=linux/amd64.'
    Write-Output 'Only Docker with Buildx/Compose is required on the host; the pinned build container runs actual Unity tests/build.'
    Write-Output 'Provide a private, pre-provisioned UNITY_LICENSE_FILE or UNITY_LICENSE_DIR outside the checkout. No activation is automated.'
    Write-Output '-SkipUnityBuild consumes existing REAL artifacts; Docker still validates the manifest, pins, sizes, digests and wasm magic.'
    Write-Output '-AllowSourceDirty is local qualification only; development/toolchain/integrity checks are never bypassed.'
    Write-Output 'Build identity defaults to Git commit[-dirty]-UTC timestamp (or local-source-unavailable-timestamp).'
    Write-Output 'BUILD_SHA overrides that public identity exactly; no runtime secrets are needed.'
    Write-Output '-Archive writes raw tar; use gzip separately for a .tar.gz archive.'
    exit 0
}
if ($Platform -notmatch '^linux/[^,/]+(/[^,/]+)?$') {
    Write-Error 'Specify one Linux platform, for example linux/amd64 or linux/arm64.' -ErrorAction Continue
    exit 2
}
if ($Archive -and (Test-Path -LiteralPath $Archive)) {
    Write-Error "Archive already exists; refusing to overwrite: $Archive" -ErrorAction Continue
    exit 2
}
$allow = if ($AllowSourceDirty) { '1' } elseif ($env:ALLOW_SOURCE_DIRTY) { $env:ALLOW_SOURCE_DIRTY } else { '0' }
if ($allow -notin @('0', '1')) {
    Write-Error 'ALLOW_SOURCE_DIRTY must be exactly 0 or 1.' -ErrorAction Continue
    exit 2
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error 'Docker with the Buildx plugin is required.' -ErrorAction Continue
    exit 127
}
& docker buildx version > $null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipUnityBuild) {
    if ($env:UNITY_LICENSE_FILE -and $env:UNITY_LICENSE_DIR) {
        Write-Error 'Set only ONE of UNITY_LICENSE_FILE or UNITY_LICENSE_DIR.' -ErrorAction Continue
        exit 2
    }
    $privateSource = $null
    $privateTarget = '/workspace/artifacts/unity-builder-home/.local/share/unity3d/Unity/Unity_lic.ulf'
    if ($env:UNITY_LICENSE_FILE) {
        if (-not [IO.Path]::IsPathRooted($env:UNITY_LICENSE_FILE) -or
            -not (Test-Path -LiteralPath $env:UNITY_LICENSE_FILE -PathType Leaf) -or
            [IO.Path]::GetExtension($env:UNITY_LICENSE_FILE) -ne '.ulf') {
            Write-Error 'UNITY_LICENSE_FILE must name an existing absolute private .ulf file.' -ErrorAction Continue
            exit 1
        }
        $item = Get-Item -LiteralPath $env:UNITY_LICENSE_FILE
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Write-Error 'UNITY_LICENSE_FILE must not be a symlink.' -ErrorAction Continue
            exit 1
        }
        $privateSource = $item.FullName
    } elseif ($env:UNITY_LICENSE_DIR) {
        if (-not [IO.Path]::IsPathRooted($env:UNITY_LICENSE_DIR) -or
            -not (Test-Path -LiteralPath $env:UNITY_LICENSE_DIR -PathType Container)) {
            Write-Error 'UNITY_LICENSE_DIR must name an existing absolute private pre-provisioned Unity licensing directory.' -ErrorAction Continue
            exit 1
        }
        $directoryItem = Get-Item -LiteralPath $env:UNITY_LICENSE_DIR
        if ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Write-Error 'UNITY_LICENSE_DIR must not be a symlink.' -ErrorAction Continue
            exit 1
        }
        $privateSource = $directoryItem.FullName
        $privateTarget = '/workspace/artifacts/unity-builder-home/.local/share/unity3d/Unity'
    } else {
        Write-Error 'Unity build blocked: provide privately provisioned valid UNITY_LICENSE_FILE or UNITY_LICENSE_DIR outside the checkout. Docker supplies tools, not activation. Personal activation still needs the supported Unity Hub/operator process.' -ErrorAction Continue
        exit 1
    }
    $rootPrefix = $PSScriptRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($privateSource.Equals($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $privateSource.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Error 'Licensing must stay outside the checkout; source/build-context license files are not accepted.' -ErrorAction Continue
        exit 1
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.git') -PathType Container)) {
        Write-Error 'An actual Git checkout is required for renderer metadata; host Git is not required.' -ErrorAction Continue
        exit 1
    }
    & docker compose version > $null
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & docker info > $null
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    foreach ($directory in @('artifacts/unity-builder-home', 'artifacts/tool-work', 'artifacts/test-results')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot $directory) > $null
    }
    $emptyEnv = Join-Path $PSScriptRoot 'artifacts/tool-work/unity-compose-empty.env'
    [IO.File]::WriteAllText($emptyEnv, '')
    $env:UNITY_LICENSE_MOUNT_SOURCE = $privateSource
    $env:UNITY_LICENSE_MOUNT_TARGET = $privateTarget
    if (-not $env:UNITY_BUILD_UID) { $env:UNITY_BUILD_UID = '1000' }
    if (-not $env:UNITY_BUILD_GID) { $env:UNITY_BUILD_GID = '1000' }
    $env:ALLOW_SOURCE_DIRTY = $allow
    & docker compose --env-file $emptyEnv --project-name mahjong-unity-build --profile unity-build `
        -f (Join-Path $PSScriptRoot 'docker-compose.unity-build.yml') `
        run --build --rm --no-deps unity-builder --test-and-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$buildSha = $env:BUILD_SHA
if ($null -eq $buildSha) {
    $buildTime = [DateTime]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $buildSha = "local-source-unavailable-$buildTime"
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $commit = $null
        try {
            $candidate = & git -C $PSScriptRoot rev-parse --verify HEAD 2>$null
            if ($LASTEXITCODE -eq 0) { $commit = $candidate }
        } catch {
            # Older PowerShell can throw on native stderr for a source export without .git.
        }
        if ($null -ne $commit) {
            $changes = & git --no-optional-locks -C $PSScriptRoot status --porcelain --untracked-files=normal
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            $dirty = if ($changes) { '-dirty' } else { '' }
            $buildSha = "$commit$dirty-$buildTime"
        }
    }
}
Write-Output "Build identity: $buildSha"
$buildArguments = @(
    'buildx', 'build', '--load', '--platform', $Platform, '--tag', $ImageTag,
    '--file', (Join-Path $PSScriptRoot 'Dockerfile'), '--build-arg', "BUILD_SHA=$buildSha",
    '--build-arg', "ALLOW_SOURCE_DIRTY=$allow"
)
if ($NoCache) { $buildArguments += '--no-cache' }
$buildArguments += $PSScriptRoot
& docker @buildArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$imageOs = & docker image inspect $ImageTag --format '{{.Os}}'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($imageOs -ne 'linux') {
    Write-Error "Expected a locally loaded Linux image; got OS=$imageOs" -ErrorAction Continue
    exit 1
}
& docker image inspect $ImageTag --format 'Built {{.Id}} ({{.Os}}/{{.Architecture}})'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Archive) {
    & docker image save --output $Archive $ImageTag
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output "Saved image archive: $Archive"
}
