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
    # 默认落在系统临时目录；需要固定位置时用 -WorkDir 或环境变量 SONETTO_LAUNCHER_WORKDIR
    [string]$WorkDir = $(if ($env:SONETTO_LAUNCHER_WORKDIR) { $env:SONETTO_LAUNCHER_WORKDIR } else { Join-Path ([IO.Path]::GetTempPath()) 'SonettoLauncherHereToo' }),
    [string]$Message = "chore: 更新启动器源码 $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    [switch]$SkipPush
)

$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source 'SonettoLauncher.csproj'))) {
    throw "请在 launcher 目录下运行本脚本（当前：$source）"
}

Write-Host "==> 源目录：$source" -ForegroundColor Cyan
Write-Host "==> 临时仓库：$WorkDir" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ── 0a. 确保工作目录是个 git 仓库 ─────────────────────────────
# 工作目录是全新的、而远端仓库已存在时，必须先 clone 继承既有历史：
# 直接 git init 会得到一条与远端断链的单提交历史，push 会被拒（non-fast-forward）。
if (-not (Test-Path (Join-Path $WorkDir '.git'))) {
    Get-ChildItem -Path $WorkDir -Force |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    $hasRemote = $false
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        gh repo view $Repo --json name 2>$null | Out-Null
        $hasRemote = ($LASTEXITCODE -eq 0)
    }

    if ($hasRemote) {
        Write-Host "==> 从 $Repo 克隆既有历史到工作目录 ..." -ForegroundColor Cyan
        git clone --quiet "https://github.com/$Repo.git" $WorkDir
        if ($LASTEXITCODE -ne 0) {
            throw "克隆 $Repo 失败（exit=$LASTEXITCODE）"
        }
    } else {
        git init -b main $WorkDir | Out-Null
        Write-Host "==> 远端仓库尚不存在，已初始化空仓库" -ForegroundColor Cyan
    }
}

# ── 0b. 清空工作目录（保留 .git）─────────────────────────────
# 这样源目录里被重命名/删除的文件，在仓库里也会同步删除（否则旧文件会永久残留）
Get-ChildItem -Path $WorkDir -Force |
    Where-Object { $_.Name -ne '.git' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# ── 1. 拷贝源码（不含 build/、dist/）──────────────────────────
$files = @(
    'SonettoLauncher.csproj',
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

foreach ($dir in @('Native', 'assets', 'wrappers', 'tools')) {
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
        throw "工作目录不是 git 仓库：$WorkDir（步骤 0a 应已创建）"
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
