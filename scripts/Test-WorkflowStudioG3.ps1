[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$CandidateHostRoot = $env:MYAVALONIA_CANDIDATE_HOST_ROOT,
    [string]$CandidateFeed
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resultRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\test-results\WorkflowStudioG31'))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\test-results'))
$solution = Join-Path $repositoryRoot 'WorkflowStudio.slnx'
$pluginProject = Join-Path $repositoryRoot 'src\WorkflowStudio.Plugin\WorkflowStudio.Plugin.csproj'
$standaloneProject = Join-Path $repositoryRoot 'src\WorkflowStudio.Standalone\WorkflowStudio.Standalone.csproj'
$testProject = Join-Path $repositoryRoot 'tests\WorkflowStudio.Tests\WorkflowStudio.Tests.csproj'

if (-not $resultRoot.StartsWith(
        $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "G3 门禁结果目录越界：$resultRoot。"
}
if ([string]::IsNullOrWhiteSpace($CandidateHostRoot)) {
    throw '必须通过 -CandidateHostRoot 或 MYAVALONIA_CANDIDATE_HOST_ROOT 提供候选 Host 输出目录。'
}
$CandidateHostRoot = [IO.Path]::GetFullPath($CandidateHostRoot)
if (-not (Test-Path -LiteralPath (Join-Path $CandidateHostRoot 'MyAvaloniaManagement.exe') -PathType Leaf)) {
    throw "候选 Host 输出缺少 MyAvaloniaManagement.exe：$CandidateHostRoot。"
}

function Invoke-Checked {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') 失败，退出码：$LASTEXITCODE。"
    }
}

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-ZipText {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$EntryName)
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) { throw "ZIP 缺少条目：$EntryName。" }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Test-MarkdownLinks {
    $markdownFiles = Get-ChildItem -LiteralPath $repositoryRoot -Filter '*.md' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts|TestResults)[\\/]' }
    foreach ($file in $markdownFiles) {
        $text = Get-Content -Raw -LiteralPath $file.FullName
        foreach ($match in [regex]::Matches($text, '\[[^\]]+\]\((?!https?://|#)(?<path>[^)#]+)(?:#[^)]+)?\)')) {
            $relative = [Uri]::UnescapeDataString($match.Groups['path'].Value)
            $target = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $relative))
            if (-not (Test-Path -LiteralPath $target)) {
                throw "文档链接失效：$($file.FullName) -> $relative。"
            }
        }
    }
}

function Test-CandidateHost {
    param([Parameter(Mandatory)][string]$PluginZip)
    $hostCopy = Join-Path $resultRoot 'candidate-host'
    $dataRoot = Join-Path $resultRoot 'candidate-host-data'
    New-Item -ItemType Directory -Path $hostCopy, $dataRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $CandidateHostRoot '*') -Destination $hostCopy -Recurse -Force

    # 只清理刚创建且已校验位于结果目录内的隔离副本，避免候选输出中的其他插件干扰 G3 结论。
    $controls = Join-Path $hostCopy 'Controls'
    if (Test-Path -LiteralPath $controls) {
        Remove-Item -LiteralPath $controls -Recurse -Force
    }
    Expand-Archive -LiteralPath $PluginZip -DestinationPath $hostCopy -Force

    $previousSmoke = $env:MYAVALONIA_SMOKE_TEST
    $previousData = $env:MYAVALONIA_DATA_DIRECTORY
    try {
        $env:MYAVALONIA_SMOKE_TEST = '1'
        $env:MYAVALONIA_DATA_DIRECTORY = $dataRoot
        $process = Start-Process `
            -FilePath (Join-Path $hostCopy 'MyAvaloniaManagement.exe') `
            -WorkingDirectory $hostCopy `
            -WindowStyle Hidden `
            -PassThru
        if (-not $process.WaitForExit(30000)) {
            Stop-Process -Id $process.Id -Force
            throw '候选 Host 在 30 秒内未完成隔离启动与自动关闭。'
        }
        Assert-True ($process.ExitCode -eq 0) "候选 Host 启动失败，退出码：$($process.ExitCode)。"
    }
    finally {
        $env:MYAVALONIA_SMOKE_TEST = $previousSmoke
        $env:MYAVALONIA_DATA_DIRECTORY = $previousData
    }

    $diagnostics = Get-ChildItem -LiteralPath $dataRoot -Filter '*.jsonl' -File -Recurse -ErrorAction SilentlyContinue
    foreach ($file in $diagnostics) {
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $record = $line | ConvertFrom-Json
            $code = [string]$record.code
            $disposition = [string]$record.disposition
            if ($disposition -match 'AbortStartup' -or
                $code -match '(?i)plugin|extension|workflow') {
                throw "候选 Host 记录了 G3 相关诊断：$code / $disposition。"
            }
        }
    }
    return [ordered]@{
        root = $CandidateHostRoot
        exitCode = 0
        diagnostics = @($diagnostics).Count
    }
}

if (Test-Path -LiteralPath $resultRoot) {
    Remove-Item -LiteralPath $resultRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resultRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

Push-Location $repositoryRoot
try {
    # G3 是本地开发门禁。Release 只是编译配置；本脚本不调用 AIFLOW、Windows CI、
    # Release Acceptance、发布门禁、标签、签名、上传或外部发布命令。
    Assert-True ((Split-Path -Leaf $repositoryRoot) -ceq 'myavalonia-workflow-studio') `
        '解决方案必须直接位于 myavalonia-workflow-studio 子目录中。'
    foreach ($required in @(
            'WorkflowStudio.slnx',
            'src\WorkflowStudio.Plugin\WorkflowStudio.Plugin.csproj',
            'src\WorkflowStudio.Standalone\WorkflowStudio.Standalone.csproj',
            'tests\WorkflowStudio.Tests\WorkflowStudio.Tests.csproj')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $repositoryRoot $required) -PathType Leaf) `
            "模板结构缺失：$required。"
    }
    $pluginProjectText = Get-Content -Raw -LiteralPath $pluginProject
    Assert-True ($pluginProjectText -notmatch '<ProjectReference') '正式插件不得引用 Host 或其他源码项目。'
    Assert-True ($pluginProjectText -match '<ManagedPluginId>myavalonia\.plugin\.workflow-studio</ManagedPluginId>') `
        'manifest 稳定 PluginId 不正确。'
    $boundaryRoots = @(
        (Join-Path $repositoryRoot 'src'),
        (Join-Path $repositoryRoot 'tests'),
        (Join-Path $repositoryRoot 'docs'))
    $allTrackedText = Get-ChildItem -LiteralPath $boundaryRoots -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts|TestResults)[\\/]' } |
        ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
    Assert-True (-not (($allTrackedText -join "`n") -match 'avalonia_dock_simple_test|ProjectReference[^\r\n]*MyAvaloniaManagement')) `
        '新仓库出现 Host 源码路径或跨仓库 ProjectReference。'

    $nugetConfig = Join-Path $resultRoot 'NuGet.G3.1.config'
    $candidateSource = ''
    if (-not [string]::IsNullOrWhiteSpace($CandidateFeed)) {
        $candidateFeedPath = [IO.Path]::GetFullPath($CandidateFeed)
        Assert-True (Test-Path -LiteralPath $candidateFeedPath -PathType Container) `
            "候选 NuGet feed 不存在：$candidateFeedPath。"
        $escapedFeed = [Security.SecurityElement]::Escape($candidateFeedPath)
        $candidateSource = "    <add key=`"G31Candidate`" value=`"$escapedFeed`" />`r`n"
    }
    $configText = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n" +
        "<configuration>`r`n  <packageSources>`r`n    <clear />`r`n" +
        $candidateSource +
        "    <add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" protocolVersion=`"3`" />`r`n" +
        "  </packageSources>`r`n</configuration>`r`n"
    [IO.File]::WriteAllText($nugetConfig, $configText, [Text.UTF8Encoding]::new($false))
    $restoreArguments = @('restore', $solution, '--locked-mode', '--configfile', $nugetConfig)
    Invoke-Checked dotnet $restoreArguments
    Invoke-Checked dotnet @('build', $solution, '-c', $Configuration, '--no-restore', '-warnaserror')
    Invoke-Checked dotnet @('format', $solution, '--verify-no-changes', '--no-restore', '--verbosity', 'minimal')

    $testRoot = Join-Path $resultRoot 'tests'
    Invoke-Checked dotnet @(
        'test', $testProject,
        '-c', $Configuration,
        '--no-build', '--no-restore',
        '--collect:XPlat Code Coverage',
        '--results-directory', $testRoot,
        '--logger', 'trx;LogFileName=WorkflowStudioG3.trx')
    $trxPath = Join-Path $testRoot 'WorkflowStudioG3.trx'
    [xml]$trx = Get-Content -Raw -LiteralPath $trxPath
    $counters = $trx.TestRun.ResultSummary.Counters
    Assert-True ([int]$counters.failed -eq 0) 'G3 单元测试存在失败。'
    Assert-True ([int]$counters.notExecuted -eq 0) 'G3 单元测试存在跳过或未执行项。'
    Assert-True ([int]$counters.passed -gt 0) 'G3 门禁没有实际执行测试。'

    $coveragePath = (Get-ChildItem -LiteralPath $testRoot -Filter 'coverage.cobertura.xml' -File -Recurse |
        Select-Object -First 1).FullName
    Assert-True (-not [string]::IsNullOrWhiteSpace($coveragePath)) '没有生成 Cobertura 覆盖率。'
    [xml]$coverage = Get-Content -Raw -LiteralPath $coveragePath
    $lineCoverage = [Math]::Round([double]$coverage.coverage.'line-rate' * 100, 2)
    $branchCoverage = [Math]::Round([double]$coverage.coverage.'branch-rate' * 100, 2)
    Assert-True ($lineCoverage -ge 85) "行覆盖率 $lineCoverage% 低于 85%。"
    Assert-True ($branchCoverage -ge 75) "分支覆盖率 $branchCoverage% 低于 75%。"

    $criticalFiles = @(
        'Workflows\WorkflowActionCatalog.cs',
        'Workflows\WorkflowDefinitionCodec.cs',
        'Workflows\WorkflowDefinitionValidator.cs',
        'Workflows\WorkflowReferenceResolver.cs',
        'Workflows\WorkflowRunner.cs')
    $classes = @($coverage.coverage.packages.package.classes.class)
    foreach ($criticalFile in $criticalFiles) {
        $lines = @($classes | Where-Object { $_.filename -ceq $criticalFile } |
            ForEach-Object { $_.lines.line } |
            Group-Object number |
            ForEach-Object {
                [pscustomobject]@{
                    Hits = [int](($_.Group | Measure-Object -Property hits -Maximum).Maximum)
                }
            })
        Assert-True ($lines.Count -gt 0) "覆盖率报告缺少协议关键文件：$criticalFile。"
        $covered = @($lines | Where-Object { $_.Hits -gt 0 }).Count
        $criticalCoverage = [Math]::Round(100 * $covered / $lines.Count, 2)
        Assert-True ($criticalCoverage -ge 90) `
            "协议关键文件 $criticalFile 行覆盖率 $criticalCoverage% 低于 90%。"
    }

    $selfTestLog = Join-Path $resultRoot 'standalone-self-test.log'
    $selfOutput = @(& dotnet run --project $standaloneProject -c $Configuration --no-build --no-restore -- --g3-self-test 2>&1)
    $selfOutput | Set-Content -LiteralPath $selfTestLog -Encoding utf8NoBOM
    Assert-True ($LASTEXITCODE -eq 0) 'Standalone G3 无窗口自检失败。'
    Assert-True (($selfOutput -join "`n") -match 'WORKFLOW_STUDIO_G3_SELF_TEST_OK invocations=4 disposedRuns=1') `
        'Standalone 自检没有完成预期的 4 次调用和 Run 释放。'

    $packageRoots = @(
        (Join-Path $resultRoot 'package-1'),
        (Join-Path $resultRoot 'package-2'))
    foreach ($packageRoot in $packageRoots) {
        Invoke-Checked dotnet @(
            'msbuild', $pluginProject,
            '-t:BuildManagedPluginPackage',
            "-p:Configuration=$Configuration",
            "-p:ManagedPluginPackageOutput=$packageRoot")
    }
    $zips1 = @(Get-ChildItem -LiteralPath $packageRoots[0] -Filter '*.zip' -File)
    $zips2 = @(Get-ChildItem -LiteralPath $packageRoots[1] -Filter '*.zip' -File)
    Assert-True ($zips1.Count -eq 1 -and $zips2.Count -eq 1) '每次包构建必须恰好生成一个 ZIP。'
    $zip1 = $zips1[0].FullName
    $zip2 = $zips2[0].FullName
    $hash1 = (Get-FileHash -LiteralPath $zip1 -Algorithm SHA256).Hash
    $hash2 = (Get-FileHash -LiteralPath $zip2 -Algorithm SHA256).Hash
    Assert-True ($hash1 -ceq $hash2) '两次隔离插件 ZIP 的 SHA-256 不一致。'

    $archive = [IO.Compression.ZipFile]::OpenRead($zip1)
    try { $entries = @($archive.Entries | ForEach-Object FullName) }
    finally { $archive.Dispose() }
    foreach ($requiredEntry in @(
            'Controls/WorkflowStudio/plugin.manifest.json',
            'Controls/WorkflowStudio/WorkflowStudio.Plugin.dll',
            'Controls/WorkflowStudio/WorkflowStudio.Plugin.deps.json')) {
        Assert-True ($entries -ccontains $requiredEntry) "插件 ZIP 缺少 $requiredEntry。"
    }
    Assert-True (-not ($entries -match 'Standalone|Tests|MyAvaloniaManagement\.PluginSdk.*\.dll')) `
        '正式 ZIP 混入 Standalone、Tests 或 Host 共享 SDK。'
    $manifest = Get-ZipText $zip1 'Controls/WorkflowStudio/plugin.manifest.json' | ConvertFrom-Json
    Assert-True (
        [int]$manifest.schemaVersion -eq 2 -and
        $manifest.pluginId -ceq 'myavalonia.plugin.workflow-studio' -and
        $manifest.pluginVersion -ceq '1.1.0' -and
        $manifest.entryPoint.assembly -ceq 'WorkflowStudio.Plugin.dll' -and
        $manifest.entryPoint.type -ceq 'WorkflowStudio.Plugin.WorkflowStudioModule' -and
        $manifest.sdk.minInclusive -ceq '3.2.0' -and
        $manifest.sdk.maxExclusive -ceq '4.0.0') `
        '正式 ZIP 的 manifest 身份、入口、版本或 SDK 区间不正确。'

    $secretCanaries = @(
        'G3-SELF-TEST-SECRET-MUST-NOT-LEAK',
        'TOP-SECRET-CANARY',
        'DOCUMENT-SECRET-CANARY',
        'UI-SECRET-MUST-NOT-APPEAR')
    $evidenceText = Get-ChildItem -LiteralPath $resultRoot -File -Recurse |
        ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
    foreach ($canary in $secretCanaries) {
        Assert-True (-not (($evidenceText -join "`n").Contains($canary, [StringComparison]::Ordinal))) `
            'Secret canary 进入 G3 测试、日志、包或门禁证据。'
    }

    $hostEvidence = Test-CandidateHost -PluginZip $zip1
    Test-MarkdownLinks

    $summary = [ordered]@{
        schemaVersion = 1
        stage = 'G3.1'
        configuration = $Configuration
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        skipped = [int]$counters.notExecuted
        lineCoverage = $lineCoverage
        branchCoverage = $branchCoverage
        standaloneInvocations = 4
        deterministicBuilds = 2
        archiveSha256 = $hash1
        packageFiles = $entries.Count
        manifest = $manifest
        candidateHost = $hostEvidence
        aiflow = $false
        windowsCi = $false
        releaseAcceptance = $false
        releaseGate = $false
        publishable = $false
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText(
        (Join-Path $resultRoot 'summary.json'),
        ($summary | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    Write-Host "Workflow Studio G3.1 专项门禁通过：$($summary.passed) 项，覆盖率 $lineCoverage% / $branchCoverage%。"
}
finally {
    Pop-Location
    & dotnet build-server shutdown | Out-Null
}
