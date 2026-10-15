# DSH 网页端启动器 —— 一键构建
#   1) 编译启动器 DSH-Web.exe(内嵌图标 + 应用清单)
#   2) 把启动器与 EULA 作为资源打进安装程序 DSH-Web-Setup-vX.Y.exe
#
# 依赖:Windows 自带的 .NET Framework 编译器 csc.exe(无需联网、无需 SDK)
# 说明:参数通过 csc 响应文件(@file)传递,避免 PowerShell 的逗号/引号解析问题,
#       因此工程路径包含空格也能正常构建。
#
# 用法:  pwsh -File build.ps1 [-Version 1.2]
param(
    [string]$Version = $null
)

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$src   = Join-Path $root 'src'
$res   = Join-Path $root 'resources'
$dist  = Join-Path $root 'dist'

# ---- 版本号:优先参数,否则读源码常量;并同步写入安装程序的版本常量
if (-not $Version) {
    $core = Get-Content (Join-Path $src 'Launcher\DSH-Core.cs') -Raw -Encoding UTF8
    if ($core -match 'internal const string Version\s*=\s*"([^"]+)"') { $Version = $Matches[1] } else { $Version = '0.0' }
}
$setupPath = Join-Path $src 'Setup\Setup.cs'
$setupText = Get-Content $setupPath -Raw -Encoding UTF8
$newText = $setupText -replace 'internal const string VersionText = "v[^"]*"', ('internal const string VersionText = "v' + $Version + '"')
if ($newText -ne $setupText) { Set-Content -Path $setupPath -Value $newText -Encoding UTF8 -NoNewline }

# ---- 定位编译器
$csc = $null
foreach ($c in @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))) {
    if (Test-Path $c) { $csc = $c; break }
}
if (-not $csc) { throw "找不到 csc.exe(.NET Framework 4.x)。请安装 .NET Framework,或改用 Visual Studio / MSBuild 打开解决方案构建。" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$ico      = Join-Path $res 'DSH-Web.ico'
$manifest = Join-Path $res 'DSH-Web.manifest'

function Invoke-Csc {
    param([string[]]$Arguments, [string]$Tag)
    $rsp = Join-Path $dist ("csc-$Tag.rsp")
    Set-Content -Path $rsp -Value $Arguments -Encoding UTF8
    & $csc ("@" + $rsp)
    if ($LASTEXITCODE -ne 0) { throw "$Tag 编译失败(exit $LASTEXITCODE),响应文件:$rsp" }
}

# ---- 1) 启动器
Write-Host "[1/2] 编译启动器 v$Version …"
$outLauncher = Join-Path $dist 'DSH-Web.exe'
$launcherArgs = @(
    '/nologo', '/target:winexe', '/codepage:65001',
    ('/win32icon:"' + $ico + '"'),
    ('/win32manifest:"' + $manifest + '"'),
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll',
    ('/out:"' + $outLauncher + '"')
)
foreach ($f in Get-ChildItem (Join-Path $src 'Launcher') -Filter *.cs) { $launcherArgs += ('"' + $f.FullName + '"') }
Invoke-Csc -Arguments $launcherArgs -Tag 'launcher'

# ---- 2) 安装程序(内嵌启动器 + 协议)
Write-Host "[2/2] 打包安装程序 …"
$outSetup = Join-Path $dist ("DSH-Web-Setup-v$Version.exe")
$setupArgs = @(
    '/nologo', '/target:winexe', '/codepage:65001',
    ('/win32icon:"' + $ico + '"'),
    ('/win32manifest:"' + $manifest + '"'),
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    ('/resource:"' + $outLauncher + '",payload.DSH-Web.exe'),
    ('/resource:"' + (Join-Path $root 'EULA.md') + '",EULA.md'),
    ('/out:"' + $outSetup + '"'),
    ('"' + (Join-Path $src 'Setup\Setup.cs') + '"')
)
Invoke-Csc -Arguments $setupArgs -Tag 'setup'

Write-Host ""
Write-Host "构建完成:"
foreach ($f in @($outLauncher, $outSetup)) {
    $i = Get-Item $f
    Write-Host ("  {0,-28} {1,12:N0} 字节" -f $i.Name, $i.Length)
}
Write-Host ""
Write-Host "  图形安装:双击安装包,按向导下一步即可"
Write-Host "  静默安装:DSH-Web-Setup-v$Version.exe /S /D=\"%LOCALAPPDATA%\Programs\DSH 网页端\" /desktopicon=1 /launch=1"
