<#
.SYNOPSIS
  Publish System Assistant as a single-file portable build.

.DESCRIPTION
  Default: framework-dependent single file (small, needs .NET 9 Desktop Runtime).
  -IncludeRuntime: self-contained single file (larger, no runtime required).
  A zip archive is produced alongside the output folder.

.EXAMPLE
  ./publish.ps1
  ./publish.ps1 -IncludeRuntime
  ./publish.ps1 -IncludeRuntime -Runtime win-arm64
#>
param(
    [switch]$IncludeRuntime,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Output = "",
    [string]$DotNet = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/SystemAssistant.App/SystemAssistant.App.csproj"
if (-not (Test-Path $project)) { throw "Project not found: $project" }

if ([string]::IsNullOrWhiteSpace($DotNet)) { $DotNet = $env:DOTNET_EXE }
if ([string]::IsNullOrWhiteSpace($DotNet)) { $DotNet = "dotnet" }

$selfContained = if ($IncludeRuntime) { "true" } else { "false" }
# Compression inside the bundle is only supported for self-contained publish.
$compression = if ($IncludeRuntime) { "true" } else { "false" }
$mode = if ($IncludeRuntime) { "self-contained" } else { "framework-dependent" }
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root "artifacts\$Runtime-$mode"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " System Assistant - portable single-file publish" -ForegroundColor Cyan
Write-Host " RID        : $Runtime" -ForegroundColor Cyan
Write-Host " Mode       : $mode" -ForegroundColor Cyan
Write-Host " Output     : $Output" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# PublishSingleFile                    -> merge into one exe
# IncludeNativeLibrariesForSelfExtract -> bundle Avalonia / Skia / LHM native libs
# IncludeAllContentForSelfExtract      -> bundle axaml and other content resources
& $DotNet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$compression `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $Output

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Write a short readme next to the exe (UTF-8, Chinese content for end users)
$readme = Join-Path $Output "README-portable.txt"
$lines = @(
    "System Assistant 单文件绿色便携版",
    "生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "目标平台：$Runtime",
    "发布模式：$mode",
    "",
    "【运行】直接双击 SystemAssistant.exe，无需安装。",
    "【权限】默认以普通用户权限启动；涉及服务、系统修复、注册表、HKLM 启动项等操作",
    "        需要管理员权限，界面上会提示并可在用户确认后以管理员身份重启。",
    "【数据】配置、日志、备份保存在 %LOCALAPPDATA%\SystemAssistant，不写入系统目录。",
    "【隐私】程序默认不联网；更新检测仅在用户在设置页填写 URL 后才会发出一次 GET 请求，",
    "        且不会上传硬件信息、扫描结果或文件列表。"
)
if (-not $IncludeRuntime) {
    $lines += ""
    $lines += "【依赖】此版本需要目标机器预装 .NET 9 Desktop Runtime（x64）。"
    $lines += "        如需免安装运行，请重新执行 publish.ps1 -IncludeRuntime。"
}
Set-Content -Path $readme -Value $lines -Encoding UTF8

$zip = Join-Path (Split-Path -Parent $Output) "$(Split-Path -Leaf $Output).zip"
$filesToZip = @(Get-ChildItem -Path $Output -Filter "*.exe" | ForEach-Object { $_.FullName }) + @($readme)
if (Test-Path $zip) { Remove-Item $zip -Force }
# Windows PowerShell 5.1 Compress-Archive may silently omit a large (>40 MB)
# single-file bundle, producing an archive with only the readme. Use the .NET
# ZipFile API instead and verify that the exe really made it into the archive.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($entryPath in $filesToZip) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $entryPath,
            [System.IO.Path]::GetFileName($entryPath),
            'Optimal') | Out-Null
    }
} finally {
    $archive.Dispose()
}
$verify = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    if (-not ($verify.Entries | Where-Object { $_.Name -like '*.exe' })) {
        throw "Archive verification failed: $zip does not contain the executable"
    }
} finally {
    $verify.Dispose()
}

Write-Host ""
Write-Host "Publish succeeded." -ForegroundColor Green
Write-Host "  Folder : $Output" -ForegroundColor Green
Write-Host "  Archive: $zip" -ForegroundColor Green
