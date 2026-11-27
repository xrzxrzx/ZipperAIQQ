<#
.SYNOPSIS
    抖音 **浏览器底座** sidecar 专用管理脚本（模板）。**只管抖音侧**。

.DESCRIPTION
    与 `deploy-server.local.ps1`（全量）的分工：
      - 本脚本：只碰 `douyin-web-sidecar` 与计划任务 `ZipperAIQQ-DouyinWeb`，
        改 sidecar 代码时不必重启服务端本体；
      - 全量脚本：编排「本体 + 抖音侧」，内部就是调本脚本和 `deploy-body.local.ps1`。

    部署动作（deploy）= 同步源码 → 远端 npm install → 远端 tsc 编译，**不覆盖**运行态文件：
      config.json（profile 目录等）、data/（登录态）、sidecar.log、run-web-sidecar.cmd（含 token）。
      覆盖它们等于要求重新扫码，所以刻意不同步。

.PARAMETER Action
    status（默认）| deploy | install | build | start | stop | restart | logs | login-check

.PARAMETER Install
    为 `deploy` 追加 `npx playwright install chromium`（约 400 MB，只在换机 / 升 Playwright 时需要）。

.PARAMETER Token
    Sidecar Token；**不给就沿用服务器 launcher 里已有的那个**（避免覆盖成空 → 401）。

.EXAMPLE
    .\douyin-web-sidecar-deploy.local.ps1                 # 看状态
    .\douyin-web-sidecar-deploy.local.ps1 -Action deploy  # 同步源码 + 编译（不重启）
    .\douyin-web-sidecar-deploy.local.ps1 -Action restart # 重启 sidecar
    .\douyin-web-sidecar-deploy.local.ps1 -Action logs    # 跟日志

.NOTES
    编码：本文件（及复制出的 .local 版）必须存为 **UTF-8 带 BOM**（Windows PowerShell 5.1 才认中文）。
    计划任务身份：SYSTEM / onstart（没有交互桌面 → 只能 headless）。
#>
[CmdletBinding()]
param(
    [ValidateSet('status', 'deploy', 'install', 'build', 'start', 'stop', 'restart', 'logs', 'login-check')]
    [string]$Action = 'status',

    [string]$Server = '<server-ip-redacted>',
    [string]$SshUser = '<ssh-user>',
    [string]$TargetPath = 'D:\App\<project-root>',
    [string]$TaskName = 'ZipperAIQQ-DouyinWeb',
    [string]$Token = '',
    [switch]$Install,
    [switch]$SkipBuild,
    [string]$LogLevel = 'info',

    # Playwright 浏览器目录。默认放在**项目目录内**（<TargetPath>\.playwright）：
    # SYSTEM 与 Administrator 的家目录不同，装到各自的 AppData 里互相看不见
    # （实测：以 Administrator 装完，SYSTEM 任务仍报 Executable doesn't exist）。
    [string]$BrowsersPath
)

$ErrorActionPreference = 'Stop'

if ($Server -like '<*') { throw '还没填服务器地址：复制成 .local.ps1 并改 -Server（或用参数传）' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$sidecarDir = Join-Path $repoRoot 'douyin-web-sidecar'
$remote = "$SshUser@$Server"
$remoteSidecar = "$TargetPath\douyin-web-sidecar"

if ([string]::IsNullOrWhiteSpace($BrowsersPath)) {
    $BrowsersPath = "$TargetPath\.playwright"
}

function Step([string]$t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok([string]$t) { Write-Host "  OK  $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "  !!  $t" -ForegroundColor Yellow }

<#
    远端执行：stderr 不许变成终止错误。
    PS 5.1 在 $ErrorActionPreference='Stop' 下会把原生 stderr 当 NativeCommandError 抛，
    哪怕远端 exit code 是 0（备份"看似失败"就是这么来的）。
#>
function Invoke-Remote([string]$script) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & ssh -o BatchMode=yes -o ConnectTimeout=10 $remote $script 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prev
    }

    $text = ($out | Out-String).Trim()
    if ($code -ne 0) { throw "远端命令失败（exit $code）：$script`n$text" }
    return $text
}

# ⚠️ 进程判据用命令行里的 `dist\main.js`：launcher 是 `node dist\main.js`，
#    命令行里**不含目录名** —— 早先用 `*douyin-web-sidecar*` 永远数出 0（实测踩过）。
$sidecarProcessFilter = "Name='node.exe'"

function Remote-Status {
    Step "sidecar 状态（$Server）"
    $out = Invoke-Remote @"
Write-Host ('task=' + ((schtasks /query /tn $TaskName /fo list 2>&1 | Select-String 'Status|状态') -join ' '));
Write-Host ('procs=' + ((Get-CimInstance Win32_Process -Filter "$sidecarProcessFilter" | Where-Object { `$_.CommandLine -like '*dist*main.js*' } | Measure-Object).Count));
if (Test-Path '$remoteSidecar\sidecar.log') { Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 -Tail 6 }
"@
    Write-Host $out
    if ($out -match 'procs=[1-9]') { Ok 'sidecar 在运行' }
    else { Warn 'sidecar 进程数为 0 —— 用 -Action start 起来，或看日志' }
    if ($out -match '已连上 Server') { Ok '已连上服务端' }
    if ($out -match '抖音已上线|已登录') { Ok '抖音已登录' }
}

function Remote-Deploy {
    Step '同步源码（src / package.json / tsconfig）'
    if (-not (Test-Path $sidecarDir)) { throw "找不到 sidecar 目录：$sidecarDir" }

    $null = Invoke-Remote "New-Item -ItemType Directory -Force -Path '$remoteSidecar\src' | Out-Null; 'ok'"

    & scp -r -q -o BatchMode=yes "$sidecarDir/src" "${remote}:$remoteSidecar/"
    if ($LASTEXITCODE -ne 0) { throw 'sidecar src 上传失败' }

    foreach ($f in @('package.json', 'tsconfig.json', 'README.md')) {
        $local = Join-Path $sidecarDir $f
        if (Test-Path $local) {
            & scp -q -o BatchMode=yes $local "${remote}:$remoteSidecar/$f"
            if ($LASTEXITCODE -ne 0) { throw "sidecar $f 上传失败" }
        }
    }

    Ok '源码已传（config.json / data / launcher 保持原样）'

    Remote-Install
}

function Remote-Install {
    Step '远端装依赖'
    $extra = if ($Install) { 'npx playwright install chromium 2>&1 | Select-Object -Last 3;' } else { '' }
    $out = Invoke-Remote @"
`$env:Path = 'C:\Program Files\nodejs;' + `$env:Path;
Set-Location '$remoteSidecar';
npm install --no-audit --no-fund 2>&1 | Select-Object -Last 3;
$extra
Write-Host ('nodeModules=' + (Test-Path '$remoteSidecar\node_modules'))
"@
    Write-Host $out
    if ($out -notmatch 'nodeModules=True') { throw '依赖没装上：看上面的输出' }
    Ok '依赖就绪'

    if (-not $SkipBuild) { Remote-Build }

    Ensure-Chromium
}

<#
    Playwright 浏览器自愈（2026-11-28 实测踩到）。

    `npm install` 会把 Playwright 升到新版本，而**对应版本的 Chromium 不会自动下载** ——
    表现是 sidecar 进程起得来、也连上了 Server，但日志里
    `浏览器启动失败：Executable doesn't exist at ...ms-playwright\chromium_headless_shell-1194\...`，
    抖音通道实际是死的。不主动补这一句，就会「部署显示成功、抖音悄悄坏掉」。
#>
function Ensure-Chromium {
    $probe = Invoke-Remote @"
`$log = '$remoteSidecar\sidecar.log';
if (Test-Path `$log) {
  `$lines = Get-Content `$log -Encoding UTF8 -Tail 300;
  `$lastFail = (`$lines | Select-String "Executable doesn't exist|浏览器启动失败" | Select-Object -Last 1).LineNumber;
  `$lastOk = (`$lines | Select-String '抖音已登录|浏览器已就绪|已旁听入站' | Select-Object -Last 1).LineNumber;
  # 只有「最后一次失败晚于最后一次成功」才算真的缺浏览器；
  # 否则历史失败会把每次部署都误判成要重装（日志是追加的，旧记录一直在）
  `$need = (`$lastFail -ne `$null) -and ((`$lastOk -eq `$null) -or (`$lastFail -gt `$lastOk));
  Write-Host ('needBrowser=' + `$need);
}
"@
    Write-Host $probe

    if ($probe -match 'needBrowser=False') {
        Ok 'Chromium 与 Playwright 版本匹配'
        return
    }

    Warn 'Playwright 找不到对应版本的 Chromium —— 正在补装（约 150~400 MB，只在版本变化时需要）'
    $out = Invoke-Remote @"
`$env:Path = 'C:\Program Files\nodejs;' + `$env:Path;
`$env:PLAYWRIGHT_BROWSERS_PATH = '$BrowsersPath';
Set-Location '$remoteSidecar';
npx playwright install chromium 2>&1 | Select-Object -Last 5;
Write-Host ('installExit=' + `$LASTEXITCODE);
Write-Host ('browsersPath=' + `$env:PLAYWRIGHT_BROWSERS_PATH)
"@
    Write-Host $out

    if ($out -notmatch 'installExit=0') {
        Warn 'Chromium 补装失败：抖音通道会起不来（sidecar 进程在，但浏览器启动报 Executable doesn''t exist）'
        return
    }

    Ok 'Chromium 已补装（记得 restart sidecar 才生效）'
}

function Remote-Build {
    Step '远端编译（tsc）'
    $out = Invoke-Remote @"
`$env:Path = 'C:\Program Files\nodejs;' + `$env:Path;
Set-Location '$remoteSidecar';
npm run build 2>&1 | Select-Object -Last 8;
Write-Host ('buildExit=' + `$LASTEXITCODE);
Write-Host ('dist=' + (Test-Path '$remoteSidecar\dist\main.js'))
"@
    Write-Host $out
    if ($out -notmatch 'buildExit=0') { throw '编译失败：看上面的 tsc 输出' }
    if ($out -notmatch 'dist=True') { throw '编译产物缺失（dist/main.js）' }
    Ok '编译通过'
}

function Remote-WriteLauncher {
    # token 明文只存在该机器的本地 launcher 里（不进仓库 / 不进日志）
    $tokenLine = if ($Token) { "set ZIPPER_DOUYIN_WEB_TOKEN=$Token" } else { 'rem 未配置 token（loopback 免验）' }
    $content = @"
@echo off
cd /d $remoteSidecar
$tokenLine
set ZIPPER_DOUYIN_WEB_LOG_LEVEL=$LogLevel
set PLAYWRIGHT_BROWSERS_PATH=$BrowsersPath
node dist\main.js >> sidecar.log 2>&1
"@

    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($content))
    Invoke-Remote @"
[IO.File]::WriteAllBytes('$remoteSidecar\run-web-sidecar.cmd', [Convert]::FromBase64String('$b64'))
Write-Host 'launcher=written'
"@ | Out-Null

    if ($Token) { Ok "启动器已写（token 长度 $($Token.Length)，不回显）" } else { Warn '启动器已写（未带 token）' }
}

function Read-RemoteToken {
    if ($Token) { return $Token }

    $line = Invoke-Remote @"
if (Test-Path '$remoteSidecar\run-web-sidecar.cmd') {
  (Get-Content '$remoteSidecar\run-web-sidecar.cmd' -Encoding ASCII | Select-String 'ZIPPER_DOUYIN_WEB_TOKEN=' | Select-Object -First 1).Line
}
"@

    if ($line -match 'ZIPPER_DOUYIN_WEB_TOKEN=(.+)$') {
        $found = $Matches[1].Trim()
        if ($found) {
            Ok "已沿用服务器上的 token（长度 $($found.Length)，不回显）"
            return $found
        }
    }

    Warn '服务器上没有可沿用的 token；若服务端配了 Sidecar Token，这次启动会 401 —— 用 -Token 指定'
    return ''
}

function Remote-Start {
    Step '启动 sidecar'
    if (-not $Token) { $Token = Read-RemoteToken }
    Remote-WriteLauncher | Out-Null

    $out = Invoke-Remote @"
schtasks /create /f /tn $TaskName /tr '$remoteSidecar\run-web-sidecar.cmd' /sc onstart /ru SYSTEM 2>&1 | Out-Null;
schtasks /end /tn $TaskName 2>&1 | Out-Null;
Get-CimInstance Win32_Process -Filter "$sidecarProcessFilter" | Where-Object { `$_.CommandLine -like '*dist*main.js*' } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force -ErrorAction SilentlyContinue };
Start-Sleep -Seconds 2;
schtasks /run /tn $TaskName 2>&1 | Out-Null;
Start-Sleep -Seconds 20;
Write-Host ('procs=' + ((Get-CimInstance Win32_Process -Filter "$sidecarProcessFilter" | Where-Object { `$_.CommandLine -like '*dist*main.js*' } | Measure-Object).Count));
if (Test-Path '$remoteSidecar\sidecar.log') { Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 -Tail 10 }
"@
    Write-Host $out
    if ($out -notmatch 'procs=[1-9]') { throw 'sidecar 没起来 —— 看服务器上的 douyin-web-sidecar\sidecar.log' }
    Ok '已启动（需要登录就在客户端点「登录」扫码）'
}

function Remote-Stop {
    Step '停止 sidecar'
    $out = Invoke-Remote @"
schtasks /end /tn $TaskName 2>&1 | Out-Null;
Get-CimInstance Win32_Process -Filter "$sidecarProcessFilter" | Where-Object { `$_.CommandLine -like '*dist*main.js*' } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force -ErrorAction SilentlyContinue };
Start-Sleep -Seconds 2;
Write-Host ('procs=' + ((Get-CimInstance Win32_Process -Filter "$sidecarProcessFilter" | Where-Object { `$_.CommandLine -like '*dist*main.js*' } | Measure-Object).Count))
"@
    Write-Host $out
    if ($out -notmatch 'procs=0') { throw 'sidecar 停不下来' }
    Ok '已停止'
}

function Remote-Logs {
    Write-Host "跟随服务器 $remoteSidecar\sidecar.log（Ctrl+C 退出）`n" -ForegroundColor Cyan
    & ssh -o BatchMode=yes $remote "powershell -NoProfile -Command `"Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 -Tail 40 -Wait`""
}

function Remote-LoginCheck {
    Step '登录态体检（只看不写）'
    # profile 是登录态的唯一载体：看它多大、有没有 cookie 落盘
    $out = Invoke-Remote @"
`$p = '$remoteSidecar\data\douyin-web-profile';
if (Test-Path `$p) {
  `$bytes = (Get-ChildItem `$p -Recurse -File | Measure-Object -Property Length -Sum).Sum;
  Write-Host ('profileMB=' + [math]::Round(`$bytes/1MB,1));
} else { Write-Host 'profileMB=missing' }
Write-Host ('storageState=' + (Test-Path '$remoteSidecar\data\storage-state.json'));
if (Test-Path '$remoteSidecar\sidecar.log') {
  Get-Content '$remoteSidecar\sidecar.log' -Encoding UTF8 | Select-String '已登录|登录成功|二维码|旁听入站' | Select-Object -Last 6
}
"@
    Write-Host $out
    if ($out -match 'profileMB=missing') { Warn '还没有 profile：说明一次都没登录成功（或 profileDir 配错了）' }
}

# ─────────────────────────── 入口 ───────────────────────────

if (-not (Test-Connection -ComputerName $Server -Count 1 -Quiet)) { throw "ping 不通 $Server" }

switch ($Action) {
    'status' { Remote-Status }
    # deploy 只做「同步 + 编译」，**不重启、也不报进程状态** ——
    # 全量编排里它是"停着的时候同步"，此时报 procs=0 会被误读成失败
    'deploy' { Remote-Deploy }
    'install' { Remote-Install }
    'build' { Remote-Build }
    'start' { Remote-Start; Remote-Status }
    'stop' { Remote-Stop }
    'restart' { Remote-Stop; Remote-Start; Remote-Status }
    'logs' { Remote-Logs }
    'login-check' { Remote-LoginCheck }
}
