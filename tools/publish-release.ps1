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
    0. kiểm SHA-256 MỌI file trong thư mục build với bcode-manifest.json của chính nó (~6 giây / 3.700 file) — lệch thì DỪNG, chưa đụng server
       (v2.3-beta build 475: BcodeNote.App build sau Bcode.App chép đè BcodeNote.dll sau khi manifest đã sinh → máy cập nhật báo "sai mã kiểm tra")
    1. xoá bcode-version.json ở đích  → các máy thấy "chưa có bản phát hành", không tải bản đang chép dở
    2. chép file (KHÔNG /MIR — thư mục đích có thể chứa thứ khác; file thừa ở đích vô hại vì máy cập nhật đi theo manifest).
       Chép cả file trên server thiếu / khác cỡ với bản build dù manifest cũ ghi là giống (bản trước chép dở, sửa tay...).
       File không ghi đè được (máy khác đang mở Bcode chạy thẳng từ thư mục phát hành, thiếu quyền...) → robocopy thử lại rồi DỪNG, in tên file + lỗi.
    3. kiểm lại SHA-256 các file vừa chép NGAY TRÊN SERVER (-Full: mọi file) — lệch thì DỪNG, không chép dấu phiên bản
    4. chép bcode-manifest.json, bcode-changelog.txt, rồi bcode-version.json SAU CÙNG
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
$Source = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\')   # ProviderPath: không dính tiền tố "FileSystem::" khi là đường dẫn UNC
if (-not (Test-Path -LiteralPath (Join-Path $Source $exe))) { throw "Không thấy $exe trong $Source — sai -App hoặc -Source?" }
# Chặn phát hành nhầm app vào thư mục đang chứa app khác (mỗi app phải có thư mục phát hành riêng)
foreach ($k in $apps.Keys) {
    $other = $apps[$k][1]
    if ($k -ne $App -and (Test-Path -LiteralPath (Join-Path $Dest $other))) { throw "$Dest đang chứa $other — mỗi app cần thư mục phát hành riêng." }
}
$Dest = [IO.Path]::GetFullPath($Dest).TrimEnd('\')
$meta = 'bcode-version.json', 'bcode-manifest.json', 'bcode-changelog.txt'
$stampRemoved = $false   # đã xoá bcode-version.json trên server (máy khác thấy "chưa có bản") hay còn dấu bản cũ — để báo đúng khi dừng giữa chừng

function Get-Rel([string]$root, [string]$full) { return $full.Substring($root.Length).TrimStart('\') }
# Như Shared/AppUpdate.cs: bỏ hồ sơ WebView2 (*.WebView2\) và thư mục con cấp 1 có bcode-version.json riêng (app khác tự cập nhật — Bcode: ScreenDesigner\)
function Test-Excluded([string]$rel) { foreach ($seg in $rel.Split('\')) { if ($seg -like '*.WebView2') { return $true } }; return $false }
$nested = @{}
Get-ChildItem -LiteralPath $Source -Directory -Force | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bcode-version.json') } | ForEach-Object { $nested[$_.Name] = $true }
function Test-Nested([string]$rel) { $i = $rel.IndexOf('\'); return ($i -gt 0 -and $nested.ContainsKey($rel.Substring(0, $i))) }
$sha256 = [Security.Cryptography.SHA256]::Create()   # .NET thẳng: Get-FileHash từng file trên PS 5.1 chậm gấp nhiều lần với 3.700 file
function Get-Sha([string]$path) {
    $s = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { return [BitConverter]::ToString($sha256.ComputeHash($s)).Replace('-', '').ToLowerInvariant() } finally { $s.Dispose() }
}
# Liệt kê 1 lần cả cây (rel → FileInfo) thay vì Test-Path / Get-Item từng file (chậm, nhất là trên share)
function Get-FileMap([string]$root) {
    $map = @{}
    Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { $map[(Get-Rel $root $_.FullName)] = $_ }
    return $map
}
function Get-StopNote {
    if ($stampRemoved) { return 'Bản phát hành CHƯA hoàn tất: trên server hiện không có bcode-version.json (đã gỡ trước khi chép) nên các máy tạm thấy "chưa có bản phát hành" — sửa lỗi rồi chạy lại script.' }
    return 'Bản phát hành CHƯA hoàn tất: dấu phiên bản trên server vẫn là bản cũ (chưa chép bcode-version.json mới) nên các máy chưa thấy bản mới — sửa lỗi rồi chạy lại script.'
}
# robocopy giữ lại dòng lỗi (ERROR nn ... + câu giải thích dòng sau) để báo đúng file nào không ghi đè được, thay vì chỉ "mã 8"
function Invoke-Robocopy([string]$from, [string]$to, [string[]]$opts, [string]$where) {
    $out = @(& robocopy $from $to @opts)
    $code = $LASTEXITCODE
    if ($code -lt 8) { return }
    $lines = New-Object System.Collections.Generic.List[string]
    $seen = New-Object System.Collections.Generic.HashSet[string]   # robocopy in lại cùng lỗi ở mỗi lần thử (/R) — chỉ giữ 1
    for ($i = 0; $i -lt $out.Count -and $lines.Count -lt 24; $i++) {
        if ($out[$i] -match 'ERROR \d+.*$') {
            $msg = $Matches[0].Trim()   # bỏ ngày giờ đầu dòng
            if (-not $seen.Add($msg)) { continue }
            $lines.Add('    ' + $msg)
            if ($i + 1 -lt $out.Count -and $out[$i + 1].Trim() -and $out[$i + 1] -notmatch 'ERROR \d+|Waiting|Retrying') { $lines.Add('      ' + $out[$i + 1].Trim()) }
        }
    }
    $detail = if ($lines.Count) { "`n" + ($lines -join "`n") } else { '' }
    throw ("robocopy lỗi mã $code ở $where — có file KHÔNG ghi đè được lên server (thường do máy khác đang mở Bcode chạy thẳng từ thư mục phát hành, " +
           "file đang bị khoá, hoặc tài khoản $env:USERNAME thiếu quyền ghi):$detail`n" + (Get-StopNote))
}

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

$newMan = Read-Manifest (Join-Path $Source 'bcode-manifest.json')

# 0) manifest phải khớp TỪNG file của bản build (chưa đụng tới server). Manifest sinh lúc Bcode.App build xong; project khác chép đè file vào thư mục
#    build sau đó (BcodeNote.App → BcodeNote.dll) thì manifest mang hash cũ → mọi máy cập nhật báo "sai mã kiểm tra" mà Thử lại cũng không hết.
Write-Host ("  kiểm SHA-256 {0} file của bản build với bcode-manifest.json..." -f $newMan.Count)
$srcMap = Get-FileMap $Source
$srcLen = @{}
$bad = New-Object System.Collections.Generic.List[string]
foreach ($rel in @($newMan.Keys)) {
    $fi = $srcMap[$rel]
    if ($null -eq $fi) { $bad.Add("thiếu file      $rel"); continue }
    $srcLen[$rel] = $fi.Length
    $h = Get-Sha $fi.FullName
    if ($h -ne $newMan[$rel]) { $bad.Add("khác hash       $rel   manifest $($newMan[$rel]) ≠ file $h") }
}
foreach ($rel in @($srcMap.Keys)) {
    if ($meta -notcontains $rel -and -not (Test-Excluded $rel) -and -not (Test-Nested $rel) -and -not $newMan.ContainsKey($rel)) { $bad.Add("ngoài manifest  $rel") }
}
if ($bad.Count) {
    $shown = ($bad | Select-Object -First 20 | ForEach-Object { "    $_" }) -join "`n"
    $more = if ($bad.Count -gt 20) { "`n    ... và $($bad.Count - 20) file nữa" } else { '' }
    throw ("bcode-manifest.json trong $Source KHÔNG khớp file build ($($bad.Count) file) — manifest sinh trước khi file bị build / chép lại " +
           "(vd BcodeNote.App build sau Bcode.App chép đè BcodeNote.dll). CHƯA chép gì lên server.`n$shown$more`n" +
           "Sinh lại manifest theo đúng file đang có (không build lại, giữ nguyên số build), rồi chạy lại script:`n" +
           "    dotnet msbuild src\$proj\$proj.csproj -t:WriteReleaseManifest -p:Configuration=Release -p:BcodeManifest=true `"-p:OutDir=$Source\\`"")
}

if (-not (Test-Path -LiteralPath $Dest)) { New-Item -ItemType Directory -Path $Dest -Force | Out-Null }
$oldManPath = Join-Path $Dest 'bcode-manifest.json'
$oldMan = $null
if (-not $Full -and (Test-Path -LiteralPath $oldManPath)) {
    try { $oldMan = Read-Manifest $oldManPath } catch { Write-Host "  manifest trên server hỏng — chép toàn bộ." -ForegroundColor Yellow }
}

$destStamp = Join-Path $Dest 'bcode-version.json'
$stampRemoved = $true   # server không có dấu phiên bản (lần đầu / lần chạy trước dừng giữa chừng) = như đã gỡ
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
    $stampRemoved = $removed
}

if ($null -eq $oldMan) {
    Write-Host ("  chép toàn bộ ({0} file)..." -f $newMan.Count)
    Invoke-Robocopy $Source $Dest (@('/E', '/MT:16', '/XD', '*.WebView2', '/XF') + $meta + @('/R:3', '/W:2', '/NP', '/NFL', '/NDL', '/NJH', '/NJS')) '(toàn bộ)'
    $verify = @($newMan.Keys)
    $copied = $newMan.Count; $bytes = 0
} else {
    # File trên server thiếu / khác cỡ với bản build cũng chép lại dù manifest cũ ghi "giống" (bản trước chép dở, file bị sửa tay...)
    $destMap = Get-FileMap $Dest
    $changed = @($newMan.Keys | Where-Object {
            -not $oldMan.ContainsKey($_) -or $oldMan[$_] -ne $newMan[$_] -or $null -eq $destMap[$_] -or $destMap[$_].Length -ne $srcLen[$_]
        } | Sort-Object)
    $drift = @($changed | Where-Object { $oldMan.ContainsKey($_) -and $oldMan[$_] -eq $newMan[$_] })
    if ($drift.Count) { Write-Host ("  {0} file trên server thiếu / khác cỡ dù manifest cũ ghi giống — chép lại." -f $drift.Count) -ForegroundColor Yellow }
    $bytes = 0
    foreach ($rel in $changed) { $bytes += $srcLen[$rel] }
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
            $where = if ($sub) { $sub } else { '(gốc)' }
            Invoke-Robocopy $from $to (@($batch) + @('/IS', '/IT', '/MT:8', '/R:3', '/W:2', '/NP', '/NFL', '/NDL', '/NJH', '/NJS')) $where
        }
        if ($changed.Count -le 30) { $g.Group | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray } }
    }
    $verify = $changed
    $copied = $changed.Count
}

# 3) đọc lại NGAY TRÊN SERVER các file vừa chép: robocopy báo xong nhưng file vẫn là bản cũ / chép dở thì dừng trước khi chép dấu phiên bản
if ($verify.Count) {
    Write-Host ("  kiểm lại SHA-256 {0} file trên server..." -f $verify.Count)
    $wrong = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $verify) {
        $p = Join-Path $Dest $rel
        if (-not [IO.File]::Exists($p)) { $wrong.Add("thiếu file  $rel"); continue }
        $h = Get-Sha $p
        if ($h -ne $newMan[$rel]) { $wrong.Add("khác hash   $rel   cần $($newMan[$rel]), server $h") }
    }
    if ($wrong.Count) {
        $shown = ($wrong | Select-Object -First 20 | ForEach-Object { "    $_" }) -join "`n"
        throw ("$($wrong.Count) file trên server không khớp bản build sau khi chép (file bị khoá / ghi đè không được / bị chép đè giữa chừng):`n$shown`n" + (Get-StopNote))
    }
}

foreach ($f in 'bcode-manifest.json', 'bcode-changelog.txt', 'bcode-version.json') {
    $p = Join-Path $Source $f
    if (-not (Test-Path -LiteralPath $p)) { continue }
    try { Copy-Item -LiteralPath $p -Destination (Join-Path $Dest $f) -Force -ErrorAction Stop }
    catch { throw ("Không ghi được $f lên server: $($_.Exception.Message)`n" + (Get-StopNote)) }
}
Write-Host ("Xong: chép {0} file trong {1:N1} giây. Các máy sẽ thấy bản mới ở lần kiểm tra kế tiếp (mở $($exe -replace '\.exe$',''), hoặc mỗi 4 giờ)." -f $copied, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
