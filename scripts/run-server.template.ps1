<#
    ZipperAIQQ Server 启动脚本（模板，不含真实凭证）

    用法：复制为 run-server.local.ps1（同目录，已被 .gitignore 排除）→ 填入真实值 → 运行。
#>
$ErrorActionPreference = 'Stop'

# ─── 必填 ───
$env:ZIPPER_NAPCAT_WS    = 'ws://<napcat-host>:17011'
$env:ZIPPER_NAPCAT_TOKEN = '<napcat access_token>'
$env:ZIPPER_LLM_BASEURL  = '<llm base url>'
$env:ZIPPER_LLM_MODEL    = '<model id>'
$env:ZIPPER_LLM_APIKEY   = '<llm api key>'

# ─── 可选 ───
# Responses（DeepSeek）| ChatCompletions（OpenRouter 等）；缺省 Responses
$env:ZIPPER_LLM_APIKIND = 'Responses'
$env:ZIPPER_DB = './data/zipperaiqq.db'
# 留空 = 全放行（仅用于首次验证）；生产请填 qq:pxxxx,qq:gxxxx
$env:ZIPPER_WHITELIST = ''

if ($env:ZIPPER_LLM_APIKEY -like '<*') {
    throw '请复制为 run-server.local.ps1 并填入真实凭证'
}

Push-Location $PSScriptRoot\..
try {
    dotnet run --project ZipperAIQQ.Server
}
finally {
    Pop-Location
    'ZIPPER_NAPCAT_WS','ZIPPER_NAPCAT_TOKEN','ZIPPER_LLM_BASEURL','ZIPPER_LLM_MODEL','ZIPPER_LLM_APIKEY' |
        ForEach-Object { Remove-Item "Env:\$_" -ErrorAction SilentlyContinue }
}