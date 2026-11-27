<#
.SYNOPSIS
    ZipperAIQQ **全量部署编排**（模板）。

.DESCRIPTION
    三个脚本的分工（本文件是**编排**，不重复实现任何一侧的细节）：
      - `deploy-body.local.ps1`              → 服务端本体（计划任务 ZipperAIQQ）
      - `douyin-web-sidecar-deploy.local.ps1` → 抖音侧（计划任务 ZipperAIQQ-DouyinWeb）
      - 本文件                                → 两边一起，按正确顺序

    顺序是有讲究的：
      停：先 sidecar（它反连本体、还锁着浏览器 profile）→ 再本体
      起：先本体（sidecar 要连它）→ 再 sidecar
      备份/上传：在两边都停、**上传之前**做，回滚点才是"改之前"的样子。

.PARAMETER Action
    deploy（默认）| status（只读）| stop | start | restart | rollback

.PARAMETER SkipSidecar
    只部署服务端本体（等价于直接跑 `deploy-body.local.ps1`）。

.PARAMETER SkipBody
    只部署抖音侧。

.PARAMETER SkipBuild
    透传给本体：复用上次发布产物。

.PARAMETER SkipBackup
    跳过备份（本编排默认会备份整个目录；只在已知场景用）。

.PARAMETER SidecarToken
    透传给抖音侧；不给就沿用服务器 launcher 里已有的那个。

.EXAMPLE
    .\deploy-server.local.ps1                     # 全量（本体 + 抖音侧）
    .\deploy-server.local.ps1 -Action status       # 两边状态
    .\deploy-server.local.ps1 -SkipSidecar         # 只发本体
    .\deploy-server.local.ps1 -SkipBody            # 只发抖音侧
    .\deploy-server.local.ps1 -Action rollback -BackupDir D:\App\<project-root>.bak-20261128-0214

.NOTES
    编码：本文件（及复制出的 .local 版）必须存为 **UTF-8 带 BOM**（Windows PowerShell 5.1 才认中文）。
#>
[CmdletBinding()]
param(
    [ValidateSet('deploy', 'status', 'stop', 'start', 'restart', 'rollback')]
    [string]$Action = 'deploy',

    [string]$Server = '<server-ip-redacted>',
    [string]$SshUser = '<ssh-user>',
    [string]$TargetPath = 'D:\App\<project-root>',

    [string]$BackupDir,
    [string]$SidecarToken = '',
    [switch]$SkipSidecar,
    [switch]$SkipBody,
    [switch]$SkipBuild,
    [switch]$SkipBackup
)

$ErrorActionPreference = 'Stop'

if ($Server -like '<*') { throw '还没填服务器地址：复制成 .local.ps1 并改 -Server（或用参数传）' }

$bodyScript = Join-Path $PSScriptRoot 'deploy-body.local.ps1'
$sidecarScript = Join-Path $PSScriptRoot 'douyin-web-sidecar-deploy.local.ps1'

foreach ($s in @($bodyScript, $sidecarScript)) {
    if (-not (Test-Path $s)) { throw "缺少配套脚本：$s" }
}

$doBody = -not $SkipBody
$doSidecar = -not $SkipSidecar

function Step([string]$t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok([string]$t) { Write-Host "  OK  $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "  !!  $t" -ForegroundColor Yellow }

$common = @{
    Server = $Server
    SshUser = $SshUser
    TargetPath = $TargetPath
}

$remote = "$SshUser@$Server"
$remotePosix = $TargetPath -replace '\\', '/'

<# 远端执行：stderr 不许变成终止错误（PS 5.1 会把原生 stderr 当 NativeCommandError 抛，哪怕 exit code 是 0）。 #>
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

function Show-AllStatus {
    Step '两边状态'
    $info = Invoke-Remote @"
Write-Host ('host=' + `$env:COMPUTERNAME);
Write-Host ('now=' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'));
Write-Host ('serverProc=' + (Get-Process ZipperAIQQ.Server -ErrorAction SilentlyContinue | Measure-Object).Count);
Write-Host ('serverExeAt=' + (Get-Item '$TargetPath\ZipperAIQQ.Server.exe' -ErrorAction SilentlyContinue).LastWriteTime);
Write-Host ('diskFreeGB=' + [math]::Round((Get-PSDrive D).Free/1GB,1));
Write-Host ('task-server=' + ((schtasks /query /tn ZipperAIQQ /fo list 2>&1 | Select-String 'Status|状态') -join ' '));
Write-Host ('task-sidecar=' + ((schtasks /query /tn ZipperAIQQ-DouyinWeb /fo list 2>&1 | Select-String 'Status|状态') -join ' '));
Write-Host ('sidecarProcs=' + ((Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { `$_.CommandLine -like '*dist*main.js*' } | Measure-Object).Count));
"@
    Write-Host $info
}

<#
    备份整个部署目录（本体 + sidecar + data）。
    用 robocopy 并排除**运行中的 Chromium profile**（465 MB，被 sidecar 的浏览器锁着，
    Copy-Item 会因 Cookies 占用直接失败 —— 实测踩到过）。
#>
function Backup-All {
    Step '备份 + 校验（整个部署目录）'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmm'
    $path = "D:\App\<project-root>.bak-$stamp"

    $report = Invoke-Remote @"
`$ErrorActionPreference = 'Stop'

if ($Server -like '<*') { throw '还没填服务器地址：复制成 .local.ps1 并改 -Server（或用参数传）' };
`$bak = '$path';
if (Test-Path `$bak) { throw ('备份目录已存在: ' + `$bak) }
`$exclude1 = '$TargetPath\douyin-web-sidecar\data\douyin-web-profile';
`$exclude2 = '$TargetPath\.playwright';
robocopy '$TargetPath' `$bak /E /XD `$exclude1 `$exclude2 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null;
`$code = `$LASTEXITCODE;
Write-Host ('robocopyExit=' + `$code);
if (`$code -ge 8) { throw ('robocopy 失败（exit ' + `$code + '）') }
`$db = '$TargetPath\data\zipperaiqq.db';
`$dbBak = Join-Path `$bak 'data\zipperaiqq.db';
Write-Host ('backup=' + `$bak);
Write-Host ('serverExe=' + (Test-Path (Join-Path `$bak 'ZipperAIQQ.Server.exe')));
Write-Host ('sidecarDist=' + (Test-Path (Join-Path `$bak 'douyin-web-sidecar\dist\main.js')));
if (Test-Path `$db) {
  Write-Host ('dbHashEqual=' + ((Get-FileHash `$db -Algorithm SHA256).Hash -eq (Get-FileHash `$dbBak -Algorithm SHA256).Hash));
}
Write-Host ('storageState=' + (Test-Path (Join-Path `$bak 'douyin-web-sidecar\data\storage-state.json')));
"@
    Write-Host $report

    if ($report -notmatch 'robocopyExit=([0-7])') { throw '备份 robocopy 退出码异常（>=8 或没输出），已中止' }
    if ($report -match 'dbHashEqual=False') { throw '备份 db 哈希与源不一致，已中止' }
    if ($report -notmatch 'serverExe=True') { throw '备份里没有服务端主程序，已中止' }
    Ok "备份完成：$path（浏览器 profile 未备份，登录态走 storage-state.json）"
    return $path
}

# ─────────────────────────── 入口 ───────────────────────────

if (-not (Test-Connection -ComputerName $Server -Count 1 -Quiet)) { throw "ping 不通 $Server" }

switch ($Action) {
    'status' {
        Show-AllStatus
        exit 0
    }

    'stop' {
        if ($doSidecar) { & $sidecarScript -Action stop @common }
        if ($doBody) { & $bodyScript -Action stop @common }
        exit 0
    }

    'start' {
        # 本体先起：sidecar 要连它
        if ($doBody) { & $bodyScript -Action start @common }
        if ($doSidecar) { & $sidecarScript -Action start @common -Token $SidecarToken }
        exit 0
    }

    'restart' {
        if ($doSidecar) { & $sidecarScript -Action stop @common }
        if ($doBody) { & $bodyScript -Action restart @common }
        else { & $sidecarScript -Action start @common -Token $SidecarToken }
        exit 0
    }

    'rollback' {
        if (-not $BackupDir) { throw '回滚需要 -BackupDir' }

        # 先停两边，再让本体脚本做「改名 → 复制 → 起」
        if ($doSidecar) { & $sidecarScript -Action stop @common }

        & $bodyScript -Action rollback -BackupDir $BackupDir @common

        if ($doSidecar) { & $sidecarScript -Action start @common -Token $SidecarToken }
        Ok '回滚完成（两边都已恢复并验证）'
        exit 0
    }
}

# ── deploy（全量编排）──
Show-AllStatus

# ① 先停两边：sidecar 反连本体、还锁着浏览器 profile；本体锁着 SQLite
#    —— 只要还有一边在跑，备份就取不到 db 哈希（实测：Get-FileHash 报文件被占用）
if ($doSidecar) { & $sidecarScript -Action stop @common }
if ($doBody) { & $bodyScript -Action stop @common }

# ② 备份（整个目录；两边都已停）
$backup = $null
if ($SkipBackup) { Warn '跳过备份（-SkipBackup）' }
else { $backup = Backup-All }

# ③ 本体：发布 → 上传 → 起 → 验证（备份已由本编排做过，传 -SkipBackup 避免重复；
#    它内部的 stop 是幂等的，此时进程已经是 0）
if ($doBody) {
    $bodyArgs = @{ Action = 'deploy'; SkipBackup = $true }
    if ($SkipBuild) { $bodyArgs['SkipBuild'] = $true }
    & $bodyScript @bodyArgs @common
}
else { Warn '跳过服务端本体（-SkipBody）' }

# ④ 抖音侧：同步源码 → 远端编译 → 起（此时本体已就绪）
if ($doSidecar) {
    & $sidecarScript -Action deploy @common
    & $sidecarScript -Action start @common -Token $SidecarToken
}
else { Warn '跳过抖音侧（-SkipSidecar）' }

Step '全量部署完成'
Write-Host "  服务端：$Server`:$TargetPath（任务 ZipperAIQQ）"
Write-Host "  抖音侧：$Server`:$TargetPath\douyin-web-sidecar（任务 ZipperAIQQ-DouyinWeb）"
Write-Host "  日志：$TargetPath\stdout.log ｜ $TargetPath\douyin-web-sidecar\sidecar.log"
if ($backup) {
    Write-Host "  备份：$backup"
    Write-Host "  回滚：.\deploy-server.local.ps1 -Action rollback -BackupDir `"$backup`""
}
