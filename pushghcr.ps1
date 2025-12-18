param(
  [string]$Owner = "zeastwin",
  [string]$ImageName = "ew-link",
  [string]$Tag = ""
)

Set-Location $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Tag)) {
  $Tag = (git rev-parse --short HEAD)
}

$Image = "ghcr.io/$Owner/$ImageName"

Write-Host "Publishing ${Image}:${Tag}"

docker buildx build --platform linux/amd64 `
  -f .\Dockerfile `
  -t "${Image}:${Tag}" `
  -t "${Image}:latest" `
  --push `
  .
