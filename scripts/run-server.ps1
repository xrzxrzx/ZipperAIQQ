# 启动服务端；退出码 0 视为「请求重启」，自动重新拉起。
# 用法：先把 scripts/run-server.local.ps1 里的环境变量准备好，再运行本脚本。
# 注意：本脚本不含任何凭证。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'ZipperAIQQ.Server\bin\Release\net10.0\ZipperAIQQ.Server.dll'

if (-not (Test-Path $dll)) {
    Write-Error "找不到服务端构建产物：$dll（先 dotnet build -c Release）"
    exit 1
}

$local = Join-Path $PSScriptRoot 'run-server.local.ps1'
if (Test-Path $local) {
    # 只加载环境变量，不执行脚本里的 dotnet run
    $raw = Get-Content $local -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($raw, '\$env:([A-Z0-9_]+)\s*=\s*''([^'']*)''')) {
        Set-Item -Path ("Env:" + $m.Groups[1].Value) -Value $m.Groups[2].Value
    }
}

Set-Location $root
while ($true) {
    & dotnet $dll
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "服务端退出码 $code（非重启），停止守护。"
        exit $code
    }

    Write-Host "服务端已按要求停止（退出码 0），2 秒后重新拉起…"
    Start-Sleep -Seconds 2
}
