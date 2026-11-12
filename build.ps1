#requires -Version 5.1
[CmdletBinding(PositionalBinding = $false)]
param(
    [Alias('Tag')][string] $ImageTag = $(if ($env:MAHJONG_IMAGE) { $env:MAHJONG_IMAGE } else { 'mahjong-web:local' }),
    [string] $Platform = $(if ($env:MAHJONG_PLATFORM) { $env:MAHJONG_PLATFORM } else { 'linux/amd64' }),
    [string] $Archive, [switch] $NoCache, [switch] $Help
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Help) {
    Write-Output 'Usage: ./build.ps1 [-Tag IMAGE] [-Platform linux/ARCH] [-Archive FILE] [-NoCache]'
    Write-Output 'Builds the WebGL2/.NET image from source with Docker Buildx. No Editor/player/activation required. Does not publish.'
    exit 0
}
if ($Platform -notmatch '^linux/[^,/]+(/[^,/]+)?$') { throw 'Choose one Linux platform.' }
if ($Archive -and (Test-Path -LiteralPath $Archive)) { throw 'Archive already exists.' }
& docker buildx version > $null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$sha = $env:BUILD_SHA
if (-not $sha) {
    $sha = 'local'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $head = & git -C $PSScriptRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $sha = $head
            $changes = & git -C $PSScriptRoot status --porcelain --untracked-files=normal
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            if ($changes) { $sha += '-dirty' }
        }
    }
}
$arguments = @('buildx', 'build', '--load', '--platform', $Platform, '--tag', $ImageTag,
    '--file', (Join-Path $PSScriptRoot 'Dockerfile'), '--build-arg', "BUILD_SHA=$sha")
if ($NoCache) { $arguments += '--no-cache' }
$arguments += $PSScriptRoot
& docker @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& docker image inspect $ImageTag --format 'Built {{.Id}} ({{.Os}}/{{.Architecture}})'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Archive) {
    & docker image save --output $Archive $ImageTag
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
