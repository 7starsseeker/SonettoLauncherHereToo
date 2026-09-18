<#
.SYNOPSIS
    把打包好的 exe 作为 GitHub Release 附件发布（**不**提交进仓库）。

.DESCRIPTION
    启动器自身版本从 SonettoHereLauncher.csproj 的 <Version> 读取，默认 tag 为 v<版本>。
    上传 dist/ 下的两个产物：
      · dist\self-contained\SonettoHereLauncher.exe      自包含，免运行时（约 63MB）
      · dist\framework-dependent\SonettoHereLauncher.exe 框架依赖，需 .NET 8 桌面运行时（约 1.3MB）
    Release 已存在时改为补传附件（--clobber 覆盖），方便重打包后刷新。

.EXAMPLE
    pwsh launcher\build.ps1
    pwsh launcher\publish-release.ps1 -Notes "首个版本：一键启动、内嵌界面、优雅退出"
#>
[CmdletBinding()]
param(
    [string]$Repo = '7starsseeker/SonettoLauncherHereToo',
    [string]$Tag,
    [string]$Title,
    [string]$Notes = '',
    [string]$NotesFile,
    [switch]$Draft,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot

# ── 版本与 tag ──────────────────────────────────────────────
[xml]$project = Get-Content (Join-Path $source 'SonettoHereLauncher.csproj')
$version = ($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw '未能从 csproj 读取 <Version>' }
if (-not $Tag) { $Tag = "v$version" }
if (-not $Title) { $Title = "SonettoHere Launcher $Tag" }

# ── 产物检查 ────────────────────────────────────────────────
$variants = [ordered]@{
    'self-contained'      = '自包含（免运行时，约 63MB）'
    'framework-dependent' = '框架依赖（需 .NET 8 桌面运行时，约 1.3MB）'
}

# 附件名必须唯一，否则同名会互相覆盖；统一带版本号与变体名
$staging = Join-Path $env:TEMP ("sonetto-release-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$assets = @()
$assetTable = @()
try {
    foreach ($variant in $variants.Keys) {
        $candidate = Join-Path $source "dist\$variant\SonettoHereLauncher.exe"
        if (-not (Test-Path $candidate)) {
            Write-Host "    [!] 缺少产物：$candidate（先跑 pwsh build.ps1）" -ForegroundColor Yellow
            continue
        }

        $assetName = "SonettoHereLauncher-$version-win-x64-$variant.exe"
        $staged = Join-Path $staging $assetName
        Copy-Item $candidate $staged -Force
        $assets += $staged
        $assetTable += [pscustomobject]@{
            Name = $assetName
            Size = [math]::Round((Get-Item $candidate).Length / 1MB, 1)
            Note = $variants[$variant]
        }
    }

    if ($assets.Count -eq 0) {
        throw 'dist/ 下没有任何产物，请先运行：pwsh build.ps1'
    }

    # ── Release 说明 ────────────────────────────────────────
    $rows = ($assetTable | ForEach-Object { "| ``$($_.Name)`` | 约 $($_.Size) MB | $($_.Note) |" }) -join "`n"

    $body = @"
## 下载

| 附件 | 大小 | 运行要求 |
|---|---|---|
$rows

## 使用

1. 下载上面任一 exe（文件名里的 `self-contained` = 免运行时，`framework-dependent` = 需 .NET 8 桌面运行时）；
2. 放到 SonettoHere 项目根目录（与 ``start.bat`` 并排）双击；放在别处也行，启动器会自动向上查找项目目录，找不到会弹目录选择；
3. 需要 **Microsoft Edge WebView2 Runtime**（Windows 11 与多数 Windows 10 已预装；缺失时自动降级为独立 Edge 窗口显示）。

## 说明

- 启动器自身版本 **$Tag**，与上游 SonettoHere 本体（``version.py``）的版本相互独立；
- 本仓库只包含源码，预编译产物仅作为本 Release 的附件提供；
- 本启动器是 SonettoHere 的第三方配套工具，不修改上游任何代码。
"@

    if ($NotesFile) {
        if (-not (Test-Path $NotesFile)) { throw "找不到说明文件：$NotesFile" }
        $body = (Get-Content $NotesFile -Raw) + "`n`n" + $body
    } elseif ($Notes) {
        $body = "## 本次更新`n`n$Notes`n`n" + $body
    }

    # ── 上传 ────────────────────────────────────────────────
    gh repo view $Repo --json name 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "仓库 $Repo 不存在，请先运行：pwsh publish-repo.ps1"
    }

    gh release view $Tag --repo $Repo 2>$null | Out-Null
    $releaseExists = ($LASTEXITCODE -eq 0)

    Write-Host "==> 目标仓库：$Repo" -ForegroundColor Cyan
    Write-Host "==> Tag：$Tag（$(if ($releaseExists) { '已存在，补传附件' } else { '新建 Release' })）" -ForegroundColor Cyan
    foreach ($asset in $assetTable) {
        Write-Host "    附件：$($asset.Name)  ($($asset.Size) MB)" -ForegroundColor Cyan
    }

    if ($releaseExists) {
        gh release upload $Tag --repo $Repo @assets --clobber
        if ($Notes -or $NotesFile) {
            gh release edit $Tag --repo $Repo --notes $body | Out-Null
            Write-Host "==> 已更新 Release 说明" -ForegroundColor DarkGray
        }
    } else {
        # 新建时：先建空 Release，再逐个上传附件（避免 gh 在上传失败时把整个 Release 回滚掉）
        $createArgs = @('release', 'create', $Tag, '--repo', $Repo, '--title', $Title, '--notes', $body)
        if ($Draft) { $createArgs += '--draft' }
        if ($Prerelease) { $createArgs += '--prerelease' }
        gh @createArgs

        if ($LASTEXITCODE -ne 0) {
            throw "创建 Release 失败（exit=$LASTEXITCODE）"
        }

        gh release upload $Tag --repo $Repo @assets --clobber
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "完成：https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
    } else {
        throw "发布失败（exit=$LASTEXITCODE）"
    }
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
