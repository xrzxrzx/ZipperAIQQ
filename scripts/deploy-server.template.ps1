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
    [switch]$WithSidecar,
    [string]$SidecarToken,
    [switch]$InstallNode,
    [string]$NodeVersion = '22.23.4'
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


function Deploy-Sidecar {
    Step '部署抖音 sidecar'
    $sidecarDir = Join-Path $repoRoot 'douyin-sidecar'
    if (-not (Test-Path $sidecarDir)) { Warn "没有 $sidecarDir，跳过"; return }

    $remoteSidecar = "$TargetPath\douyin-sidecar"
    $remoteSidecarPosix = $remoteSidecar -replace '\\', '/'

    # ① Node：没有就装（MSI 静默安装；-InstallNode 才动手，避免静默改系统）
    $nodeVersion = Invoke-Remote "if (Get-Command node -ErrorAction SilentlyContinue) { (node -v) } else { 'missing' }"
    if ($nodeVersion -notmatch '^v\d+') {
        if (-not $InstallNode) {
            Warn '服务器上没有 Node；加 -InstallNode 才会装（会被装成系统级）'
            return
        }

        Step "安装 Node v$NodeVersion（MSI 静默）"
        $msi = "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-x64.msi"
        $install = Invoke-Remote @"
`$msi = Join-Path `$env:TEMP 'node-install.msi';
`$ProgressPreference = 'SilentlyContinue';
Invoke-WebRequest -Uri '$msi' -OutFile `$msi -UseBasicParsing;
`$p = Start-Process msiexec.exe -ArgumentList '/i', `$msi, '/qn', '/norestart' -Wait -PassThru;
Remove-Item `$msi -Force -ErrorAction SilentlyContinue;
'miExit=' + `$p.ExitCode + ' node=' + (& 'C:\Program Files\nodejs\node.exe' -v)
"@
        Write-Host "  $install"
        if ($install -notmatch 'node=v\d+') { throw 'Node 安装后仍不可用，停止部署 sidecar' }
        Ok 'Node 已安装'
    }
    else { Ok "服务器已有 node $nodeVersion" }

    # ② 传源码与依赖清单（不含 node_modules / data / 登录态）
    Invoke-Remote "New-Item -ItemType Directory -Force -Path '$remoteSidecar\src' | Out-Null; 'ok'" | Out-Null
    foreach ($f in @('package.json', 'src/bridge.ts')) {
        & scp -q -o BatchMode=yes (Join-Path $sidecarDir ($f -replace '/', '\')) "${remote}:$remoteSidecarPosix/$f"
        if ($LASTEXITCODE -ne 0) { throw "sidecar 上传失败：$f" }
    }
    Ok 'sidecar 源码已传'

    # ③ 启动脚本（token 写在这个只在本机运行的 cmd 里；不放进 git、不写进日志）
    $tokenLine = if ($SidecarToken) { "set ZIPPER_DOUYIN_TOKEN=$SidecarToken" } else { 'rem 未配置 token（服务端若配了 token，这里必须一致）' }
    $cmd = @"
@echo off
cd /d $remoteSidecar
set ZIPPER_DOUYIN_SERVER_URL=ws://127.0.0.1:5180/ws/channel
$tokenLine
set ZIPPER_DOUYIN_DATA_DIR=data
set ZIPPER_DOUYIN_LOG_LEVEL=info
set PATH=C:\Program Files\nodejs;%PATH%
node dist\bridge.js >> sidecar.log 2>&1
"@
    $tmp = Join-Path $env:TEMP 'zipper-sidecar-run.cmd'
    Set-Content -Path $tmp -Value $cmd -Encoding ASCII
    & scp -q -o BatchMode=yes $tmp "${remote}:$remoteSidecarPosix/run-sidecar.cmd"
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) { throw 'run-sidecar.cmd 上传失败' }

    # ④ 装依赖 + 编译 + 注册计划任务 + 启动
    $r = Invoke-Remote @"
Set-Location '$remoteSidecar';
`$env:Path = 'C:\Program Files\nodejs;' + `$env:Path;
& npm install --no-audit --no-fund 2>&1 | Select-Object -Last 3;
& npm run build 2>&1 | Select-Object -Last 3;
Write-Host ('build exit=' + `$LASTEXITCODE);
if (`$LASTEXITCODE -eq 0) {
  schtasks /create /f /tn ZipperAIQQ-DouyinSidecar /tr '$remoteSidecar\run-sidecar.cmd' /sc onstart /ru SYSTEM 2>&1 | Out-Null;
  schtasks /end /tn ZipperAIQQ-DouyinSidecar 2>&1 | Out-Null;
  Start-Sleep -Seconds 1;
  schtasks /run /tn ZipperAIQQ-DouyinSidecar 2>&1 | Out-Null;
  Start-Sleep -Seconds 12;
  Write-Host ('nodeCount=' + (Get-Process node -ErrorAction SilentlyContinue | Measure-Object).Count);
  if (Test-Path 'sidecar.log') { Get-Content 'sidecar.log' -Tail 6 }
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

        if ($WithSidecar) {
            Deploy-Sidecar
        }
    }
}

Step '完成'
Write-Host "  远端：$Server`:$TargetPath"
Write-Host "  日志：$TargetPath\stdout.log"
Write-Host "  任务：$TaskName（开机自启 / SYSTEM 账户）"