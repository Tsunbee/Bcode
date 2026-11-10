<#
  Phát hành bản Bcode lên thư mục phát hành (thư mục mà "Cập nhật Bcode" của các máy đọc — Services/UpdateCheckService.cs).

  Cách dùng (từ gốc repo, sau khi build Release):
      powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Dest "\\máy\thư mục phát hành"
      (thêm -Source <thư mục build> nếu không phải src\Bcode.App\bin\Release\net8.0-windows)

  Thứ tự an toàn cho máy đang kiểm tra cùng lúc:
    1. xoá bcode-version.json ở đích  → các máy thấy "chưa có bản phát hành", không tải bản đang chép dở
    2. robocopy mọi file (KHÔNG /MIR — thư mục đích có thể chứa thứ khác; file thừa ở đích vô hại vì máy cập nhật đi theo manifest)
    3. chép bcode-manifest.json, bcode-changelog.txt, rồi bcode-version.json SAU CÙNG
  Bỏ qua *.WebView2\ (hồ sơ trình duyệt sinh ra khi chạy Bcode từ thư mục build).
#>
param(
    [Parameter(Mandatory = $true)][string]$Dest,
    [string]$Source = (Join-Path $PSScriptRoot '..\src\Bcode.App\bin\Release\net8.0-windows')
)
$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
$Dest = $Dest.TrimEnd('\')

foreach ($f in 'Bcode.exe', 'bcode-version.json', 'bcode-manifest.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $f))) {
        throw "Thiếu $f trong $Source — build Release trước (bcode-manifest.json chỉ sinh ở Release, hoặc build thêm /p:BcodeManifest=true)."
    }
}
$stamp = Get-Content -LiteralPath (Join-Path $Source 'bcode-version.json') -Raw | ConvertFrom-Json
Write-Host ("Phát hành v{0} build {1} ({2}, commit {3})" -f $stamp.version, $stamp.build, $stamp.built, $stamp.commit)
Write-Host "  từ  $Source"
Write-Host "  tới $Dest"

if (-not (Test-Path -LiteralPath $Dest)) { New-Item -ItemType Directory -Path $Dest -Force | Out-Null }
$destStamp = Join-Path $Dest 'bcode-version.json'
if (Test-Path -LiteralPath $destStamp) { Remove-Item -LiteralPath $destStamp -Force }

robocopy $Source $Dest /E /XD '*.WebView2' /XF bcode-version.json bcode-manifest.json bcode-changelog.txt /R:3 /W:2 /NP /NFL /NDL /NJH
if ($LASTEXITCODE -ge 8) { throw "robocopy lỗi mã $LASTEXITCODE — bản phát hành chưa hoàn tất (các máy chưa thấy bản mới vì chưa có bcode-version.json)." }

foreach ($f in 'bcode-manifest.json', 'bcode-changelog.txt', 'bcode-version.json') {
    $p = Join-Path $Source $f
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $Dest $f) -Force }
}
Write-Host "Xong. Các máy sẽ thấy bản mới ở lần kiểm tra kế tiếp (mở Bcode, hoặc mỗi 4 giờ)." -ForegroundColor Green
