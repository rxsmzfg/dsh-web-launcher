# 把本仓库源码推送到 GitHub —— 走 API,适用于 github.com 被墙/超时的网络环境。
#
# 推送策略(自动降级):
#   ① GraphQL createCommitOnBranch —— 一次调用产生"一个提交",历史最干净(推荐,细粒度令牌可用)
#   ② Contents API 逐文件 PUT     —— 若 ① 不可用则降级,每个文件一个提交
#
# 用法:
#   $env:GH_TOKEN = "github_pat_xxx"          # 细粒度令牌:仅本仓库 Contents: Read and write
#   pwsh -File tools/push-to-github.ps1
#   pwsh -File tools/push-to-github.ps1 -DryRun              # 只列出将要推送的文件
#   pwsh -File tools/push-to-github.ps1 -Message "更新说明"  # 自定义提交信息
#   pwsh -File tools/push-to-github.ps1 -Mode contents       # 强制用逐文件方式
#
# 说明:令牌只从参数或环境变量读取,不写入任何文件、也不打印。
param(
    [string]$Token = $null,
    [string]$Owner = 'rxsmzfg',
    [string]$Repo  = 'dsh-web-launcher',
    [string]$Branch = 'main',
    [string]$Message = $null,
    [ValidateSet('auto', 'graphql', 'contents')][string]$Mode = 'auto',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if (-not $Token) { $Token = $env:GH_TOKEN }
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$exclude = @('\dist\', '\.git\', '\.vs\', '\.idea\')
$files = Get-ChildItem $root -Recurse -File | Where-Object {
    $p = $_.FullName
    -not ($exclude | Where-Object { $p -like "*$_*" }) -and
    $_.Extension -ne '.zip' -and $_.Name -ne 'DSH-Web.ini' -and $_.Name -ne 'install.txt'
} | Sort-Object FullName

if ($DryRun) {
    Write-Host "将推送以下 $($files.Count) 个文件到 $Owner/$Repo@$Branch :"
    foreach ($f in $files) { Write-Host ('  ' + $f.FullName.Substring($root.Length + 1).Replace('\', '/')) }
    return
}
if (-not $Token) { throw "缺少令牌。请设置环境变量 GH_TOKEN,或使用 -Token 参数。" }

$headers = @{
    'User-Agent'    = 'dsh-web-launcher-push'
    'Accept'        = 'application/vnd.github+json'
    'Authorization' = 'Bearer ' + $Token
}
$apiBase   = "https://api.github.com/repos/$Owner/$Repo"
$graphql   = 'https://api.github.com/graphql'
$commitMsg = if ($Message) { $Message } else { "同步 DSH 网页端启动器源码($($files.Count) 个文件)" }

function Rel($f) { return $f.FullName.Substring($root.Length + 1).Replace('\', '/') }
function B64($f) { return [Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName)) }

Write-Host "目标:$Owner/$Repo  分支:$Branch  文件:$($files.Count)"

# ---------------------------------------------------------------- 方式① GraphQL 单提交
if ($Mode -eq 'auto' -or $Mode -eq 'graphql') {
    try {
        $q = @'
query($owner:String!,$name:String!,$branch:String!){
  repository(owner:$owner,name:$name){
    ref(qualifiedName:$branch){ target{ ... on Commit { oid } } }
  }
}
'@
        $qres = Invoke-RestMethod -Method Post -Uri $graphql -Headers $headers -ContentType 'application/json' `
                 -Body (@{ query = $q; variables = @{ owner = $Owner; name = $Repo; branch = $Branch } } | ConvertTo-Json -Depth 6) -TimeoutSec 30
        $headOid = $qres.data.repository.ref.target.oid

        $additions = @()
        foreach ($f in $files) { $additions += @{ path = (Rel $f); contents = (B64 $f) } }

        $m = @'
mutation($input:CreateCommitOnBranchInput!){
  createCommitOnBranch(input:$input){ commit { oid url } }
}
'@
        $vars = @{
            input = @{
                branch         = @{ repositoryNameWithOwner = "$Owner/$Repo"; branchName = $Branch }
                message        = @{ headline = $commitMsg }
                fileChanges    = @{ additions = $additions }
                expectedHeadOid = $headOid
            }
        }
        $res = Invoke-RestMethod -Method Post -Uri $graphql -Headers $headers -ContentType 'application/json' `
                -Body (@{ query = $m; variables = $vars } | ConvertTo-Json -Depth 12 -Compress) -TimeoutSec 120
        if ($res.errors) { throw ($res.errors | ConvertTo-Json -Compress) }
        $oid = $res.data.createCommitOnBranch.commit.oid
        Write-Host ""
        Write-Host "推送完成 ✅(单次提交)"
        Write-Host "  提交: $($oid.Substring(0,8))  $commitMsg"
        Write-Host "  仓库: https://github.com/$Owner/$Repo"
        return
    } catch {
        if ($Mode -eq 'graphql') { throw }
        Write-Host "  GraphQL 方式不可用($($_.Exception.Message)),降级为逐文件推送…"
    }
}

# ---------------------------------------------------------------- 方式② Contents API 逐文件
$ok = 0; $fail = 0
foreach ($f in $files) {
    $rel = Rel $f
    $url = "$apiBase/contents/$rel"
    $sha = $null
    try { $sha = (Invoke-RestMethod -Uri "$url`?ref=$Branch" -Headers $headers -TimeoutSec 20).sha } catch { }
    $body = @{ message = $commitMsg + ' — ' + $rel; content = (B64 $f); branch = $Branch }
    if ($sha) { $body['sha'] = $sha }
    try {
        $null = Invoke-RestMethod -Method Put -Uri $url -Headers $headers -ContentType 'application/json; charset=utf-8' `
                 -Body ($body | ConvertTo-Json -Compress) -TimeoutSec 60
        Write-Host ("  {0} {1}" -f $(if ($sha) { '更新' } else { '新增' }), $rel)
        $ok++
    } catch {
        $d = $_.Exception.Message
        try { if ($_.ErrorDetails.Message) { $d = $_.ErrorDetails.Message } } catch { }
        Write-Host ("  失败 {0} → {1}" -f $rel, $d)
        $fail++
    }
}
Write-Host ""
Write-Host "完成:成功 $ok,失败 $fail"
Write-Host "仓库: https://github.com/$Owner/$Repo"
if ($fail -gt 0) { exit 1 }
