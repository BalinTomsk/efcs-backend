<#
.SYNOPSIS
  Downloads BAAI/bge-small-en-v1.5 (ONNX export + vocab) into models/bge-small-en-v1.5/.

.DESCRIPTION
  The files are ~130 MB and stay out of git (models/ is ignored). The Docker build copies them into the
  image. Pass -Revision to pin a commit of the Hugging Face repository; the SHA-256 of each file is printed
  so it can be recorded in docs/specification.md, and -ExpectModelSha256 makes a mismatch fatal.

.EXAMPLE
  ./scripts/fetch-model.ps1
  ./scripts/fetch-model.ps1 -Revision <commit> -ExpectModelSha256 <sha256>
#>
param(
    [string]$Revision = "main",
    [string]$ExpectModelSha256 = ""
)

$ErrorActionPreference = "Stop"
$target = Join-Path $PSScriptRoot "..\models\bge-small-en-v1.5"
New-Item -ItemType Directory -Force $target | Out-Null

$base = "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/$Revision"
$files = @{ "model.onnx" = "onnx/model.onnx"; "vocab.txt" = "vocab.txt" }

foreach ($name in $files.Keys) {
    $out = Join-Path $target $name
    Write-Host "Downloading $($files[$name]) -> $out"
    Invoke-WebRequest -Uri "$base/$($files[$name])" -OutFile $out
    $sha = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("  {0}  {1:N1} MB  sha256={2}" -f $name, ((Get-Item $out).Length / 1MB), $sha)
    if ($name -eq "model.onnx" -and $ExpectModelSha256 -and $sha -ne $ExpectModelSha256.ToLowerInvariant()) {
        Remove-Item $out
        throw "model.onnx SHA-256 mismatch (expected $ExpectModelSha256, got $sha)"
    }
}
