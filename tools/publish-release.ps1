<#
  Phát hành bản Bcode / BcodeViewer / Screen Designer lên thư mục phát hành (thư mục mà "Cập nhật ..." của các máy đọc — Shared/AppUpdate.cs).
  Mỗi app một cơ chế cập nhật riêng, thư mục phát hành RIÊNG (không trùng nhau).

  Cách dùng (từ gốc repo, sau khi build Release):
      powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 -Dest "\\máy\thư mục phát hành"
      -App Viewer | Designer   phát hành BcodeViewer / Screen Designer (mặc định: Bcode)
                               Bcode build kèm ScreenDesigner\ (bản khởi đầu cho máy mới) nhưng manifest của Bcode bỏ thư mục đó —
                               Designer cập nhật bằng -App Designer.
      -Source <thư mục build>  nếu không phải src\<app>\bin\Release\net8.0-windows
      -Full                    chép lại toàn bộ (khi nghi file trên server bị sửa tay / thiếu)

  Nhanh: chỉ đọc 1 file bcode-manifest.json đang có trên server, so SHA-256 với bản mới → chỉ chép file KHÁC (thường vài chục file,
  không đụng 3.000+ file mẫu Templates\). Server chưa có manifest (lần đầu) hoặc -Full → robocopy toàn bộ, đa luồng (/MT:16).

  Thứ tự an toàn cho máy đang kiểm tra cùng lúc:
    1. xoá bcode-version.json ở đích  → các máy thấy "chưa có bản phát hành", không tải bản đang chép dở
    2. chép file (KHÔNG /MIR — thư mục đích có thể chứa thứ khác; file thừa ở đích vô hại vì máy cập nhật đi theo manifest)
    3. chép bcode-manifest.json, bcode-changelog.txt, rồi bcode-version.json SAU CÙNG
  Bỏ qua *.WebView2\ (hồ sơ trình duyệt sinh ra khi chạy Bcode từ thư mục build).
#>
param(
    [Parameter(Mandatory = $true)][string]$Dest,
    [ValidateSet('Bcode', 'Viewer', 'Designer')][string]$App = 'Bcode',
    [string]$Source,
    [switch]$Full
)
$ErrorActionPreference = 'Stop'
$apps = @{ Bcode = 'Bcode.App', 'Bcode.exe'; Viewer = 'BcodeViewer.App', 'BcodeViewer.exe'; Designer = 'Bcode.ScreenDesigner', 'BcodeScreenDesigner.exe' }
$proj, $exe = $apps[$App]
# Windows PowerShell 5.1 chạy bằng -File: $PSScriptRoot có thể còn rỗng → lấy thư mục script từ $MyInvocation.
if (-not $Source) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Source = Join-Path $here "..\src\$proj\bin\Release\net8.0-windows"
}
$Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $Source $exe))) { throw "Không thấy $exe trong $Source — sai -App hoặc -Source?" }
# Chặn phát hành nhầm app vào thư mục đang chứa app khác (mỗi app phải có thư mục phát hành riêng)
foreach ($k in $apps.Keys) {
    $other = $apps[$k][1]
    if ($k -ne $App -and (Test-Path -LiteralPath (Join-Path $Dest $other))) { throw "$Dest đang chứa $other — mỗi app cần thư mục phát hành riêng." }
}
$Dest = $Dest.TrimEnd('\')
$meta = 'bcode-version.json', 'bcode-manifest.json', 'bcode-changelog.txt'

foreach ($f in $exe, 'bcode-version.json', 'bcode-manifest.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $f))) {
        throw "Thiếu $f trong $Source — build Release trước (bcode-manifest.json chỉ sinh ở Release, hoặc build thêm /p:BcodeManifest=true)."
    }
}
function Read-Manifest($path) {
    $map = @{}
    $m = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($e in $m.files) { if ($e.p -and $e.h) { $map[$e.p.Replace('/', '\')] = $e.h.ToLowerInvariant() } }
    return $map
}

$stamp = [IO.File]::ReadAllText((Join-Path $Source 'bcode-version.json')) | ConvertFrom-Json
Write-Host ("Phát hành $App v{0} build {1} ({2}, commit {3})" -f $stamp.version, $stamp.build, $stamp.built, $stamp.commit)
Write-Host "  từ  $Source"
Write-Host "  tới $Dest"
$sw = [Diagnostics.Stopwatch]::StartNew()

if (-not (Test-Path -LiteralPath $Dest)) { New-Item -ItemType Directory -Path $Dest -Force | Out-Null }
$newMan = Read-Manifest (Join-Path $Source 'bcode-manifest.json')
$oldManPath = Join-Path $Dest 'bcode-manifest.json'
$oldMan = $null
if (-not $Full -and (Test-Path -LiteralPath $oldManPath)) {
    try { $oldMan = Read-Manifest $oldManPath } catch { Write-Host "  manifest trên server hỏng — chép toàn bộ." -ForegroundColor Yellow }
}

$destStamp = Join-Path $Dest 'bcode-version.json'
if (Test-Path -LiteralPath $destStamp) {
    # Xoá dấu phiên bản trước khi chép để máy khác không thấy "bản mới" giữa chừng. Share chỉ cho GHI mà không cho XOÁ (file do người khác tạo...)
    # thì giữ dấu cũ và ghi đè ở cuối — máy cập nhật vẫn kiểm SHA-256 từng file + đọc lại dấu sau khi tải nên không cài phải bản chép dở.
    $removed = $false
    try { Remove-Item -LiteralPath $destStamp -Force -ErrorAction Stop; $removed = $true } catch { }
    if (-not $removed) {
        try { (Get-Item -LiteralPath $destStamp -Force).Attributes = 'Normal'; Remove-Item -LiteralPath $destStamp -Force -ErrorAction Stop; $removed = $true } catch { }
    }
    if (-not $removed) {
        $canWrite = $false
        try { $fs = [IO.File]::Open($destStamp, 'Open', 'Write', 'ReadWrite'); $fs.Dispose(); $canWrite = $true } catch { }
        if (-not $canWrite) {
            throw "Không xoá cũng không ghi được $destStamp (máy này / tài khoản $env:USERNAME không có quyền). Chưa chép gì lên server. " +
                  "Nhờ người quản lý share cấp quyền Sửa (Modify) cho thư mục $Dest, hoặc nhờ người tạo file xoá nó."
        }
        Write-Host "  Không xoá được bcode-version.json cũ (share không cho xoá) — giữ lại, sẽ ghi đè ở cuối." -ForegroundColor Yellow
    }
}

if ($null -eq $oldMan) {
    Write-Host ("  chép toàn bộ ({0} file)..." -f $newMan.Count)
    robocopy $Source $Dest /E /MT:16 /XD '*.WebView2' /XF $meta /R:3 /W:2 /NP /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy lỗi mã $LASTEXITCODE — bản phát hành chưa hoàn tất (các máy chưa thấy bản mới vì chưa có bcode-version.json)." }
    $copied = $newMan.Count; $bytes = 0
} else {
    $changed = @($newMan.Keys | Where-Object { -not $oldMan.ContainsKey($_) -or $oldMan[$_] -ne $newMan[$_] } | Sort-Object)
    $bytes = 0
    foreach ($rel in $changed) { $bytes += (Get-Item -LiteralPath (Join-Path $Source $rel)).Length }
    Write-Host ("  {0} / {1} file khác bản trên server ({2:N1} MB) — chỉ chép các file này." -f $changed.Count, $newMan.Count, ($bytes / 1MB))
    # gom theo thư mục, mỗi lần robocopy tối đa 40 file (giới hạn độ dài dòng lệnh); /IS = chép cả khi cỡ + giờ trùng
    $groups = $changed | Group-Object { Split-Path -Parent $_ }
    foreach ($g in $groups) {
        $sub = $g.Name
        $names = @($g.Group | ForEach-Object { Split-Path -Leaf $_ })
        for ($i = 0; $i -lt $names.Count; $i += 40) {
            $batch = $names[$i..([Math]::Min($i + 39, $names.Count - 1))]
            $from = if ($sub) { Join-Path $Source $sub } else { $Source }
            $to = if ($sub) { Join-Path $Dest $sub } else { $Dest }
            robocopy $from $to $batch /IS /IT /MT:8 /R:3 /W:2 /NP /NFL /NDL /NJH /NJS | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy lỗi mã $LASTEXITCODE ở $sub — bản phát hành chưa hoàn tất (các máy chưa thấy bản mới)." }
        }
        if ($changed.Count -le 30) { $g.Group | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray } }
    }
    $copied = $changed.Count
}

foreach ($f in 'bcode-manifest.json', 'bcode-changelog.txt', 'bcode-version.json') {
    $p = Join-Path $Source $f
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $Dest $f) -Force }
}
Write-Host ("Xong: chép {0} file trong {1:N1} giây. Các máy sẽ thấy bản mới ở lần kiểm tra kế tiếp (mở $($exe -replace '\.exe$',''), hoặc mỗi 4 giờ)." -f $copied, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
