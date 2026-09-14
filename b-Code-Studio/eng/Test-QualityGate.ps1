#requires -Version 5.1

<#
    HistoryAurora 质量门禁。

    骨架阶段只守三件事：宿主版本单点声明、z-Publish 未被忽略规则覆盖、
    模块引用的私有性策略正确。随着能力迁入再逐条追加。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$componentRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $componentRoot
$violations = @()

# ---- 1. 宿主版本只在 AuroraVersion.props 声明一次 -------------------------
$versionProps = Join-Path $componentRoot 'AuroraVersion.props'
if (-not (Test-Path -LiteralPath $versionProps)) {
    throw "AuroraVersion.props missing: $versionProps"
}
$propsText = Get-Content -LiteralPath $versionProps -Raw -Encoding UTF8
$minMatch = [regex]::Match($propsText, '<MinimumHistoryVulcanVersion>(?<v>[^<]+)</MinimumHistoryVulcanVersion>')
if (-not $minMatch.Success) {
    $violations += 'AuroraVersion.props does not declare MinimumHistoryVulcanVersion'
} else {
    $minVersion = $minMatch.Groups['v'].Value
    # 除声明处外，源码与脚本不得出现该版本字面量。
    # 这里刻意用显式 foreach 而不是管道：Windows PowerShell 5.1 在 StrictMode Latest 下
    # 对跨行管道中的 $_ 解析不稳，门禁脚本自身不能成为第一个失败点。
    foreach ($relative in @('b-Code-Studio', 'b-Code-Verify')) {
        $root = Join-Path $repoRoot $relative
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $files = Get-ChildItem -LiteralPath $root -Recurse -File -Include *.ps1, *.cs, *.csproj, *.props
        foreach ($file in $files) {
            if ($file.FullName -match '\\(bin|obj)\\') { continue }
            if ($file.FullName -eq $versionProps) { continue }
            $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
            if ($content -and $content.Contains($minVersion)) {
                $violations += "hardcoded host version '$minVersion' in $($file.FullName)"
            }
        }
    }
}

# ---- 2. z-Publish 必须纳入 git ------------------------------------------
$gitignore = Join-Path $repoRoot '.gitignore'
if (Test-Path -LiteralPath $gitignore) {
    foreach ($line in (Get-Content -LiteralPath $gitignore)) {
        if ($line -match '^\s*/?z-Publish\s*/?\s*$') {
            $violations += '.gitignore excludes z-Publish; release truth must stay tracked'
            break
        }
    }
}

# ---- 3. 引用私有性策略 ---------------------------------------------------
$moduleProj = Join-Path $componentRoot 'Module\HistoryAurora.Module.csproj'
if (Test-Path -LiteralPath $moduleProj) {
    $text = Get-Content -LiteralPath $moduleProj -Raw -Encoding UTF8
    if ($text -match '<Private>true</Private>') {
        $violations += 'module project must reference host assemblies with Private=false'
    }
}
foreach ($name in @('Contracts', 'ModuleSmoke')) {
    $proj = Join-Path $repoRoot "b-Code-Verify\$name\$name.csproj"
    if (Test-Path -LiteralPath $proj) {
        $text = Get-Content -LiteralPath $proj -Raw -Encoding UTF8
        if ($text -match '<Private>false</Private>') {
            $violations += "$name must reference host assemblies with Private=true"
        }
    }
}

if ($violations.Count -ne 0) {
    Write-Host 'Quality gate failed:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Quality gate passed: host version single-sourced; z-Publish tracked; reference privacy correct.'
