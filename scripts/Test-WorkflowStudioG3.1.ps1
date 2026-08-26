[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [Parameter(Mandatory)]
    [string]$CandidateHostRoot,
    [string]$CandidateFeed,
    [switch]$PublicOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cacheName = if ($PublicOnly) { 'g31-public' } else { 'g31-candidate' }
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\nuget-cache\$cacheName"))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\nuget-cache'))
if (-not $cacheRoot.StartsWith(
        $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "G3.1 NuGet 缓存目录越界：$cacheRoot。"
}
if ($PublicOnly -and -not [string]::IsNullOrWhiteSpace($CandidateFeed)) {
    throw 'PublicOnly 复验不得提供候选 feed。'
}
if (-not $PublicOnly -and [string]::IsNullOrWhiteSpace($CandidateFeed)) {
    throw '候选阶段必须提供 -CandidateFeed；发布后纯公开复验请使用 -PublicOnly。'
}
if (Test-Path -LiteralPath $cacheRoot) {
    Remove-Item -LiteralPath $cacheRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

$previousPackages = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = $cacheRoot
    & (Join-Path $PSScriptRoot 'Test-WorkflowStudioG3.ps1') `
        -Configuration $Configuration `
        -CandidateHostRoot $CandidateHostRoot `
        -CandidateFeed $(if ($PublicOnly) { '' } else { $CandidateFeed })
    if ($LASTEXITCODE -ne 0) {
        throw "Workflow Studio G3.1 内部门禁失败，退出码：$LASTEXITCODE。"
    }
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
}
