#requires -Version 5.1

<#
    构建 HistoryAurora 发布候选到 z-Publish\ 根部。

    与体系其他模块一致：候选是生成物，不手工编辑；SHA256SUMS 覆盖包内全部内容，
    history/ 与 SHA256SUMS 自身除外（宿主 RuntimeModuleDiscoverySource 按此校验）。
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
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
& dotnet build $solution -c $Configuration --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$output = Join-Path $componentRoot "Module\bin\$Configuration\net8.0-windows"
if (-not (Test-Path -LiteralPath $output)) { throw "build output missing: $output" }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("HistoryAurora-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    foreach ($name in @('HistoryAurora.dll', 'HistoryAurora.xml', 'module.manifest.json')) {
        $source = Join-Path $output $name
        if (-not (Test-Path -LiteralPath $source)) { throw "expected artifact missing: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $name) -Force
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

    $candidate = Join-Path $repoRoot "z-Publish\HistoryAurora-v$version"
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
