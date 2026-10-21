<#
.SYNOPSIS
    M1-A 探针启动脚本（模板，不含真实 token）。

.DESCRIPTION
    M1-A 的验收目标是「收到真实 QQ 消息并入库」。
    本脚本拉起一个最小探针：连 NapCat → 过滤 → 写入 SQLite → 打印。

    用法：
      1. 复制本文件为 run-m1a-probe.local.ps1（同目录）
      2. 在副本里填入真实 token
      3. 运行副本：  .\run-m1a-probe.local.ps1

    ⚠️ 为什么分两个文件：.local.ps1 含真实 token，已加入 .gitignore，**永不入库**。
       本模板只放占位符，可以安全提交。
#>

$ErrorActionPreference = 'Stop'

# ───────────────────────── 配置区（按需修改）─────────────────────────
$NapCatToken = '<在此填入 NapCat 正向 WS 的 access_token>'
$WsUrl       = 'ws://127.0.0.1:17011'
$DbPath      = './data/probe.db'

# 留空 = 黑名单 + 空 = 全放行（仅用于验证，会接收所有聊天）
# 填条目 = 白名单模式，只接收这些会话，例：@('qq:g123456', 'qq:p987654')
$Whitelist = @()
# ─────────────────────────────────────────────────────────────────────

if ($NapCatToken -like '<*') {
    throw '请先复制本文件为 run-m1a-probe.local.ps1 并填入真实 token（模板文件不应直接运行）'
}

# token 只进进程环境变量，不写文件、不打印
$env:NAPCAT_TOKEN = $NapCatToken

$probeDir = Join-Path $PSScriptRoot '..\temp\m1a-probe'
$project  = Join-Path $probeDir 'm1a-probe.csproj'
$exe      = Join-Path $probeDir 'bin\Release\net10.0\m1a-probe.exe'

if (-not (Test-Path $exe)) {
    Write-Host '未找到已构建的探针，正在构建…' -ForegroundColor Yellow
    dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
}

Write-Host "启动探针：$WsUrl" -ForegroundColor Cyan
Write-Host '（Ctrl+C 停止）' -ForegroundColor DarkGray

Push-Location $probeDir
try {
    & $exe $WsUrl $DbPath @Whitelist
}
finally {
    Pop-Location
    Remove-Item Env:\NAPCAT_TOKEN -ErrorAction SilentlyContinue
}
