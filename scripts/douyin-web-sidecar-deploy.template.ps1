<#
.SYNOPSIS
    抖音 **浏览器底座** sidecar（douyin-web-sidecar）管理脚本模板。

.DESCRIPTION
    与 `douyin-sidecar-deploy` 的分工：那个管旧底座（douyin-im），这个管新底座（Playwright + 网页版）。
    两个 sidecar 都往同一个 `/ws/channel` 反连，**由服务端 `douyin.driver` 决定收哪一个**。

    ⚠️ 本文件是**模板**（占位符 `<server-ip-redacted>` 等要换成真值，或用参数覆盖）。
    复制成 `douyin-web-sidecar-deploy.local.ps1` 再改，那个文件名已被 .gitignore 排除。

.PARAMETER Action
    status（默认）| install | build | start | stop | restart | logs | login-check

.PARAMETER Mode
    remote（默认，管服务器）| local（本机开发调试）

.EXAMPLE
    .\douyin-web-sidecar-deploy.local.ps1                       # 看服务器上的状态
    .\douyin-web-sidecar-deploy.local.ps1 -Action install        # 服务器上装依赖 + Chromium
    .\douyin-web-sidecar-deploy.local.ps1 -Action start -Token <token>
    .\douyin-web-sidecar-deploy.local.ps1 -Action logs

.NOTES
    编码：本文件必须存为 **UTF-8 带 BOM**（Windows PowerShell 5.1 才认中文）。
    计划任务身份：SYSTEM / onstart（**没有交互桌面 → 只能 headless**，见实施方案 §C.6）。
#>
[CmdletBinding()]
param(
    [ValidateSet('status', 'install', 'build', 'start', 'stop', 'restart', 'logs', 'login-check')]
    [string]$Action = 'status',

    [ValidateSet('remote', 'local')]
    [string]$Mode = 'remote',

    [string]$Server = '<server-ip-redacted>',
    [string]$SshUser = 'Administrator',
    [string]$TargetPath = 'D:\App\ZipperAIQQ',
    [string]$TaskName = 'ZipperAIQQ-DouyinWeb',
    [string]$Token,
    [string]$LogLevel = 'info'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$sidecarDir = Join-Path $repoRoot 'douyin-web-sidecar'
$remote = "$SshUser@$Server"
$remoteSidecar = "$TargetPath\douyin-web-sidecar"

function Step([string]$t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok([string]$t) { Write-Host "  OK  $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "  !!  $t" -ForegroundColor Yellow }

function Assert-Remote {
    if ($Server -eq '<server-ip-redacted>') {
        throw '还没填服务器地址：复制成 douyin-web-sidecar-deploy.local.ps1 并改 -Server（或用参数传）'
    }
}

function Assert-LocalNode {
    $v = try { & node -v } catch { $null }
    if (-not $v) { throw '本机没有 node；先装 Node.js ≥ 22.5' }
    $major = [int](($v.TrimStart('v') -split '\.')[0])
    if ($major -lt 22) { throw "Node 版本过低：$v（需要 ≥ 22.5）" }
    Ok "node $v"
}

function Invoke-Remote([string]$script) {
    & ssh -o BatchMode=yes $remote "powershell -NoProfile -Command `"$script`""
}

# ─────────────────────────── remote ───────────────────────────

function Remote-Status {
    Assert-Remote
    Step "服务器状态（$remote）"
    $out = Invoke-Remote @"
Write-Host ('task=' + ((schtasks /query /tn $TaskName /fo list 2>&1 | Select-String 'Status|状态') -join ' '));
Write-Host ('procs=' + ((Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { `$_.CommandLine -like '*douyin-web-sidecar*' } | Measure-Object).Count));
if (Test-Path '$remoteSidecar\sidecar.log') { Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 -Tail 6 }
"@
    Write-Host $out
    if ($out -match 'procs=0') { Warn 'sidecar 进程数为 0 —— 用 -Action start 起来，或看日志' }
    if ($out -match 'procs=[1-9]') { Ok 'sidecar 在运行' }
}

function Remote-Install {
    Assert-Remote
    Step '安装依赖（服务器）'
    Warn '这一步会下载 Playwright 自带的 Chromium（约 400 MB），并落盘到服务器 —— 需要你明确同意'
    $out = Invoke-Remote @"
Set-Location '$remoteSidecar';
npm install --no-audit --no-fund 2>&1 | Select-Object -Last 5;
npx playwright install chromium 2>&1 | Select-Object -Last 5;
Write-Host ('nodeModules=' + (Test-Path '$remoteSidecar\node_modules'))
"@
    Write-Host $out
    if ($out -notmatch 'nodeModules=True') { throw '依赖没装上：看上面的输出' }
    Ok '依赖已装'
}

function Remote-Build {
    Assert-Remote
    Step '编译（服务器）'
    $out = Invoke-Remote @"
Set-Location '$remoteSidecar';
npm run build 2>&1 | Select-Object -Last 8;
Write-Host ('dist=' + (Test-Path '$remoteSidecar\dist\main.js'))
"@
    Write-Host $out
    if ($out -notmatch 'dist=True') { throw '编译失败：看上面的输出' }
    Ok '编译完成'
}

function Remote-WriteLauncher {
    # 启动器里只有环境变量与命令行；token 明文只存在该机器的本地文件里（不进仓库 / 不进日志）
    $tokenLine = if ($Token) { "set ZIPPER_DOUYIN_WEB_TOKEN=$Token" } else { 'rem 未配置 token（loopback 免验）' }
    $content = @"
@echo off
cd /d $remoteSidecar
$tokenLine
set ZIPPER_DOUYIN_WEB_LOG_LEVEL=$LogLevel
node dist\main.js >> sidecar.log 2>&1
"@

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($content)
    $b64 = [Convert]::ToBase64String($bytes)
    Invoke-Remote @"
[IO.File]::WriteAllBytes('$remoteSidecar\run-web-sidecar.cmd', [Convert]::FromBase64String('$b64'))
Write-Host 'launcher=written'
"@ | Out-Null

    if ($Token) { Ok "启动器已写（token 长度 $($Token.Length)，不回显）" } else { Warn '启动器已写（未带 token）' }
}

function Remote-Start {
    Assert-Remote
    Step '启动 sidecar（服务器）'
    Remote-WriteLauncher | Out-Null

    # ⚠️ 只结束本 sidecar 的进程：过渡期旧 sidecar 可能还在跑，`Get-Process node | Stop-Process`
    #    会把两个一起打死（旧脚本就是这么写的，这里刻意不沿用）。
    $out = Invoke-Remote @"
schtasks /create /f /tn $TaskName /tr '$remoteSidecar\run-web-sidecar.cmd' /sc onstart /ru SYSTEM 2>&1 | Out-Null;
schtasks /end /tn $TaskName 2>&1 | Out-Null;
Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { `$_.CommandLine -like '*douyin-web-sidecar*' } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force -ErrorAction SilentlyContinue };
Start-Sleep -Seconds 2;
schtasks /run /tn $TaskName 2>&1 | Out-Null;
Start-Sleep -Seconds 20;
Write-Host ('procs=' + ((Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { `$_.CommandLine -like '*douyin-web-sidecar*' } | Measure-Object).Count));
if (Test-Path '$remoteSidecar\sidecar.log') { Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 -Tail 12 }
"@
    Write-Host $out
    if ($out -notmatch 'procs=[1-9]') { throw 'sidecar 没起来 —— 看服务器上的 sidecar.log' }
    Ok '已启动（接下来在客户端点「登录」，扫二维码）'
}

function Remote-Stop {
    Assert-Remote
    Step '停止 sidecar（服务器）'
    $out = Invoke-Remote @"
schtasks /end /tn $TaskName 2>&1 | Out-Null;
Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { `$_.CommandLine -like '*douyin-web-sidecar*' } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force -ErrorAction SilentlyContinue };
Start-Sleep -Seconds 2;
Write-Host ('procs=' + ((Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { `$_.CommandLine -like '*douyin-web-sidecar*' } | Measure-Object).Count))
"@
    Write-Host $out
    Ok '已停止'
}

function Remote-Logs {
    Assert-Remote
    Write-Host "跟随服务器 $remoteSidecar\sidecar.log（Ctrl+C 退出）`n" -ForegroundColor Cyan
    & ssh -o BatchMode=yes $remote "powershell -NoProfile -Command `"Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 -Tail 40 -Wait`""
}

function Remote-LoginCheck {
    Assert-Remote
    Step '登录态体检（只看不写）'
    # profile 是登录态的唯一载体：看它多大、有没有 cookie 落盘
    $out = Invoke-Remote @"
`$p = '$remoteSidecar\data\douyin-web-profile';
if (Test-Path `$p) {
  `$bytes = (Get-ChildItem `$p -Recurse -File | Measure-Object -Property Length -Sum).Sum;
  Write-Host ('profileMB=' + [math]::Round(`$bytes/1MB,1));
} else { Write-Host 'profileMB=missing' }
if (Test-Path '$remoteSidecar\sidecar.log') {
  Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 | Select-String '已登录|登录成功|二维码|旁听入站' | Select-Object -Last 6
}
"@
    Write-Host $out
    if ($out -match 'profileMB=missing') { Warn '还没有 profile：说明一次都没登录成功（或 profileDir 配错了）' }
}

# ─────────────────────────── local（本机开发）───────────────────────────

function Local-Run([string]$npmArgs) {
    Assert-LocalNode
    Push-Location $sidecarDir
    try {
        Write-Host "`n> npm $npmArgs" -ForegroundColor DarkGray
        & npm $npmArgs
        if ($LASTEXITCODE -ne 0) { throw "npm $npmArgs 失败（$LASTEXITCODE）" }
    }
    finally {
        Pop-Location
    }
}

# ─────────────────────────── 入口 ───────────────────────────

switch ($Mode) {
    'remote' {
        switch ($Action) {
            'status' { Remote-Status }
            'install' { Remote-Install }
            'build' { Remote-Build }
            'start' { Remote-Start }
            'stop' { Remote-Stop }
            'restart' { Remote-Stop; Remote-Start }
            'logs' { Remote-Logs }
            'login-check' { Remote-LoginCheck }
        }
    }

    'local' {
        switch ($Action) {
            'status' { Assert-LocalNode; Write-Host "本地目录：$sidecarDir" }
            'install' { Local-Run 'install' }
            'build' { Local-Run 'run build' }
            'start' { Local-Run 'start' }
            'stop' { Warn '本地模式：Ctrl+C 结束即可（脚本不替你杀进程）' }
            'restart' { Warn '本地模式：Ctrl+C 后重新 -Action start' }
            'logs' { Warn '本地模式日志直接打在窗口里' }
            'login-check' {
                $profile = Join-Path $sidecarDir 'data\douyin-web-profile'
                if (Test-Path $profile) {
                    $mb = [math]::Round(((Get-ChildItem $profile -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
                    Ok "profile 大小 ${mb} MB"
                }
                else {
                    Warn "还没有 profile：$profile"
                }
            }
        }
    }
}
