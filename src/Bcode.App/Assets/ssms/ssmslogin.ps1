param([int]$ProcId, [string]$Server, [string]$User, [string]$Database, [switch]$Sql, [int]$WaitSec = 120)
# Bcode → Mở SSMS: chờ hộp "Connect" của tiến trình SSMS vừa mở, điền theo workspace rồi bấm Connect (dùng UI Automation).
#  - Mật khẩu đọc từ stdin (không đưa lên dòng lệnh — tiến trình khác đọc được).
#  - Luôn tick "Trust Server Certificate": SSMS mới mặc định Encrypt = Mandatory, server nội bộ dùng chứng chỉ tự ký.
#  - SSMS 21+ đăng nhập SQL: Bcode mở SSMS KHÔNG kèm -S/-U (kèm -U thì SSMS tự kết nối luôn bằng mật khẩu rỗng → 1 lần đăng nhập
#    sai + hộp Error chặn nút Connect), nên script điền đủ Server / Authentication / User / Password / Database.
#  - SSMS không tự hiện hộp Connect lúc mở (Startup = môi trường trống) → bấm nút "Connect Object Explorer..." để mở hộp.
# In ra 1 dòng: OK | NODIALOG | ERR: <lý do>.
$ErrorActionPreference = 'Stop'
$pass = ''
if ($Sql) { $pass = [Console]::In.ReadLine(); if ($null -eq $pass) { $pass = '' } }
try {
  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
  $AE = [System.Windows.Automation.AutomationElement]
  $Scope = [System.Windows.Automation.TreeScope]
  $CT = [System.Windows.Automation.ControlType]
  function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
  # Phần tử đầu tiên loại $type có tên khớp regex $rx (không phân biệt hoa thường: SSMS 20 ghi "Trust server certificate", 21+ "Trust Server Certificate").
  function FindRx($root, $type, $rx) {
    foreach ($e in $root.FindAll($Scope::Descendants, (Cond $AE::ControlTypeProperty $type))) { if ($e.Current.Name -match $rx) { return $e } }
    return $null
  }
  function SetVal($el, $v) {
    if ($null -eq $el -or -not $el.Current.IsEnabled) { return }
    try { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) } catch { }
  }
  # Ô chọn không sửa được (Authentication): mở ra, chọn mục có tên khớp regex, đóng lại.
  function SelectItem($combo, $rx) {
    if ($null -eq $combo -or -not $combo.Current.IsEnabled) { return }
    try {
      $cur = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
      if ($cur.Count -gt 0 -and $cur[0].Current.Name -match $rx) { return }
    } catch { }
    $ec = $null
    try { $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand(); Start-Sleep -Milliseconds 300 } catch { }
    $item = FindRx $combo $CT::ListItem $rx
    if ($item) { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    if ($ec) { try { $ec.Collapse() } catch { } }
    Start-Sleep -Milliseconds 300   # ô User / Password bật / tắt theo kiểu xác thực
  }

  # 1) Chờ hộp Connect có ô Password (SSMS khởi động nguội 10–30 giây).
  $root = $AE::RootElement
  $byProc = Cond $AE::ProcessIdProperty $ProcId
  $dlg = $null; $pwd = $null; $askedDialog = $false
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $WaitSec) {
    foreach ($w in $root.FindAll($Scope::Children, $byProc)) {
      $d = if ($w.Current.Name -eq 'Connect' -or $w.Current.Name -eq 'Connect to Server') { $w } else { FindRx $w $CT::Window '^Connect( to Server)?$' }
      if ($d) { $p = FindRx $d $CT::Edit '^Password'; if ($p) { $dlg = $d; $pwd = $p; break } }
      if (-not $askedDialog -and $sw.Elapsed.TotalSeconds -gt 15) {
        $btn = FindRx $w $CT::Button '^Connect Object Explorer'
        if ($btn -and $btn.Current.IsEnabled) { try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $askedDialog = $true } catch { } }
      }
    }
    if ($dlg) { break }
    Start-Sleep -Milliseconds 400
  }
  if (-not $dlg) { 'NODIALOG'; exit }
  Start-Sleep -Milliseconds 600   # để hộp nạp xong giá trị lần trước, tránh bị ghi đè sau khi điền

  # Hộp Error còn mở (lần kết nối trước hỏng) chặn nút Connect → đóng.
  foreach ($w in $root.FindAll($Scope::Children, $byProc)) {
    $err = FindRx $w $CT::Window '^Error$'
    if ($err) { try { $err.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep -Milliseconds 300 } catch { } }
  }

  # 2) Điền.
  $srv = FindRx $dlg $CT::Edit '^Server name'
  if (-not $srv) { $sc = FindRx $dlg $CT::ComboBox '^Server name'; if ($sc) { $srv = $sc.FindFirst($Scope::Descendants, (Cond $AE::ControlTypeProperty $CT::Edit)) } }
  SetVal $srv $Server
  SelectItem (FindRx $dlg $CT::ComboBox '^Authentication') $(if ($Sql) { 'SqlPassword|^SQL Server Authentication' } else { 'Integrated,|^Windows Authentication' })
  if ($Sql) {
    $u = FindRx $dlg $CT::Edit '^(User name|Login)'
    if (-not $u) { $uc = FindRx $dlg $CT::ComboBox '^(User name|Login)'; if ($uc) { $u = $uc.FindFirst($Scope::Descendants, (Cond $AE::ControlTypeProperty $CT::Edit)) } }
    SetVal $u $User
    SetVal (FindRx $dlg $CT::Edit '^Password') $pass
  }
  if ($Database) {
    $db = FindRx $dlg $CT::ComboBox '^Database name'
    if ($db) { SetVal ($db.FindFirst($Scope::Descendants, (Cond $AE::ControlTypeProperty $CT::Edit))) $Database }
  }
  $trust = FindRx $dlg $CT::CheckBox '^Trust server certificate'
  if ($trust) {
    $t = $trust.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($t.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $t.Toggle() }
  }

  # Cửa sổ SSMS bật lên giành focus: người dùng đang gõ dở ở chỗ khác thì phím lọt vào ô đang focus (đã gặp: Server Name thành chữ gõ dở).
  # Đọc lại trước khi bấm Connect, lệch thì đặt lại (tối đa 3 lần).
  function ValOf($el) { if ($null -eq $el) { return $null }; try { return $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return $null } }
  for ($i = 0; $i -lt 3; $i++) {
    $bad = $false
    if ($srv -and (ValOf $srv) -ne $Server) { SetVal $srv $Server; $bad = $true }
    if ($Sql -and $u -and (ValOf $u) -ne $User) { SetVal $u $User; $bad = $true }
    if (-not $bad) { break }
    Start-Sleep -Milliseconds 300
  }

  # 3) Bấm Connect (nút bật lại sau khi đủ thông tin).
  $ok = $dlg.FindFirst($Scope::Descendants, (Cond $AE::AutomationIdProperty 'OkButton'))
  if (-not $ok) { $ok = FindRx $dlg $CT::Button '^Connect$' }
  if (-not $ok) { 'ERR: không thấy nút Connect'; exit }
  $sw.Restart()
  while (-not $ok.Current.IsEnabled -and $sw.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 150 }
  $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  'OK'
} catch {
  'ERR: ' + $_.Exception.Message
}
