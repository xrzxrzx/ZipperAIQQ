<#
.SYNOPSIS
    ZipperAIQQ 服务端部署脚本（Windows / 计划任务方案）。

.DESCRIPTION
    和 Linux 上一样：先停 -> 再传 -> 再拉起。

    为什么用计划任务而不是 Windows 服务：
      - ZipperAIQQ.Server.exe 是控制台程序，不实现 SCM 协议
        => sc create 会以 1053（服务未及时响应）失败
      - 而 Start-Process 启动的进程会随 SSH 会话结束被清理
        => 用「计划任务（SYSTEM + onstart）」托底：
           schtasks /run 启动、schtasks /end + taskkill 停止、开机自启

.PARAMETER Action
    deploy（默认：发布+传输+启动）| stop | start | restart（只停+起）

.EXAMPLE
    .\deploy-server.ps1
    .\deploy-server.ps1 -Action restart
    .\deploy-server.ps1 -Action stop

.NOTES
    前置：本机已配 administrator@<server> 的 SSH 免密。
#>
[CmdletBinding()]
param(
    [ValidateSet('deploy', 'stop', 'start', 'restart')]
    [string]$Action = 'deploy',
    [string]$Server = '<server-ip>',
    [string]$SshUser = 'administrator',
    [string]$TargetPath = '<target-path>',
    [string]$TaskName = 'ZipperAIQQ',
    [string]$ProcessName = 'ZipperAIQQ.Server',
    [switch]$SkipBuild,

    # ── 抖音 sidecar（和服务器一起部署）──
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'artifacts/deploy'
$remote = "$SshUser@$Server"
$remotePosix = $TargetPath -replace '\\', '/'

function Step([string]$t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Ok([string]$t) { Write-Host "  OK  $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "  !!  $t" -ForegroundColor Yellow }

function Invoke-Remote([string]$script) {
    $out = & ssh -o BatchMode=yes $remote $script 2>&1
    return ($out | Out-String).Trim()
}

function Stop-Remote {
    Step '停止远端服务'
    Invoke-Remote "schtasks /end /tn $TaskName 2>`$null; taskkill /F /IM $ProcessName.exe 2>`$null; 'stopped'" | Out-Null
    Start-Sleep -Seconds 2
    $alive = Invoke-Remote "(Get-Process $ProcessName -ErrorAction SilentlyContinue | Measure-Object).Count"
    if ($alive -eq '0') { Ok '已停止' } else { Warn "仍有 $alive 个进程" }
}

function Start-Remote {
    Step '启动远端服务'
    $r = Invoke-Remote "schtasks /run /tn $TaskName"
    if ($r -match 'SUCCESS') { Ok '计划任务已触发' } else { Warn $r }

    Write-Host '  等待 14 秒...'
    Start-Sleep -Seconds 14

    $line = Invoke-Remote "tasklist /fi `"imagename eq $ProcessName.exe`" /fo csv /nh"
    if ($line -match $ProcessName) {
        Ok "进程在跑：$line"
        $tail = Invoke-Remote "Get-Content '$TargetPath\stdout.log' -Tail 2"
        Write-Host "  日志: $tail"
        if ($tail -match '消息路由已启动') { Ok '启动正常（日志已确认）' } else { Warn '日志里没看到「消息路由已启动」' }
    }
    else {
        Warn '进程没起来 —— 去看远端 stdout.log'
    }
}


"@
    Write-Host $r

    if ($r -match 'nodeCount=0') { Warn 'sidecar 进程没起来 —— 看远端 sidecar.log' }
    else { Ok 'sidecar 已启动（首次登录需要扫码：看 sidecar.log 里「登录二维码已保存」的路径）' }
    Write-Host "  远端目录：$remoteSidecar"
    Write-Host "  远端日志：$remoteSidecar\sidecar.log"
    Write-Host '  ⚠️ 登录要人机交互：二维码文件在远端 data\login-qr.png，需要通过 RDP 打开或用 scp 取回'
}

if (-not (Test-Connection -ComputerName $Server -Count 1 -Quiet)) {
    throw "ping 不通 $Server"
}
Ok (Invoke-Remote "'host=' + `$env:COMPUTERNAME + ' targetExists=' + (Test-Path '$TargetPath')")

switch ($Action) {
    'stop' { Stop-Remote }
    'start' { Start-Remote }
    'restart' {
        Stop-Remote
        Start-Remote
    }
    'deploy' {
        Stop-Remote

        if (-not $SkipBuild) {
            Step '发布（Release / self-contained，服务器无需装 .NET）'
            & dotnet publish (Join-Path $repoRoot 'ZipperAIQQ.Server/ZipperAIQQ.Server.csproj') `
                -c Release -o $publishDir -r win-x64 --self-contained true -nologo
            if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }
            $size = [math]::Round((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
            Ok "产物 $size MB"
        }

        Step '传输主程序'
        & scp -r -q -o BatchMode=yes "$publishDir\*" "${remote}:$remotePosix/"
        if ($LASTEXITCODE -ne 0) { throw 'scp 主程序失败' }
        Ok '主程序已传'

        Step '传输附属文件（embedding 模型 / 表情表 / 人设）'
        Invoke-Remote "New-Item -ItemType Directory -Force -Path '$TargetPath\temp\bge-small-zh-v1.5','$TargetPath\config' | Out-Null; 'ok'" | Out-Null

        $modelDir = Join-Path $repoRoot 'temp/bge-small-zh-v1.5'
        if (Test-Path $modelDir) {
            & scp -q -o BatchMode=yes "$modelDir\*" "${remote}:$remotePosix/temp/bge-small-zh-v1.5/"
            Ok '模型已传'
        }
        else { Warn "本机没有模型目录：$modelDir" }

        foreach ($cfg in @('config/faces.qq.json', 'config/persona.md')) {
            $p = Join-Path $repoRoot $cfg
            if (Test-Path $p) { & scp -q -o BatchMode=yes $p "${remote}:$remotePosix/config/" }
        }

        # server.yaml：服务端主配置（含 token/key，**不入仓库**）
        # 放在 artifacts/ 下，已被 .gitignore 排除；这里每次部署同步，避免 YAML 改动漏传
        $yamlPath = Join-Path $repoRoot 'artifacts/server.yaml'
        if (Test-Path $yamlPath) {
            & scp -q -o BatchMode=yes $yamlPath "${remote}:$remotePosix/config/"
            Ok 'server.yaml 已同步（含 token/key，不入仓库）'
        }
        else {
            Warn "本机没有 $yamlPath —— 服务端会沿用已有的 config/server.yaml 或环境变量"
        }

        Ok '配置已传'

        Start-Remote

        # 抖音底座已换成 douyin-web-sidecar（Playwright 浏览器底座）；
        # 它的部署不在这份脚本里：见 docs/08-上手/抖音web底座部署与登录.md
        # 与 scripts/douyin-web-sidecar-deploy.template.ps1
    }
}

Step '完成'
Write-Host "  远端：$Server`:$TargetPath"
Write-Host "  日志：$TargetPath\stdout.log"
Write-Host "  任务：$TaskName（开机自启 / SYSTEM 账户）"