# 生成本地向量模型（bge-small-zh-v1.5 -> ONNX 量化版）
#
# 模型来源：BAAI/bge-small-zh-v1.5，许可 MIT（可商用、可再分发）
# 为什么不在仓库里带：24MB 二进制会永久留在 git 历史；项目约定模型文件不入库。
#
# 用法： .\scripts\build-embedding-model.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'temp/bge-small-zh-v1.5'

Write-Host "=== 检查 Python ===" -ForegroundColor Cyan
$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { throw "没找到 python。先装 Python 3.10+（或用 py -3）。" }
python --version

Write-Host "`n=== 检查已有产物 ===" -ForegroundColor Cyan
$target = Join-Path $outDir 'model_quantized.onnx'
if (Test-Path $target) {
    $mb = [math]::Round((Get-Item $target).Length / 1MB, 1)
    Write-Host "  已存在：$target（$mb MB）" -ForegroundColor Green
    $ans = Read-Host "  重新生成？(y/N)"
    if ($ans -ne 'y') { Write-Host '  跳过。'; exit 0 }
}

Write-Host "`n=== 安装依赖（首次会比较大）===" -ForegroundColor Cyan
python -m pip install --quiet --upgrade "optimum[onnxruntime]" transformers
if ($LASTEXITCODE -ne 0) { throw "pip install 失败" }

Write-Host "`n=== 导出 + INT8 量化 ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
python -m optimum.commands.optimum_cli export onnx `
    --model BAAI/bge-small-zh-v1.5 `
    --task feature-extraction `
    --quantize `
    $outDir
if ($LASTEXITCODE -ne 0) { throw "optimum 导出失败" }

Write-Host "`n=== 结果 ===" -ForegroundColor Cyan
Get-ChildItem $outDir -File | Select-Object Name, @{N='MB'; E={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize

if (Test-Path $target) {
    Write-Host "OK：$target 已就绪" -ForegroundColor Green
} else {
    Write-Host "!! 没看到 model_quantized.onnx —— 检查 optimum 的输出目录与文件名" -ForegroundColor Yellow
}
