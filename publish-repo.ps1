<#
.SYNOPSIS
    把 launcher/ 的源码发布到独立的 GitHub 仓库（预编译 exe 走 Release，不进仓库）。

.DESCRIPTION
    以临时工作目录作为 git 仓库来发布，因此在项目本体仓库里不会产生嵌套仓库，
    也不会碰本体任何文件：
      源文件（launcher/） ──拷贝──▶ 临时仓库目录 ──git push──▶ GitHub

    仓库里只放源码 + build.ps1 + README + wrapper 脚本 + .gitignore/.gitattributes；
    dist/（预编译 exe）被 .gitignore 排除，改用 publish-release.ps1 作为 Release 附件发布。

.EXAMPLE
    # 首次发布（仓库不存在时会自动创建，公开）
    pwsh launcher\publish-repo.ps1 -Message "feat: SonettoHere 启动器 1.0.0"

    # 以后更新
    pwsh launcher\publish-repo.ps1 -Message "fix: 修复 xxx"

    # 只提交到本地临时仓库、不推远端（先看看 diff）
    pwsh launcher\publish-repo.ps1 -SkipPush
#>
[CmdletBinding()]
param(
    [string]$Repo = '7starsseeker/SonettoLauncherHereToo',
    [string]$WorkDir = 'Q:\TEMP\SonettoLauncherHereToo',
    [string]$Message = "chore: 更新启动器源码 $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    [switch]$SkipPush
)

$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source 'SonettoHereLauncher.csproj'))) {
    throw "请在 launcher 目录下运行本脚本（当前：$source）"
}

Write-Host "==> 源目录：$source" -ForegroundColor Cyan
Write-Host "==> 临时仓库：$WorkDir" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ── 1. 拷贝源码（不含 build/、dist/）──────────────────────────
$files = @(
    'SonettoHereLauncher.csproj',
    'Directory.Build.props',
    'app.manifest',
    'build.ps1',
    'publish-repo.ps1',
    'publish-release.ps1',
    'README.md'
)
$files += (Get-ChildItem -Path $source -Filter '*.cs' -File | Select-Object -ExpandProperty Name)

foreach ($item in $files) {
    Copy-Item (Join-Path $source $item) (Join-Path $WorkDir $item) -Force
}

foreach ($dir in @('Native', 'assets', 'wrappers')) {
    $target = Join-Path $WorkDir $dir
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $source "$dir\*") $target -Recurse -Force
}

# ── 2. 仓库级的 .gitignore / .gitattributes（只作用于独立仓库）──
@'
# 构建中间产物与预编译产物（预编译 exe 走 Release 附件，不进仓库）
build/
bin/
obj/
dist/
*.user
'@ | Set-Content -Path (Join-Path $WorkDir '.gitignore') -Encoding utf8

@'
* text=auto
*.exe binary
*.dll binary
*.png binary
*.ico binary
'@ | Set-Content -Path (Join-Path $WorkDir '.gitattributes') -Encoding utf8

# 清理历史上误提交的 dist/（如果存在）
$staleDist = Join-Path $WorkDir 'dist'
if (Test-Path $staleDist) {
    Remove-Item $staleDist -Recurse -Force
    Write-Host "    - 已移除临时仓库中的 dist/（预编译产物改走 Release）" -ForegroundColor DarkGray
}

# ── 3. git commit ───────────────────────────────────────────
Push-Location $WorkDir
try {
    if (-not (Test-Path (Join-Path $WorkDir '.git'))) {
        git init -b main | Out-Null
        Write-Host "==> 已初始化 git 仓库" -ForegroundColor Cyan
    }

    git add -A
    $pending = git status --porcelain
    if ([string]::IsNullOrWhiteSpace($pending)) {
        Write-Host "==> 没有需要提交的改动" -ForegroundColor Yellow
    } else {
        git commit -m $Message | Out-Null
        Write-Host "==> 已提交：$Message" -ForegroundColor Cyan
    }

    if ($SkipPush) {
        Write-Host "==> 已跳过推送（-SkipPush）" -ForegroundColor Yellow
        return
    }

    # ── 4. 推送到 GitHub（仓库不存在则创建）──────────────────
    gh repo view $Repo --json name 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "==> 仓库 $Repo 不存在，创建公开仓库并推送 ..." -ForegroundColor Cyan
        gh repo create $Repo --public `
            --description 'SonettoHere 独立启动器：一键拉起前后端、内嵌界面、优雅退出，并整合初始化/更新' `
            --source $WorkDir --remote origin --push
    } else {
        $remotes = git remote
        if ($remotes -notcontains 'origin') {
            git remote add origin "https://github.com/$Repo.git"
        }
        Write-Host "==> 推送到 $Repo ..." -ForegroundColor Cyan
        git push -u origin main
    }

    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "完成：https://github.com/$Repo" -ForegroundColor Green
        Write-Host "预编译 exe 请用：pwsh publish-release.ps1 -Tag v<版本>" -ForegroundColor DarkGray
    } else {
        throw "推送失败（exit=$LASTEXITCODE）"
    }
}
finally {
    Pop-Location
}
