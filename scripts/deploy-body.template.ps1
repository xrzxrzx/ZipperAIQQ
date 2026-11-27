<#
.SYNOPSIS
    ZipperAIQQ **服务端本体**专用部署脚本（模板）。**只管本体**。

.DESCRIPTION
    与另外两个脚本的分工：
      - 本脚本：只碰 `ZipperAIQQ.Server`（计划任务 `ZipperAIQQ`）+ 它的外置工具（tools/），
        改服务端代码时不必动抖音 sidecar；
      - `douyin-web-sidecar-deploy.local.ps1`：只管抖音侧；
      - `deploy-server.local.ps1`：全量编排（就是依次调上面两个）。

    链路：发布 → 停 → 备份 + 校验 → 上传（主程序 / silk 解码器 / ffmpeg）→ 起 → 验证。

.PARAMETER Action
    deploy（默认）| status（只读）| stop | start | restart | rollback

.PARAMETER SkipBuild
    复用上次 artifacts/deploy 产物（不重新 dotnet publish）。

.PARAMETER SkipBackup
    跳过备份（**不建议**；全量部署时由编排脚本接管备份，会传这个开关）。

.PARAMETER SyncConfig
    同时把本地 artifacts/server.yaml 推到服务器（**默认不同步** —— 服务器上的往往更新）。

.EXAMPLE
    .\deploy-body.local.ps1                # 发布 + 备份 + 部署 + 验证
    .\deploy-body.local.ps1 -Action status  # 只看状态
    .\deploy-body.local.ps1 -SkipBackup     # 编排脚本用：备份已由外层做过

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
    [string]$TaskName = 'ZipperAIQQ',
    [string]$ProcessName = 'ZipperAIQQ.Server',

    [string]$BackupDir,
    [switch]$SkipBuild,
    [switch]$SkipBackup,
    [switch]$SyncConfig
)

$ErrorActionPreference = 'Stop'

if ($Server -like '<*') { throw '还没填服务器地址：复制成 .local.ps1 并改 -Server（或用参数传）' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts/deploy'
$silkDir = Join-Path $repoRoot 'tools/silk-decode'
$remote = "$SshUser@$Server"
$remotePosix = $TargetPath -replace '\\', '/'

function Step([string]$t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok([string]$t) { Write-Host "  OK  $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "  !!  $t" -ForegroundColor Yellow }

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

function Show-Status {
    Step "状态 $Server（服务端本体）"
    $info = Invoke-Remote @"
Write-Host ('host=' + `$env:COMPUTERNAME);
Write-Host ('now=' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'));
Write-Host ('process=' + (Get-Process $ProcessName -ErrorAction SilentlyContinue | Measure-Object).Count);
Write-Host ('exeAt=' + (Get-Item '$TargetPath\$ProcessName.exe' -ErrorAction SilentlyContinue).LastWriteTime);
Write-Host ('diskFreeGB=' + [math]::Round((Get-PSDrive D).Free/1GB,1));
Write-Host ('task=' + ((schtasks /query /tn $TaskName /fo list 2>&1 | Select-String 'Status|状态') -join ' '));
Write-Host 'logTail:'; Get-Content '$TargetPath\stdout.log' -Tail 3
"@
    Write-Host $info
}

function Stop-Body {
    Step '停止服务端本体'
    Invoke-Remote "schtasks /end /tn $TaskName | Out-Null; Start-Sleep -Seconds 1; taskkill /F /IM $ProcessName.exe 2>&1 | Out-Null; Start-Sleep -Seconds 3; 'remaining=' + (Get-Process $ProcessName -ErrorAction SilentlyContinue | Measure-Object).Count" |
        ForEach-Object { Write-Host "  $_" }
    $alive = Invoke-Remote "(Get-Process $ProcessName -ErrorAction SilentlyContinue | Measure-Object).Count"
    if ($alive -ne '0') { throw "停不下来：仍有 $alive 个进程，已中止（未做任何文件操作）" }
    Ok '已停止（进程数 0）'
}

function Start-Body {
    Step '启动服务端本体'
    $r = Invoke-Remote "schtasks /run /tn $TaskName"
    if ($r -notmatch 'SUCCESS') { throw "启动失败：$r" }
    Write-Host '  等待 16 秒...'
    Start-Sleep -Seconds 16
}

function Backup-Remote {
    Step '备份 + 校验'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmm'
    $path = "D:\App\<project-root>.bak-$stamp"

    $report = Invoke-Remote @"
`$ErrorActionPreference = 'Stop'

if ($Server -like '<*') { throw '还没填服务器地址：复制成 .local.ps1 并改 -Server（或用参数传）' };
`$bak = '$path';
if (Test-Path `$bak) { throw ('备份目录已存在: ' + `$bak) }
# robocopy 而不是 Copy-Item：① 大目录快得多；② 必须排除运行中的 Chromium profile
# （douyin-web-profile 465 MB，被 sidecar 的浏览器锁着，Copy-Item 会因 Cookies 占用直接失败）；
# ③ 排除 .playwright（1.3 GB 的浏览器二进制，属"环境"不属"代码"——回滚时会从旧目录移回来）
`$exclude1 = '$TargetPath\douyin-web-sidecar\data\douyin-web-profile';
`$exclude2 = '$TargetPath\.playwright';
robocopy '$TargetPath' `$bak /E /XD `$exclude1 `$exclude2 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null;
`$code = `$LASTEXITCODE;
Write-Host ('robocopyExit=' + `$code);
if (`$code -ge 8) { throw ('robocopy 失败（exit ' + `$code + '）') }
`$db = '$TargetPath\data\zipperaiqq.db';
`$dbBak = Join-Path `$bak 'data\zipperaiqq.db';
Write-Host ('backup=' + `$bak);
Write-Host ('serverExe=' + (Test-Path (Join-Path `$bak '$ProcessName.exe')));
Write-Host ('sidecarDist=' + (Test-Path (Join-Path `$bak 'douyin-web-sidecar\dist\main.js')));
if (Test-Path `$db) {
  Write-Host ('dbHashEqual=' + ((Get-FileHash `$db -Algorithm SHA256).Hash -eq (Get-FileHash `$dbBak -Algorithm SHA256).Hash));
}
Write-Host ('storageState=' + (Test-Path (Join-Path `$bak 'douyin-web-sidecar\data\storage-state.json')));
"@
    Write-Host $report

    # 硬判据三条（文件数不作判据：备份里排除了运行中的 Chromium profile，数量天然对不上）
    if ($report -notmatch 'robocopyExit=([0-7])') { throw '备份 robocopy 退出码异常（>=8 或没输出），已中止' }
    if ($report -match 'dbHashEqual=False') { throw '备份 db 哈希与源不一致，已中止' }
    if ($report -notmatch 'serverExe=True') { throw '备份里没有服务端主程序，已中止' }
    Ok "备份完成：$path（浏览器 profile 未备份，登录态走 storage-state.json）"
    return $path
}

function Publish-Body {
    if ($SkipBuild) {
        Warn "跳过发布（-SkipBuild）：复用 $publishDir"
        if (-not (Test-Path $publishDir)) { throw "没有发布产物：$publishDir" }
        return
    }

    Step '发布服务端（Release / self-contained，服务器无需装 .NET）'
    & dotnet publish (Join-Path $repoRoot 'ZipperAIQQ.Server/ZipperAIQQ.Server.csproj') `
        -c Release -o $publishDir -r win-x64 --self-contained true -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }
    Ok ("产物 {0} MB" -f [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1))
}

function Push-Body {
    Step '上传服务端本体'
    if (-not (Test-Path $publishDir)) { throw "没有发布产物：$publishDir" }
    $size = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & scp -r -q -o BatchMode=yes "$publishDir/*" "${remote}:$remotePosix/"
    if ($LASTEXITCODE -ne 0) { throw "scp 失败（exit $LASTEXITCODE）" }
    Ok ("主程序已传（{0} MB，{1:N1}s）" -f $size, $sw.Elapsed.TotalSeconds)

    $dirs = Invoke-Remote "New-Item -ItemType Directory -Force -Path '$TargetPath\temp\bge-small-zh-v1.5','$TargetPath\config','$TargetPath\tools\silk-decode\vendor' | Out-Null; 'ok'"
    if ($dirs -ne 'ok') { throw '远端目录准备失败' }

    if ($SyncConfig) {
        $yaml = Join-Path $repoRoot 'artifacts/server.yaml'
        if (Test-Path $yaml) {
            Warn '正在覆盖远端 config/server.yaml（-SyncConfig）'
            & scp -q -o BatchMode=yes $yaml "${remote}:$remotePosix/config/"
            if ($LASTEXITCODE -ne 0) { throw 'yaml 上传失败' }
            Ok 'server.yaml 已同步'
        }
        else { Warn "本机没有 $yaml，跳过配置同步" }
    }
}

<#
    外置工具（都不入库的大件 / 运行期依赖）：
      - tools/silk-decode：语音 SILK → WAV（silk-wasm 已 vendored，283 KB）
      - tools/ffmpeg：语音 AMR → WAV（82 MB，缺了就用 npm 装一份 ffmpeg-static）
#>
function Ensure-Tools {
    if (Test-Path $silkDir) {
        & scp -r -q -o BatchMode=yes "$silkDir/*" "${remote}:$remotePosix/tools/silk-decode/"
        if ($LASTEXITCODE -ne 0) { throw 'silk 解码器上传失败' }
        Ok 'SILK 解码器已传（tools/silk-decode）'
    }
    else { Warn "本机没有 $silkDir：SILK 语音只能依赖 NapCat 侧转码" }

    $hasFfmpeg = Invoke-Remote "Test-Path '$TargetPath\tools\ffmpeg\ffmpeg.exe'"
    if ($hasFfmpeg -match 'True') {
        Ok 'ffmpeg 已在远端（tools/ffmpeg）'
        return
    }

    Warn '远端没有 ffmpeg，正在装一份（ffmpeg-static，约 82 MB）'
    $r = Invoke-Remote @"
`$root = '$TargetPath';
`$dst = Join-Path `$root 'tools\ffmpeg';
New-Item -ItemType Directory -Force -Path `$dst | Out-Null;
`$work = Join-Path `$root 'tools\ffmpeg\_npm';
New-Item -ItemType Directory -Force -Path `$work | Out-Null;
Set-Location `$work;
if (-not (Test-Path (Join-Path `$work 'package.json'))) { npm init -y | Out-Null }
npm i ffmpeg-static --no-audit --no-fund 2>&1 | Select-Object -Last 2;
Copy-Item (Join-Path `$work 'node_modules\ffmpeg-static\ffmpeg.exe') (Join-Path `$dst 'ffmpeg.exe') -Force;
Set-Location `$root;
Remove-Item `$work -Recurse -Force -ErrorAction SilentlyContinue;
Write-Host ('ffmpeg=' + (Test-Path (Join-Path `$dst 'ffmpeg.exe')))
"@
    Write-Host $r
    if ($r -notmatch 'ffmpeg=True') { Warn 'ffmpeg 安装失败：非 WAV 语音只能靠 silk-wasm（SILK）或 NapCat 侧转码' }
    else { Ok 'ffmpeg 已就位（tools/ffmpeg）' }
}

function Test-Body {
    Step '验证服务端本体'
    $probe = Invoke-Remote @"
`$p = (Get-Process $ProcessName -ErrorAction SilentlyContinue | Measure-Object).Count;
Write-Host ('process=' + `$p);
if (`$p -eq 0) { exit 1 }
Get-Content '$TargetPath\stdout.log' -Tail 400 | Select-String '数据库就绪|消息路由已启动|ERR|FTL' | Select-Object -Last 8 | ForEach-Object Line
"@
    Write-Host $probe

    # 「消息路由已启动」只作弱判据：热启动 + sidecar 重连会打出几十行把它挤出窗口
    if ($probe -notmatch '消息路由已启动') {
        Warn '未在最近 400 行日志里看到「消息路由已启动」，以进程与接口为准'
    }
    if ($probe -match ' FTL ') { throw '日志出现 FTL，需人工确认' }

    $yamlLocal = Join-Path $repoRoot 'artifacts/server.yaml'
    if (-not (Test-Path $yamlLocal)) {
        Warn "本机没有 $yamlLocal，跳过接口探测（只验证了进程与日志）"
        Ok '进程 + 日志两项通过'
        return
    }

    $tokenLine = (Get-Content $yamlLocal -Encoding UTF8 | Select-String 'httpToken:').Line
    $token = ($tokenLine -split ':', 2)[1].Trim().Trim('"')
    $headers = @{ Authorization = "Bearer $token" }

    foreach ($probeAction in @('ping', 'getStatus', 'getPlatforms')) {
        $body = @{ action = $probeAction; payload = @{} } | ConvertTo-Json -Depth 4
        try {
            $r = Invoke-RestMethod -Uri "http://${Server}:5180/api" -Method Post -Body $body `
                -ContentType 'application/json' -Headers $headers -TimeoutSec 20
        }
        catch {
            throw "接口 $probeAction 探测失败：$($_.Exception.Message)"
        }

        if ($r.code -ne 0) { throw "接口 $probeAction 返回 code=$($r.code)（$($r.message)）" }
        Ok "$probeAction -> code=0"
    }

    Ok '进程 + 日志 + 接口三项通过'
}

function Invoke-Rollback {
    if (-not $BackupDir) { throw '回滚需要 -BackupDir（备份目录，形如 D:\App\<project-root>.bak-yyyyMMdd-HHmm）' }
    Step "回滚到 $BackupDir"

    Stop-Body

    $r = Invoke-Remote @"
if (-not (Test-Path '$BackupDir')) { throw '备份目录不存在: $BackupDir' }
`$failed = '$TargetPath.failed-' + (Get-Date -Format 'yyyyMMdd-HHmmss');
Rename-Item '$TargetPath' `$failed;
robocopy '$BackupDir' '$TargetPath' /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null;
# .playwright（浏览器二进制）不在备份里：从旧目录移回来，否则 sidecar 回滚后没浏览器可跑
`$pw = Join-Path `$failed '.playwright';
if ((Test-Path `$pw) -and -not (Test-Path (Join-Path '$TargetPath' '.playwright'))) {
  Move-Item `$pw (Join-Path '$TargetPath' '.playwright') -Force;
  Write-Host 'browsers=restored-from-failed';
}
Write-Host ('robocopyExit=' + `$LASTEXITCODE);
Write-Host ('failedKept=' + `$failed);
Write-Host ('restored=' + (Test-Path '$TargetPath\$ProcessName.exe'));
"@
    Write-Host $r
    if ($r -notmatch 'restored=True') { throw '回滚未完成' }

    Start-Body
    Test-Body
    Ok '回滚完成（旧的失败目录保留在上面 failedKept 里，确认无误后可手工删）'
}

# ─────────────────────────── 入口 ───────────────────────────

if (-not (Test-Connection -ComputerName $Server -Count 1 -Quiet)) { throw "ping 不通 $Server" }

switch ($Action) {
    'status' { Show-Status; exit 0 }
    'stop' { Stop-Body; exit 0 }
    'start' { Start-Body; Test-Body; exit 0 }
    'restart' { Stop-Body; Start-Body; Test-Body; exit 0 }
    'rollback' { Invoke-Rollback; exit 0 }
}

# deploy
Show-Status
Publish-Body
Stop-Body

$backup = $null
if ($SkipBackup) { Warn '跳过备份（-SkipBackup）' }
else { $backup = Backup-Remote }

Push-Body
Ensure-Tools
Start-Body
Test-Body

Step '完成（服务端本体）'
Write-Host "  远端：$Server`:$TargetPath（任务 $TaskName）"
Write-Host "  日志：$TargetPath\stdout.log"
if ($backup) {
    Write-Host "  备份：$backup"
    Write-Host "  回滚：.\deploy-body.local.ps1 -Action rollback -BackupDir `"$backup`""
}
