#requires -Version 5.1

<#
    构建 HistoryAurora 发布候选到 `z-Publish/HistoryAurora-vX.Y.Z/`。

    与体系其他模块一致：候选是生成物，不手工编辑；SHA256SUMS 覆盖包内全部内容，
    history/ 与 SHA256SUMS 自身除外（宿主 RuntimeModuleDiscoverySource 按此校验）。
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 省略时直接写进 z-Publish 的版本化目录并归档旧版。
    # 传入 OutputRoot 时该目录就是包根（宿主 staging 或临时校验），不二次归档。
    [string]$OutputRoot,

    # 宿主可传入 HistoryVulcanPackageRoot，指向本次要对齐的宿主快照；省略时由 Directory.Build.props 决定。
    [string]$HistoryVulcanPackageRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$componentRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $componentRoot

$propsText = Get-Content -LiteralPath (Join-Path $componentRoot 'AuroraVersion.props') -Raw -Encoding UTF8
$versionMatch = [regex]::Match($propsText, '<HistoryAuroraVersion>(?<v>[^<]+)</HistoryAuroraVersion>')
if (-not $versionMatch.Success) {
    throw 'AuroraVersion.props does not declare HistoryAuroraVersion'
}
$version = $versionMatch.Groups['v'].Value

$solution = Join-Path $repoRoot 'HistoryAurora.sln'
$buildProperties = @('-p:NuGetAudit=false')
if (-not [string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    $buildProperties += "-p:HistoryVulcanPackageRoot=$HistoryVulcanPackageRoot"
}
& dotnet build $solution -c $Configuration --nologo @buildProperties
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$output = Join-Path $componentRoot "Module\bin\$Configuration\net8.0-windows"
if (-not (Test-Path -LiteralPath $output)) { throw "build output missing: $output" }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("HistoryAurora-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    # DEC-008：产物名取回 HistoryAurora（独立 exe 退役，不再有同名程序集）。
    # AvalonDock 必须随包走——模块的装载上下文只在包目录内解析依赖，
    # 少带一个就在建窗时 FileNotFoundException，而那发生在运行期。
    foreach ($name in @(
            'HistoryAurora.dll',
            'HistoryAurora.xml',
            'AvalonDock.dll',
            'AvalonDock.Themes.VS2013.dll',
            'module.manifest.json')) {
        $source = Join-Path $output $name
        if (-not (Test-Path -LiteralPath $source)) { throw "expected artifact missing: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $name) -Force
    }

    # b-Office/package 随包进 docs/。这不是附赠品：Diana 的 `diana.docs.*` 通道按候选包里的
    # docs/ 建索引，别的项目就是从那里读 Aurora 的组件清单与模块 API。少打这一步，
    # `diana.docs.catalog` 里 aurora 那条的 Documents 会变成空数组——不报错，只是各模块
    # 写页面时再也查不到协议参考。1.14.0 的包里有 docs/，1.15.0 一度漏掉，据此补回。
    $packageDocuments = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'b-Office\package') -Filter '*.md' -File)
    if ($packageDocuments.Count -eq 0) {
        throw 'b-Office/package must contain at least one Markdown document'
    }
    $docsStage = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Path $docsStage -Force | Out-Null
    foreach ($document in $packageDocuments) {
        Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $docsStage $document.Name) -Force
    }

    # SHA256SUMS：覆盖包内全部有效载荷，排除自身与 history/
    $lines = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if ($relative -eq 'SHA256SUMS' -or $relative -like 'history/*') { return }
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        "$hash  $relative"
    } | Where-Object { $_ }

    # 必须无 BOM：宿主 RuntimeModuleDiscoverySource 按 ^[0-9A-Fa-f]{64}  <路径>$ 逐行匹配，
    # BOM 会让首行匹配失败，整个包被判 invalid-checksum 而静默跳过。
    # Set-Content -Encoding utf8 在 Windows PowerShell 5.1 下写的是带 BOM 的 UTF-8，不能用。
    [System.IO.File]::WriteAllLines(
        (Join-Path $stage 'SHA256SUMS'),
        [string[]]$lines,
        (New-Object System.Text.UTF8Encoding $false))

    if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
        # 显式 OutputRoot 只交付包内容，不碰 z-Publish 根（避免与版本化归档抢同一份）。
        $staged = [IO.Path]::GetFullPath($OutputRoot)
        New-Item -ItemType Directory -Path $staged -Force | Out-Null
        Get-ChildItem -LiteralPath $staged -Force | Remove-Item -Recurse -Force
        Copy-Item -Path (Join-Path $stage '*') -Destination $staged -Recurse -Force
        Write-Host "HistoryAurora $version staged: $staged"
        return
    }

    $publishRoot = Join-Path $repoRoot 'z-Publish'
    $historyRoot = Join-Path $publishRoot 'history'
    New-Item -ItemType Directory -Path $historyRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $publishRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'HistoryAurora-v*' -and $_.Name -ne "HistoryAurora-v$version" } |
        ForEach-Object {
            $archive = Join-Path $historyRoot $_.Name
            if (Test-Path -LiteralPath $archive) {
                Remove-Item -LiteralPath $archive -Recurse -Force
            }
            Move-Item -LiteralPath $_.FullName -Destination $archive
        }

    $candidate = Join-Path $publishRoot "HistoryAurora-v$version"
    if (Test-Path -LiteralPath $candidate) {
        Remove-Item -LiteralPath $candidate -Recurse -Force
    }
    New-Item -ItemType Directory -Path $candidate -Force | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $candidate -Recurse -Force

    Write-Host "HistoryAurora $version packaged: $candidate"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
