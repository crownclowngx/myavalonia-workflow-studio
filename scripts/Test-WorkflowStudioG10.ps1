[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$ReuseVerifiedG7
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\test-results'))
$resultRoot = [IO.Path]::GetFullPath((Join-Path $allowedRoot 'WorkflowStudioG10'))
$g7SummaryPath = Join-Path $allowedRoot 'WorkflowStudioG7\summary.json'

function Assert-True {
    param([Parameter(Mandatory)] [bool]$Condition, [Parameter(Mandatory)] [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Checked {
    param([Parameter(Mandatory)] [string]$FilePath, [Parameter(Mandatory)] [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') 失败，退出码：$LASTEXITCODE。"
    }
}

$prefix = $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
Assert-True ($resultRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) `
    'WorkflowStudio G10 结果目录越过 artifacts/test-results。'
if (Test-Path -LiteralPath $resultRoot) {
    Remove-Item -LiteralPath $resultRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resultRoot | Out-Null

Push-Location $repositoryRoot
try {
    # G10 包装层只复用 G7 已拥有的还原、构建、测试、覆盖率与包规则。Release 仅是
    # 编译配置；本入口不读取 AIFLOW，也不调用 Windows CI/Smoke 或任何发布入口。
    if (-not $ReuseVerifiedG7) {
        Invoke-Checked pwsh @(
            '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-WorkflowStudioG7.ps1'),
            '-Configuration', $Configuration)
    }
    Assert-True (Test-Path -LiteralPath $g7SummaryPath -PathType Leaf) `
        'WorkflowStudio G10 缺少可复用的 G7 summary.json。'
    $g7 = Get-Content -Raw -LiteralPath $g7SummaryPath | ConvertFrom-Json
    Assert-True ($g7.stage -ceq 'WorkbenchCommandG7') 'WorkflowStudio G7 阶段身份漂移。'
    Assert-True ($g7.configuration -ceq $Configuration) 'WorkflowStudio G7 编译配置漂移。'
    Assert-True (
        [int]$g7.tests.passed -ge 54 -and
        [int]$g7.tests.failed -eq 0 -and
        [int]$g7.tests.skipped -eq 0) 'WorkflowStudio G7 测试未达到 54 项、零失败、零跳过。'
    Assert-True ([double]$g7.lineCoverage -ge 85 -and [double]$g7.branchCoverage -ge 75) `
        'WorkflowStudio G7 覆盖率低于既有 85% / 75% 门槛。'
    Assert-True ([double]$g7.mainDocumentLineCoverage -ge 90) `
        'WorkflowStudio MainDocument 行覆盖率低于 90%。'
    Assert-True (
        [int]$g7.deterministicBuilds -eq 2 -and
        [string]$g7.archiveSha256 -match '^[0-9A-F]{64}$') `
        'WorkflowStudio G7 缺少两次确定性 ZIP 或规范 SHA-256。'
    Assert-True (
        [int]$g7.manifest.schemaVersion -eq 2 -and
        $g7.manifest.pluginId -ceq 'myavalonia.plugin.workflow-studio' -and
        $g7.manifest.pluginVersion -ceq '1.2.0' -and
        $g7.manifest.sdk.minInclusive -ceq '3.3.0' -and
        $g7.manifest.sdk.maxExclusive -ceq '4.0.0') `
        'WorkflowStudio manifest schema、身份、版本或 SDK 区间漂移。'
    foreach ($flag in @(
            'aiflow', 'windowsCi', 'windowsSmoke', 'releaseAcceptance', 'releaseGate',
            'publishable', 'published', 'uploaded', 'signed', 'tagCreated')) {
        Assert-True ($g7.PSObject.Properties[$flag] -and -not [bool]$g7.$flag) `
            "WorkflowStudio G7 非发布标记 $flag 必须为 false。"
    }

    $history = Join-Path $repositoryRoot `
        'docs\plan-history\workbench-command\g10-workflow-studio-local-sealing.md'
    Assert-True (Test-Path -LiteralPath $history -PathType Leaf) `
        'WorkflowStudio 缺少 G10 专项记录。'
    $historyText = Get-Content -Raw -LiteralPath $history
    foreach ($fragment in @(
            'SOLID', 'Test-WorkflowStudioG10.ps1', 'aiflow=false',
            'windowsSmoke=false', 'releaseGate=false', 'publishable=false')) {
        Assert-True ($historyText.Contains($fragment, [StringComparison]::OrdinalIgnoreCase)) `
            "WorkflowStudio G10 专项记录缺少：$fragment。"
    }

    $summary = [ordered]@{
        schemaVersion = 1
        stage = 'WorkbenchCommandG10'
        repository = 'WorkflowStudio'
        configuration = $Configuration
        g7Reused = [bool]$ReuseVerifiedG7
        tests = $g7.tests
        lineCoverage = [double]$g7.lineCoverage
        branchCoverage = [double]$g7.branchCoverage
        mainDocumentLineCoverage = [double]$g7.mainDocumentLineCoverage
        archiveSha256 = [string]$g7.archiveSha256
        packageFiles = [int]$g7.packageFiles
        deterministicBuilds = [int]$g7.deterministicBuilds
        hostInputRoot = [string]$g7.hostInputRoot
        manifest = $g7.manifest
        passed = $true
        aiflow = $false
        windowsCi = $false
        windowsSmoke = $false
        releaseAcceptance = $false
        releaseGate = $false
        publishable = $false
        published = $false
        uploaded = $false
        signed = $false
        tagCreated = $false
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText(
        (Join-Path $resultRoot 'summary.json'),
        ($summary | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
    Write-Host (
        "WorkflowStudio G10 本地封板通过：$($g7.tests.passed) 项，" +
        "覆盖率 $($g7.lineCoverage)% / $($g7.branchCoverage)%。")
}
finally {
    Pop-Location
}
