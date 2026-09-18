<#
.SYNOPSIS
    SonettoHere 启动器打包脚本。

.DESCRIPTION
    产出两种形式的单文件 exe（都在 launcher\dist\ 下，已被 .gitignore 忽略）：
      dist\self-contained\SonettoLauncher.exe        自包含，目标机器无需安装任何运行时（约 70 MB）
      dist\framework-dependent\SonettoLauncher.exe   依赖 .NET 8 桌面运行时（约 2 MB）

.PARAMETER Only
    All（默认）/ SelfContained / FrameworkDependent。

.EXAMPLE
    pwsh launcher\build.ps1
    pwsh launcher\build.ps1 -Only FrameworkDependent
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'SelfContained', 'FrameworkDependent')]
    [string]$Only = 'All'
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here 'SonettoLauncher.csproj'
$dist = Join-Path $here 'dist'

function Invoke-Publish {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][bool]$SelfContained
    )

    Write-Host ""
    Write-Host "==> 打包 $Name ..." -ForegroundColor Cyan

    $args = @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        "-p:SelfContained=$($SelfContained.ToString().ToLowerInvariant())",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishReadyToRun=false',
        '-o', $OutputDir,
        '--nologo',
        '-v', 'minimal'
    )

    # 单文件压缩只支持自包含发布（框架依赖版会报 NETSDK1176）
    if ($SelfContained) {
        $args += '-p:EnableCompressionInSingleFile=true'
    }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（exit=$LASTEXITCODE）"
    }

    $exe = Join-Path $OutputDir 'SonettoLauncher.exe'
    if (-not (Test-Path $exe)) {
        throw "没有找到产物：$exe"
    }

    # 清理多余文件，只留 exe
    Get-ChildItem -Path $OutputDir -File |
        Where-Object { $_.Name -ne 'SonettoLauncher.exe' } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "    产物：$exe ($size MB)" -ForegroundColor Green
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

if ($Only -in @('All', 'SelfContained')) {
    Invoke-Publish -Name '自包含单文件（免运行时）' -OutputDir (Join-Path $dist 'self-contained') -SelfContained $true
}

if ($Only -in @('All', 'FrameworkDependent')) {
    Invoke-Publish -Name '框架依赖（需 .NET 8 桌面运行时）' -OutputDir (Join-Path $dist 'framework-dependent') -SelfContained $false
}

Write-Host ""
Write-Host "完成。使用方式：" -ForegroundColor Cyan
Write-Host "  · 把 exe 放到项目根目录（与 start.bat 并排）双击即可；"
Write-Host "  · 或放到任意位置，启动器会自动向上查找项目目录，找不到时会提示手动选择。"
Write-Host "  · 构建产物在 launcher\dist\ 下，已被 .gitignore 忽略，不会进版本库。"
