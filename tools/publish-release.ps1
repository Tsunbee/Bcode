<#
  Phát hành bản Bcode / BcodeViewer / Screen Designer lên thư mục phát hành (thư mục mà "Cập nhật ..." của các máy đọc — Shared/AppUpdate.cs).
  Mỗi app một cơ chế cập nhật riêng, thư mục phát hành RIÊNG (không trùng nhau).

  Cách dùng (từ gốc repo, sau khi build Release):
      powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1 [-Dest "\\máy\thư mục phát hành"]
      -Dest                    bỏ trống = <BcodeReleaseFolder> trong csproj của app; khác csproj thì DỪNG (thêm -AnyDest nếu cố ý)
      -App Viewer | Designer   phát hành BcodeViewer / Screen Designer (mặc định: Bcode)
                               Bcode build kèm ScreenDesigner\ (bản khởi đầu cho máy mới) nhưng manifest của Bcode bỏ thư mục đó —
                               Designer cập nhật bằng -App Designer.
      -Source <thư mục build>  nếu không phải src\<app>\bin\Release\net8.0-windows
      -Full                    chép lại toàn bộ (khi nghi file trên server bị sửa tay / thiếu)

  Nhanh: chỉ đọc 1 file bcode-manifest.json đang có trên server, so SHA-256 với bản mới → chỉ chép file KHÁC (thường vài chục file,
  không đụng 3.000+ file mẫu Templates\). Server chưa có manifest (lần đầu) hoặc -Full → chép toàn bộ.

  Thứ tự an toàn cho máy đang kiểm tra cùng lúc:
    0. kiểm SHA-256 MỌI file trong thư mục build với bcode-manifest.json của chính nó (~6 giây / 3.700 file) — lệch thì DỪNG, chưa đụng server
       (v2.3-beta build 475: BcodeNote.App build sau Bcode.App chép đè BcodeNote.dll sau khi manifest đã sinh → máy cập nhật báo "sai mã kiểm tra")
    1. KHÔNG xoá bcode-version.json ở đích (trước đây xoá → script dừng giữa chừng là server mất luôn dấu phiên bản, các máy báo
       "chưa có bản phát hành"). Giữ dấu cũ tới bước 4: máy cập nhật kiểm SHA-256 từng file + đọc lại dấu sau khi tải nên không cài bản chép dở;
       dừng giữa chừng thì máy đang ở bản cũ không thấy gì, chạy lại script là xong.
    1b. thử mở GHI từng file sắp chép đè trên server — file đang bị khoá (máy khác mở Bcode thẳng từ thư mục phát hành, hoặc ĐANG TẢI cập nhật:
       AppUpdate mở file FileShare.Read) thì đợi vài giây, vẫn khoá thì DỪNG khi chưa chép gì.
    2. chép file (KHÔNG /MIR — thư mục đích có thể chứa thứ khác; file thừa ở đích vô hại vì máy cập nhật đi theo manifest).
       Chép cả file trên server thiếu / khác cỡ với bản build dù manifest cũ ghi là giống (bản trước chép dở, sửa tay...).
       Mỗi file thay TRỌN VẸN: chép ra <tên>.bcode-tmp rồi đổi tên đè (Write-Atomic) — ngắt giữa chừng thì file thật vẫn bản cũ nguyên vẹn.
       Cần quyền Modify trên thư mục phát hành (kiểm trước khi chép — thiếu thì dừng). KHÔNG robocopy: nó bỏ qua file cùng cỡ + giờ sửa
       mà khác nội dung và trả mã 0. Lỗi → thử lại rồi DỪNG, in tên file + lỗi.
    3. kiểm lại SHA-256 các file vừa chép NGAY TRÊN SERVER (-Full: mọi file) — lệch thì DỪNG, không chép dấu phiên bản
    4. chép bcode-manifest.json, bcode-changelog.txt, rồi bcode-version.json SAU CÙNG (mỗi file thử lại vài lần — máy cập nhật đọc dấu chớp nhoáng)
  Bỏ qua *.WebView2\ (hồ sơ trình duyệt sinh ra khi chạy Bcode từ thư mục build).
#>
param(
    [string]$Dest,   # trống = <BcodeReleaseFolder> trong csproj của app (thư mục các máy đọc); khác csproj thì DỪNG
    [switch]$AnyDest,   # cho phép -Dest khác csproj (thử nghiệm / thư mục tạm)
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

# Thư mục phát hành phải đúng thư mục các máy đọc = <BcodeReleaseFolder> trong csproj (nhúng vào exe lúc build).
# Trước đây build.bat ghi cứng đường dẫn, csproj đổi bố cục mà build.bat không đổi → phát hành 1 nơi, máy đọc nơi khác.
$csproj = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "..\src\$proj\$proj.csproj"
$relFolder = ''
if (Test-Path -LiteralPath $csproj) {
    $m = [regex]::Match([IO.File]::ReadAllText($csproj), '<BcodeReleaseFolder>\s*([^<]*?)\s*</BcodeReleaseFolder>')
    if ($m.Success) { $relFolder = $m.Groups[1].Value }
}
if (-not $Dest) {
    if (-not $relFolder) { throw "Không đọc được <BcodeReleaseFolder> trong $csproj — truyền -Dest." }
    $Dest = $relFolder
}
elseif ($relFolder -and -not $AnyDest -and
        -not [string]::Equals([IO.Path]::GetFullPath($Dest).TrimEnd('\'), [IO.Path]::GetFullPath($relFolder).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw ("-Dest $Dest KHÁC thư mục phát hành mà $App đọc (<BcodeReleaseFolder> trong $proj.csproj = $relFolder) — các máy sẽ không thấy bản này. " +
           "Bỏ -Dest (dùng thư mục trong csproj), hoặc sửa csproj rồi build lại; cố ý phát hành chỗ khác thì thêm -AnyDest. Chưa chép gì.")
}
if (-not (Test-Path -LiteralPath (Join-Path $Source $exe))) { throw "Không thấy $exe trong $Source — sai -App hoặc -Source?" }
# Chặn phát hành nhầm app vào thư mục đang chứa app khác (mỗi app phải có thư mục phát hành riêng)
foreach ($k in $apps.Keys) {
    $other = $apps[$k][1]
    if ($k -ne $App -and (Test-Path -LiteralPath (Join-Path $Dest $other))) { throw "$Dest đang chứa $other — mỗi app cần thư mục phát hành riêng." }
}
$Dest = [IO.Path]::GetFullPath($Dest).TrimEnd('\')
$meta = 'bcode-version.json', 'bcode-manifest.json', 'bcode-changelog.txt'
$stampRemoved = $false   # server KHÔNG có bcode-version.json từ trước khi chạy (lần đầu / bản cũ của script dừng giữa chừng) — để báo đúng khi dừng

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
# Thay file đích TRỌN VẸN: chép ra "<tên>.bcode-tmp" cạnh nó rồi đổi tên đè lên (MoveFileEx REPLACE_EXISTING — 1 thao tác đổi tên trên server).
# Ngắt giữa chừng thì chỉ file tạm dở, file thật vẫn bản cũ nguyên vẹn. Cần quyền Modify (tạo / đổi tên / xoá) trên thư mục phát hành —
# kiểm ở Test-ModifyRight trước khi chép. KHÔNG dùng robocopy: nó bỏ qua file đích cùng cỡ + giờ sửa mà khác nội dung và trả mã 0
# (v2.5-beta build 497: 188 file "chép xong" nhưng server vẫn bản cũ).
Add-Type -Namespace BcodePublish -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern bool MoveFileEx(string existing, string target, int flags);
'@
$TmpExt = '.bcode-tmp'
function Move-Replace([string]$from, [string]$to) {
    if (-not [BcodePublish.Native]::MoveFileEx($from, $to, 1 -bor 8)) {   # MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
        throw (New-Object ComponentModel.Win32Exception([Runtime.InteropServices.Marshal]::GetLastWin32Error()))
    }
}
function Write-Atomic([string]$from, [string]$to, [string]$rel, [int]$tries = 4, [int]$waitSec = 2) {
    $tmp = $to + $TmpExt
    for ($t = 1; ; $t++) {
        try {
            $dir = Split-Path -Parent $to
            if (-not [IO.Directory]::Exists($dir)) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
            [IO.File]::Copy($from, $tmp, $true)   # giữ giờ sửa như bản build
            if ([IO.File]::Exists($to)) { [IO.File]::SetAttributes($to, 'Normal') }   # đích chỉ-đọc thì đổi tên đè không được
            Move-Replace $tmp $to
            return
        }
        catch {
            if ($t -ge $tries) {
                try { [IO.File]::Delete($tmp) } catch { }
                $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
                throw ("Không ghi được $rel lên server: $($e.Message)`n" +
                       "Thường do máy khác đang mở $exe chạy thẳng từ thư mục phát hành hoặc đang tải cập nhật, hoặc tài khoản $env:USERNAME thiếu quyền Sửa (Modify).`n" + (Get-StopNote))
            }
            Start-Sleep -Seconds $waitSec
        }
    }
}
# Thử tạo / đổi tên đè / xoá 1 file tạm trong thư mục phát hành — thiếu quyền Modify thì dừng TRƯỚC khi chép gì.
function Test-ModifyRight([string]$dir) {
    $a = Join-Path $dir ('~bcode-probe-a' + $TmpExt); $b = Join-Path $dir ('~bcode-probe-b' + $TmpExt)
    try {
        [IO.File]::WriteAllText($a, 'a'); [IO.File]::WriteAllText($b, 'b')
        Move-Replace $a $b
        [IO.File]::Delete($b)
    }
    catch {
        try { [IO.File]::Delete($a) } catch { }; try { [IO.File]::Delete($b) } catch { }
        $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
        throw ("Tài khoản $env:USERNAME chưa có quyền Sửa (Modify: tạo / đổi tên / xoá file) trên $dir — $($e.Message)`n" +
               "Nhờ người quản lý NAS cấp quyền Modify cho thư mục này. CHƯA chép gì, bcode-version.json trên server giữ nguyên.")
    }
}
function Get-StopNote {
    if ($stampRemoved) { return 'Bản phát hành CHƯA hoàn tất: trên server chưa có bcode-version.json (từ trước khi chạy) nên các máy thấy "chưa có bản phát hành" — sửa lỗi rồi chạy lại script.' }
    return 'Bản phát hành CHƯA hoàn tất: bcode-version.json trên server vẫn là bản cũ (giữ nguyên) nên máy đang ở bản cũ chạy bình thường, chưa thấy bản mới; ' +
           'máy ở bản cũ hơn bấm cập nhật lúc này có thể báo "sai mã kiểm tra" (không hỏng gì) — sửa lỗi rồi chạy lại script.'
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

Test-ModifyRight $Dest   # thiếu quyền Modify (vd share chỉ cho Read + Write) → dừng ngay, chưa chép gì

$destStamp = Join-Path $Dest 'bcode-version.json'
# KHÔNG xoá dấu phiên bản cũ (xem đầu file) — chỉ kiểm trước là ghi đè được, kẻo chép xong hết mới phát hiện thiếu quyền ở bước cuối.
$stampRemoved = -not (Test-Path -LiteralPath $destStamp)
if (-not $stampRemoved) {
    try { $a = (Get-Item -LiteralPath $destStamp -Force); if ($a.Attributes -band [IO.FileAttributes]::ReadOnly) { $a.Attributes = 'Normal' } } catch { }
    $canWrite = $false
    for ($t = 1; $t -le 5 -and -not $canWrite; $t++) {
        try { $fs = [IO.File]::Open($destStamp, 'Open', 'Write', 'ReadWrite'); $fs.Dispose(); $canWrite = $true } catch { if ($t -lt 5) { Start-Sleep -Seconds 1 } }
    }
    if (-not $canWrite) {
        throw "Không ghi được $destStamp (máy này / tài khoản $env:USERNAME không có quyền, hoặc file đang bị khoá). Chưa chép gì lên server. " +
              "Nhờ người quản lý share cấp quyền Sửa (Modify) cho thư mục $Dest."
    }
}

# 1b) Thử mở GHI các file sắp chép đè đang có trên server (không đổi nội dung / giờ sửa). File bị khoá — máy khác mở Bcode thẳng từ thư mục
#     phát hành, hoặc đang tải cập nhật (AppUpdate mở FileShare.Read) — thì đợi; vẫn khoá thì dừng khi CHƯA chép gì.
$destMap = Get-FileMap $Dest
$toWrite = if ($null -eq $oldMan) { @($newMan.Keys) } else {
    @($newMan.Keys | Where-Object { -not $oldMan.ContainsKey($_) -or $oldMan[$_] -ne $newMan[$_] -or $null -eq $destMap[$_] -or $destMap[$_].Length -ne $srcLen[$_] })
}
$lockErr = @{}
function Test-Locked([string[]]$rels) {
    $res = @()
    foreach ($rel in $rels) {
        $fi = $destMap[$rel]
        if ($null -eq $fi) { continue }
        try { $fs = [IO.File]::Open($fi.FullName, 'Open', 'Write', 'ReadWrite'); $fs.Dispose() }
        catch { $res += $rel; $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; $lockErr[$rel] = $e.Message.Trim() }
    }
    return , $res
}
$locked = Test-Locked $toWrite
for ($t = 1; $t -le 6 -and $locked.Count; $t++) {
    Write-Host ("  {0} file trên server đang bị khoá — đợi 5 giây rồi thử lại ({1}/6)..." -f $locked.Count, $t) -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    $locked = Test-Locked $locked
}
if ($locked.Count) {
    $shown = ($locked | Select-Object -First 20 | ForEach-Object { "    $_   ($($lockErr[$_]))" }) -join "`n"
    throw ("$($locked.Count) file trên server KHÔNG ghi đè được (đang bị khoá / thiếu quyền) — CHƯA chép gì, bcode-version.json trên server giữ nguyên.`n$shown`n" +
           "Thường do máy khác đang mở $exe chạy thẳng từ $Dest, hoặc đang tải cập nhật. Đợi vài phút / nhờ đóng app rồi chạy lại script.")
}

if ($null -eq $oldMan) {
    $changed = @($newMan.Keys | Sort-Object)
    Write-Host ("  chép toàn bộ ({0} file)..." -f $changed.Count)
} else {
    # File trên server thiếu / khác cỡ với bản build cũng chép lại dù manifest cũ ghi "giống" (bản trước chép dở, file bị sửa tay...)
    $changed = @($toWrite | Sort-Object)   # tính ở 1b
    $drift = @($changed | Where-Object { $oldMan.ContainsKey($_) -and $oldMan[$_] -eq $newMan[$_] })
    if ($drift.Count) { Write-Host ("  {0} file trên server thiếu / khác cỡ dù manifest cũ ghi giống — chép lại." -f $drift.Count) -ForegroundColor Yellow }
    $bytes = 0
    foreach ($rel in $changed) { $bytes += $srcLen[$rel] }
    Write-Host ("  {0} / {1} file khác bản trên server ({2:N1} MB) — chỉ chép các file này." -f $changed.Count, $newMan.Count, ($bytes / 1MB))
}
# Thay từng file trọn vẹn (Write-Atomic: file tạm + đổi tên đè), không robocopy:
#  - robocopy bỏ qua file đích cùng cỡ + cùng giờ sửa mà khác nội dung (loại "Modified", /IS /IT không gồm) và trả mã 0 như đã chép
#    → v2.5-beta build 497: 188 file "chép xong" nhưng server vẫn bản cũ;
#  - 1 robocopy cho mỗi thư mục (100 thư mục Templates qua share) = mấy phút "đứng".
$doneFiles = 0
foreach ($rel in $changed) {
    if (($doneFiles % 5) -eq 0) {
        Write-Progress -Activity "Chép lên $Dest" -Status ("{0}/{1} file: {2}" -f $doneFiles, $changed.Count, $rel) -PercentComplete ([int](100 * $doneFiles / [Math]::Max(1, $changed.Count)))
    }
    try { Write-Atomic (Join-Path $Source $rel) (Join-Path $Dest $rel) $rel }
    catch { Write-Progress -Activity "Chép lên $Dest" -Completed; throw }
    $doneFiles++
    if ($changed.Count -le 30) { Write-Host "    $rel" -ForegroundColor DarkGray }
}
Write-Progress -Activity "Chép lên $Dest" -Completed
if ($changed.Count -gt 30) { Write-Host ("  đã chép {0} file." -f $changed.Count) }
$verify = $changed
$copied = $changed.Count

# 3) đọc lại NGAY TRÊN SERVER các file vừa chép: robocopy báo xong nhưng file vẫn là bản cũ / chép dở thì dừng trước khi chép dấu phiên bản
if ($verify.Count) {
    Write-Host ("  kiểm lại SHA-256 {0} file trên server..." -f $verify.Count)
    $wrong = New-Object System.Collections.Generic.List[string]
    $vi = 0
    foreach ($rel in $verify) {
        if (($vi++ % 20) -eq 0) { Write-Progress -Activity 'Kiểm lại SHA-256 trên server' -Status "$vi/$($verify.Count) file" -PercentComplete ([int](100 * $vi / $verify.Count)) }
        $p = Join-Path $Dest $rel
        if (-not [IO.File]::Exists($p)) { $wrong.Add("thiếu file  $rel"); continue }
        $h = Get-Sha $p
        if ($h -ne $newMan[$rel]) { $wrong.Add("khác hash   $rel   cần $($newMan[$rel]), server $h") }
    }
    Write-Progress -Activity 'Kiểm lại SHA-256 trên server' -Completed
    if ($wrong.Count) {
        $shown = ($wrong | Select-Object -First 20 | ForEach-Object { "    $_" }) -join "`n"
        throw ("$($wrong.Count) file trên server không khớp bản build sau khi chép (file bị khoá / ghi đè không được / bị chép đè giữa chừng):`n$shown`n" + (Get-StopNote))
    }
}

foreach ($f in 'bcode-manifest.json', 'bcode-changelog.txt', 'bcode-version.json') {
    $p = Join-Path $Source $f
    if (-not (Test-Path -LiteralPath $p)) { continue }
    $target = Join-Path $Dest $f
    # Đích là THƯ MỤC trùng tên (vd robocopy cũ tạo nhầm) thì Copy-Item chép VÀO trong nó → lỗi khó hiểu "...\$f\$f" — báo rõ
    if ([IO.Directory]::Exists($target)) {
        throw ("Trên server, $target đang là THƯ MỤC chứ không phải file — xoá thư mục đó rồi chạy lại script.`n" + (Get-StopNote))
    }
    # thử lại tới 8 lần: máy cập nhật đọc dấu phiên bản chớp nhoáng (FileShare.Read) — đổi tên đè lúc đó bị từ chối
    Write-Atomic $p $target $f 8 1
}
Write-Host ("Xong: chép {0} file trong {1:N1} giây. Các máy sẽ thấy bản mới ở lần kiểm tra kế tiếp (mở $($exe -replace '\.exe$',''), hoặc mỗi 4 giờ)." -f $copied, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
