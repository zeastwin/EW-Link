param(
  # GHCR 镜像信息
  [string]$Owner = "zeastwin",
  [string]$ImageName = "ew-link",
  [string]$Tag = "",

  # 目标机 SSH
  [string]$RemoteUser = "Administrator",
  [string]$RemoteHost = "172.16.211.183",
  [string]$RemotePath = "E:\EW-Link",

  # SSH 私钥路径
  [string]$KeyPath = "$env:USERPROFILE\.ssh\ewlink_deploy",

  # 新增：默认强制同步（更保险）
  [switch]$NoSyncConfig,

  # 兼容旧参数（即使传了也能用）
  [switch]$ForceSyncConfig,
  [switch]$SyncConfig,

  # 如需临时 token 拉取（一般不需要；你已在目标机落盘 E:\EW-Link\.docker\config.json）
  [switch]$UseTokenForPull
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 默认行为：强制同步（除非显式 -NoSyncConfig）
if (-not $NoSyncConfig) { $ForceSyncConfig = $true }
if ($SyncConfig) { $ForceSyncConfig = $true } # 兼容旧参数

if ([string]::IsNullOrWhiteSpace($Tag)) {
  $Tag = (git rev-parse --short HEAD)
}

# ssh/scp 通用参数：指定 key + 禁止交互 + 首次自动接受 host key
$sshTarget = "{0}@{1}" -f $RemoteUser, $RemoteHost
$sshArgs = @(
  "-o", "BatchMode=yes",
  "-o", "StrictHostKeyChecking=accept-new",
  "-i", $KeyPath
)

function Invoke-RemotePwshText {
  param([Parameter(Mandatory=$true)][string]$ScriptText)

  $bytes = [Text.Encoding]::Unicode.GetBytes($ScriptText)
  $encoded = [Convert]::ToBase64String($bytes)

  $out = & ssh @($sshArgs) $sshTarget powershell -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand $encoded
  return ($out -join "`n")
}

function Convert-ToSftpPath {
  param([Parameter(Mandatory=$true)][string]$WindowsPath)
  # E:\EW-Link  ->  /E:/EW-Link
  $p = ($WindowsPath -replace "\\", "/")
  if ($p -match '^[A-Za-z]:/') { return "/" + $p }
  if ($p -match '^[A-Za-z]:$') { return "/" + $p + "/" }
  if ($p.StartsWith("/")) { return $p }
  return $p
}

Write-Host "=== [1/3] Build & Push to GHCR ==="
.\pushghcr.ps1 -Owner $Owner -ImageName $ImageName -Tag $Tag

Write-Host "=== [2/3] Sync config files to remote (default: FORCE) ==="

$filesToSync = @(
  @{ Local = ".\docker-compose.yml";      Name = "docker-compose.yml" },
  @{ Local = ".\docker-compose.prod.yml"; Name = "docker-compose.prod.yml" },
  @{ Local = ".\nginx.conf";              Name = "nginx.conf" }
)

if (-not $NoSyncConfig) {
  # 1) 确保目标机目录存在（否则 scp 不能写入）
  $ensureDir = @"
`$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force '$RemotePath' | Out-Null
"@
  [void](Invoke-RemotePwshText -ScriptText $ensureDir)

  # 2) scp 目的路径：不带引号，且用 /E:/... 形式（SFTP 友好）
  $remoteSftpBase = Convert-ToSftpPath -WindowsPath $RemotePath

  foreach ($f in $filesToSync) {
    if (!(Test-Path $f.Local)) {
      throw "Local file missing: $($f.Local)"
    }

    $name = $f.Name
    Write-Host ("  SYNC  {0}" -f $name)

    $dest = "{0}@{1}:{2}/{3}" -f $RemoteUser, $RemoteHost, $remoteSftpBase.TrimEnd('/'), $name
    scp @($sshArgs) $f.Local $dest
  }
} else {
  Write-Host "  SKIP  Sync disabled by -NoSyncConfig"
}

Write-Host "=== [3/3] Remote pull & up via SSH ==="

$remoteDeploy = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'

New-Item -ItemType Directory -Force '$RemotePath' | Out-Null
Set-Location '$RemotePath'

# 强制 docker 使用 E:\EW-Link\.docker\config.json（绕开 Windows Credential Manager）
`$cfg = Join-Path '$RemotePath' '.docker'
if (Test-Path (Join-Path `$cfg 'config.json')) { `$env:DOCKER_CONFIG = `$cfg }

# 写入/更新 .env：给 prod compose 用
Set-Content -Path '.\.env' -Value "IMAGE_TAG=$Tag`n" -Encoding Ascii;

"@

if ($UseTokenForPull) {
  $sec = Read-Host "Enter GHCR PAT for remote pull (read:packages)" -AsSecureString
  $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
  $pat = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)

  $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Owner + ":" + $pat))
  Remove-Variable pat -ErrorAction SilentlyContinue

  $remoteDeploy += @"
`$tmp = Join-Path `$env:TEMP ("docker_cfg_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force `$tmp | Out-Null
`$env:DOCKER_CONFIG = `$tmp

@'
{ "auths": { "ghcr.io": { "auth": "$auth" } } }
'@ | Set-Content -Encoding Ascii (Join-Path `$tmp 'config.json')

docker compose -f docker-compose.yml -f docker-compose.prod.yml pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --force-recreate
docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"

Remove-Item -Recurse -Force `$tmp
"@
} else {
  $remoteDeploy += @"
docker compose -f docker-compose.yml -f docker-compose.prod.yml pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --force-recreate
docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
"@
}

$deployOut = Invoke-RemotePwshText -ScriptText $remoteDeploy
Write-Host $deployOut

Write-Host "Done. Deployed tag: $Tag"
