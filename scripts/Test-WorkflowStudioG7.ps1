[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$ReuseNuGetCache
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resultRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\test-results\WorkflowStudioG7'))
$allowedResultRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\test-results'))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\nuget-cache\g7-public'))
$allowedCacheRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\nuget-cache'))
$solution = Join-Path $repositoryRoot 'WorkflowStudio.slnx'
$pluginProject = Join-Path $repositoryRoot 'src\WorkflowStudio.Plugin\WorkflowStudio.Plugin.csproj'
$standaloneProject = Join-Path $repositoryRoot 'src\WorkflowStudio.Standalone\WorkflowStudio.Standalone.csproj'
$testProject = Join-Path $repositoryRoot 'tests\WorkflowStudio.Tests\WorkflowStudio.Tests.csproj'

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Label)
    $prefix = $Parent.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $Candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label 目录越界：$Candidate。"
    }
}

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Checked {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') 失败，退出码：$LASTEXITCODE。"
    }
}

function Get-TrxCounts {
    param([Parameter(Mandatory)][string]$Path)
    [xml]$trx = Get-Content -Raw -LiteralPath $Path
    $counters = $trx.TestRun.ResultSummary.Counters
    return [ordered]@{
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        skipped = [int]$counters.notExecuted
    }
}

function Get-FileLineCoverage {
    param([Parameter(Mandatory)][xml]$Coverage, [Parameter(Mandatory)][string]$FileName)
    $lines = @($Coverage.coverage.packages.package.classes.class |
        Where-Object { $_.filename -ceq $FileName } |
        ForEach-Object { $_.lines.line } |
        Group-Object number |
        ForEach-Object {
            [pscustomobject]@{
                Hits = [int](($_.Group | Measure-Object -Property hits -Maximum).Maximum)
            }
        })
    Assert-True ($lines.Count -gt 0) "覆盖率报告缺少关键文件：$FileName。"
    $covered = @($lines | Where-Object { $_.Hits -gt 0 }).Count
    return [Math]::Round(100 * $covered / $lines.Count, 2)
}

function Read-ZipEntries {
    param([Parameter(Mandatory)][string]$Path)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($archive.Entries | ForEach-Object FullName) }
    finally { $archive.Dispose() }
}

function Read-ZipText {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$EntryName)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) { throw "ZIP 缺少条目：$EntryName。" }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Test-MarkdownLinks {
    $markdownFiles = Get-ChildItem -LiteralPath $repositoryRoot -Filter '*.md' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts|TestResults)[\\/]' }
    foreach ($file in $markdownFiles) {
        $text = Get-Content -Raw -LiteralPath $file.FullName
        foreach ($match in [regex]::Matches(
                $text,
                '\[[^\]]+\]\((?!https?://|#)(?<path>[^)#]+)(?:#[^)]+)?\)')) {
            $relative = [Uri]::UnescapeDataString($match.Groups['path'].Value)
            $target = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $relative))
            Assert-True (Test-Path -LiteralPath $target) `
                "文档链接失效：$($file.FullName) -> $relative。"
        }
    }
}

Assert-ChildPath $resultRoot $allowedResultRoot 'G7 结果'
Assert-ChildPath $cacheRoot $allowedCacheRoot 'G7 NuGet 缓存'
Assert-True ((Split-Path -Leaf $repositoryRoot) -ceq 'myavalonia-workflow-studio') `
    'G7 必须从独立 myavalonia-workflow-studio 仓库根执行。'

if (Test-Path -LiteralPath $resultRoot) {
    Remove-Item -LiteralPath $resultRoot -Recurse -Force
}
if ((Test-Path -LiteralPath $cacheRoot) -and -not $ReuseNuGetCache) {
    Remove-Item -LiteralPath $cacheRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resultRoot, $cacheRoot -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

$previousPackages = [Environment]::GetEnvironmentVariable('NUGET_PACKAGES', 'Process')
try {
    $env:NUGET_PACKAGES = $cacheRoot
    $nugetConfig = Join-Path $resultRoot 'NuGet.G7.config'
    $nugetText = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@
    [IO.File]::WriteAllText($nugetConfig, $nugetText, [Text.UTF8Encoding]::new($false))

    Push-Location $repositoryRoot
    try {
        # G7 是本地开发门禁。Release 只是编译配置；本脚本不读取 AIFLOW，也不调用
        # Windows CI/Smoke、Release Acceptance、发布门禁、签名、上传或 tag。
        $allProductionText = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') `
            -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]' } |
            ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }
        Assert-True (-not (($allProductionText -join "`n") -match
                'avalonia_dock_simple_test|<ProjectReference[^>]*MyAvaloniaManagement')) `
            '外部 Studio 生产源码出现 Host 路径或源码 ProjectReference。'

        $packagesText = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props')
        foreach ($fragment in @(
                'MyAvaloniaManagement.PluginSdk" Version="[3.3.0]"',
                'MyAvaloniaManagement.PluginSdk.UI" Version="[3.3.0]"',
                'MyAvaloniaManagement.PluginSdk.Workflow" Version="[1.0.0]"',
                'MyAvaloniaManagement.Plugin.Build" Version="[1.1.2]"')) {
            Assert-True ($packagesText.Contains($fragment, [StringComparison]::Ordinal)) `
                "G7 精确包引用缺失：$fragment。"
        }
        $pluginText = Get-Content -Raw -LiteralPath $pluginProject
        Assert-True ($pluginText -match '<PluginVersion>1\.2\.0</PluginVersion>') `
            'WorkflowStudio G7 插件版本必须为 1.2.0。'
        Assert-True ($pluginText -match
                '<ManagedPluginSdkMinInclusive>3\.3\.0</ManagedPluginSdkMinInclusive>') `
            'WorkflowStudio G7 manifest SDK 下界必须为 3.3.0。'
        $commandDesignPath = Join-Path $repositoryRoot 'docs\workbench-commands.md'
        $g7HistoryPath = Join-Path $repositoryRoot `
            'docs\plan-history\workbench-command\g7-workflow-studio-three-real-commands.md'
        Assert-True (Test-Path -LiteralPath $commandDesignPath -PathType Leaf) `
            'G7 缺少 Workflow Studio Workbench Command 设计文档。'
        Assert-True (Test-Path -LiteralPath $g7HistoryPath -PathType Leaf) `
            'G7 缺少 Workflow Studio 专用实施记录。'
        $commandDesign = Get-Content -Raw -LiteralPath $commandDesignPath
        foreach ($fragment in @(
                'myavalonia.plugin.workflow-studio.command.validate',
                'myavalonia.plugin.workflow-studio.command.run',
                'myavalonia.plugin.workflow-studio.command.cancel',
                'IWorkbenchDocumentCommandTarget',
                'aiflow')) {
            Assert-True ($commandDesign.Contains($fragment, [StringComparison]::OrdinalIgnoreCase)) `
                "G7 Command 设计文档缺少事实：$fragment。"
        }

        Invoke-Checked dotnet @(
            'restore', $solution, '--locked-mode', '--configfile', $nugetConfig,
            '--packages', $cacheRoot, '--nologo')
        Invoke-Checked dotnet @(
            'build', $solution, '-c', $Configuration, '--no-restore', '-warnaserror',
            '-m:1', '--nologo')
        Invoke-Checked dotnet @(
            'format', $solution, '--verify-no-changes', '--no-restore', '--verbosity', 'minimal')

        $testRoot = Join-Path $resultRoot 'tests'
        Invoke-Checked dotnet @(
            'test', $testProject, '-c', $Configuration, '--no-build', '--no-restore',
            '--collect:XPlat Code Coverage', '--results-directory', $testRoot,
            '--logger', 'trx;LogFileName=WorkflowStudioG7.trx')
        $tests = Get-TrxCounts (Join-Path $testRoot 'WorkflowStudioG7.trx')
        Assert-True ($tests.failed -eq 0) 'WorkflowStudio G7 单元测试存在失败。'
        Assert-True ($tests.skipped -eq 0) 'WorkflowStudio G7 单元测试存在跳过。'
        Assert-True ($tests.passed -ge 54) 'WorkflowStudio G7 单元测试低于新增 Target 后的 54 项基线。'

        $coveragePath = (Get-ChildItem -LiteralPath $testRoot -Filter 'coverage.cobertura.xml' `
            -File -Recurse | Select-Object -First 1).FullName
        Assert-True (-not [string]::IsNullOrWhiteSpace($coveragePath)) 'G7 没有生成 Cobertura 覆盖率。'
        [xml]$coverage = Get-Content -Raw -LiteralPath $coveragePath
        $lineCoverage = [Math]::Round([double]$coverage.coverage.'line-rate' * 100, 2)
        $branchCoverage = [Math]::Round([double]$coverage.coverage.'branch-rate' * 100, 2)
        $mainDocumentCoverage = Get-FileLineCoverage $coverage 'Features\Main\MainDocument.cs'
        Assert-True ($lineCoverage -ge 85) "G7 行覆盖率 $lineCoverage% 低于 85%。"
        Assert-True ($branchCoverage -ge 75) "G7 分支覆盖率 $branchCoverage% 低于 75%。"
        Assert-True ($mainDocumentCoverage -ge 90) `
            "MainDocument 行覆盖率 $mainDocumentCoverage% 低于 90%。"

        $selfTestLog = Join-Path $resultRoot 'standalone-self-test.log'
        $selfOutput = @(& dotnet run --project $standaloneProject -c $Configuration `
            --no-build --no-restore -- --g3-self-test 2>&1)
        $selfOutput | Set-Content -LiteralPath $selfTestLog -Encoding utf8NoBOM
        Assert-True ($LASTEXITCODE -eq 0) 'WorkflowStudio Standalone 无窗口 Fake Action 自检失败。'
        Assert-True (($selfOutput -join "`n") -match
                'WORKFLOW_STUDIO_G3_SELF_TEST_OK invocations=4 disposedRuns=1') `
            'Standalone Fake Action 没有完成预期调用与 Run 释放。'

        $packageRoots = @(
            (Join-Path $resultRoot 'package-1'),
            (Join-Path $resultRoot 'package-2'))
        foreach ($packageRoot in $packageRoots) {
            Invoke-Checked dotnet @(
                'msbuild', $pluginProject, '-t:BuildManagedPluginPackage',
                "-p:Configuration=$Configuration",
                "-p:ManagedPluginPackageOutput=$packageRoot")
        }
        $zip1 = (Get-ChildItem -LiteralPath $packageRoots[0] -Filter '*.zip' -File).FullName
        $zip2 = (Get-ChildItem -LiteralPath $packageRoots[1] -Filter '*.zip' -File).FullName
        Assert-True (-not [string]::IsNullOrWhiteSpace($zip1) -and
            -not [string]::IsNullOrWhiteSpace($zip2)) 'G7 两次包构建没有各生成一个 ZIP。'
        $hash1 = (Get-FileHash -LiteralPath $zip1 -Algorithm SHA256).Hash
        $hash2 = (Get-FileHash -LiteralPath $zip2 -Algorithm SHA256).Hash
        Assert-True ($hash1 -ceq $hash2) 'WorkflowStudio G7 两次 ZIP 的 SHA-256 不一致。'
        $entries = Read-ZipEntries $zip1
        foreach ($required in @(
                'Controls/WorkflowStudio/plugin.manifest.json',
                'Controls/WorkflowStudio/WorkflowStudio.Plugin.dll',
                'Controls/WorkflowStudio/WorkflowStudio.Plugin.deps.json')) {
            Assert-True ($entries -ccontains $required) "WorkflowStudio G7 ZIP 缺少 $required。"
        }
        Assert-True (-not ($entries -match
                'Standalone|Tests|MyAvaloniaManagement\.PluginSdk.*\.dll')) `
            'G7 ZIP 混入 Standalone、Tests 或 Host 共享 SDK。'
        $manifest = Read-ZipText $zip1 'Controls/WorkflowStudio/plugin.manifest.json' |
            ConvertFrom-Json
        Assert-True (
            [int]$manifest.schemaVersion -eq 2 -and
            $manifest.pluginId -ceq 'myavalonia.plugin.workflow-studio' -and
            $manifest.pluginVersion -ceq '1.2.0' -and
            $manifest.sdk.minInclusive -ceq '3.3.0' -and
            $manifest.sdk.maxExclusive -ceq '4.0.0') `
            'G7 manifest schema、身份、版本或 SDK 区间不正确。'

        $extractRoot = Join-Path $resultRoot 'host-input'
        Expand-Archive -LiteralPath $zip1 -DestinationPath $extractRoot
        $evidenceText = Get-ChildItem -LiteralPath $resultRoot -File -Recurse |
            ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
        foreach ($canary in @(
                'G3-SELF-TEST-SECRET-MUST-NOT-LEAK',
                'TOP-SECRET-CANARY',
                'DOCUMENT-SECRET-CANARY',
                'UI-SECRET-MUST-NOT-APPEAR')) {
            Assert-True (-not (($evidenceText -join "`n").Contains(
                        $canary,
                        [StringComparison]::Ordinal))) `
                'Secret canary 进入 G7 测试、日志、包或门禁证据。'
        }
        Test-MarkdownLinks

        $summary = [ordered]@{
            schemaVersion = 1
            stage = 'WorkbenchCommandG7'
            configuration = $Configuration
            tests = $tests
            lineCoverage = $lineCoverage
            branchCoverage = $branchCoverage
            mainDocumentLineCoverage = $mainDocumentCoverage
            standaloneInvocations = 4
            deterministicBuilds = 2
            archiveSha256 = $hash1
            packageFiles = $entries.Count
            pluginZip = $zip1
            hostInputRoot = (Join-Path $extractRoot 'Controls')
            manifest = $manifest
            nugetCacheReused = [bool]$ReuseNuGetCache
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
            ($summary | ConvertTo-Json -Depth 10),
            [Text.UTF8Encoding]::new($false))
        Write-Host (
            "WorkflowStudio G7 门禁通过：$($tests.passed) 项，覆盖率 " +
            "$lineCoverage% / $branchCoverage%，ZIP $hash1。")
    }
    finally {
        Pop-Location
    }
}
finally {
    [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $previousPackages, 'Process')
    & dotnet build-server shutdown | Out-Null
}
