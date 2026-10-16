# Bcode — Write Schema → cập nhật bảng trong file Crystal Report (.rpt). Bcode (.NET 8) không nạp được SDK Crystal (.NET Framework),
# nên chạy script này bằng Windows PowerShell 5.1 (.NET Framework, có sẵn trên Windows) với SAP Crystal Reports runtime 13 đã cài.
# Gọi: powershell -File rptschema.ps1 <request.json> <response.json>   (Services/Rpt/RptSchemaService.cs)
#   read  : { mode:"read",   rpt }  → { ok, version, tables:[{alias,name,path,fields[]}], subreports:[{name,tables[]}], used:["d2.tien"...] (chữ thường) }
#   update: { mode:"update", rpt, xsd, output, maps:[{alias,xsd}], adds:[xsd], subreports:bool }
#           alias = bảng trong rpt đổi nguồn sang bảng xsd (giữ alias để công thức {d1.x} không đổi); adds = bảng xsd thêm mới vào rpt.
#           Lưu ra output (Bcode tự sao lưu rồi chép đè file gốc) → { ok, warnings[], tables[] }
param([string]$RequestPath, [string]$ResponsePath)
$ErrorActionPreference = 'Stop'

function Write-Response($obj) {
    [IO.File]::WriteAllText($ResponsePath, (ConvertTo-Json -InputObject $obj -Depth 8 -Compress), (New-Object Text.UTF8Encoding $false))
}

function Get-FilePath($table) {
    try { return [string]$table.ConnectionInfo.Attributes.Item('QE_LogonProperties').Item('File Path ') } catch { return '' }
}

# Các field {bảng.cột} report đang dùng (ô trên layout, công thức, điều kiện lọc, nhóm, sắp xếp) — đổi nguồn mà bảng mới thiếu cột,
# Crystal âm thầm xoá ô đó khỏi layout, nên Bcode cần biết để cảnh báo trước.
function Get-UsedFields($doc) {
    $used = New-Object System.Collections.ArrayList
    $texts = New-Object System.Collections.ArrayList
    foreach ($sec in $doc.ReportDefinition.Sections) {
        foreach ($o in $sec.ReportObjects) { if ($o.Kind -eq 'FieldObject') { [void]$texts.Add([string]$o.DataSource.FormulaName) } }
    }
    $dd = $doc.DataDefinition
    foreach ($f in $dd.FormulaFields) { [void]$texts.Add([string]$f.Text) }
    foreach ($g in $dd.Groups) { try { [void]$texts.Add([string]$g.ConditionField.FormulaName) } catch { } }
    foreach ($s in $dd.SortFields) { try { [void]$texts.Add([string]$s.Field.FormulaName) } catch { } }
    foreach ($r in $dd.RunningTotalFields) { try { [void]$texts.Add([string]$r.SummarizedField.FormulaName) } catch { } }
    foreach ($s in $dd.SummaryFields) { try { [void]$texts.Add([string]$s.SummarizedField.FormulaName) } catch { } }
    [void]$texts.Add([string]$dd.RecordSelectionFormula); [void]$texts.Add([string]$dd.GroupSelectionFormula)
    foreach ($m in [regex]::Matches(($texts -join "`n"), '\{([^{}?@#]+?)\.([^{}]+?)\}')) { [void]$used.Add(($m.Groups[1].Value + '.' + $m.Groups[2].Value).ToLowerInvariant()) }
    return ,@($used | Sort-Object -Unique)
}

function Describe-Tables($tables) {
    $list = New-Object System.Collections.ArrayList
    foreach ($t in $tables) {
        $fields = New-Object System.Collections.ArrayList
        foreach ($f in $t.DataFields) { [void]$fields.Add([string]$f.Name) }
        [void]$list.Add(@{ alias = [string]$t.Alias; name = [string]$t.Name; path = (Get-FilePath $t); fields = $fields })
    }
    return ,$list
}

$doc = $null
try {
    $req = [IO.File]::ReadAllText($RequestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json

    # Bản runtime cao nhất có trong GAC (13.0.x tuỳ máy) — không gắn cứng số phiên bản.
    foreach ($n in 'CrystalDecisions.Shared', 'CrystalDecisions.CrystalReports.Engine', 'CrystalDecisions.ReportAppServer.ClientDoc',
                   'CrystalDecisions.ReportAppServer.Controllers', 'CrystalDecisions.ReportAppServer.DataDefModel') {
        if ($null -eq [Reflection.Assembly]::LoadWithPartialName($n)) {
            throw "NO_RUNTIME: Máy chưa cài SAP Crystal Reports runtime for .NET Framework (thiếu $n trong GAC). Cài bản 64-bit (CRRuntime_64bit_13_0_x.msi) rồi thử lại."
        }
    }
    $engine = [Reflection.Assembly]::LoadWithPartialName('CrystalDecisions.CrystalReports.Engine')

    $doc = New-Object CrystalDecisions.CrystalReports.Engine.ReportDocument
    $doc.Load([string]$req.rpt, [CrystalDecisions.Shared.OpenReportMethod]::OpenReportByTempCopy)
    $rcd = $doc.ReportClientDocument
    $dbc = $rcd.DatabaseController
    $subNames = @($rcd.SubreportController.GetSubreportNames())

    if ($req.mode -eq 'read') {
        $subs = New-Object System.Collections.ArrayList
        foreach ($s in $subNames) {
            [void]$subs.Add(@{ name = [string]$s; tables = (Describe-Tables $rcd.SubreportController.GetSubreportDatabase($s).Tables) })
        }
        Write-Response @{ ok = $true; version = $engine.GetName().Version.ToString(); tables = (Describe-Tables $dbc.Database.Tables); subreports = $subs; used = (Get-UsedFields $doc) }
        return
    }

    $xsd = [string]$req.xsd
    $warnings = New-Object System.Collections.ArrayList
    $existing = @{}
    foreach ($t in $dbc.Database.Tables) { $existing[[string]$t.Alias] = $t }
    $template = @($dbc.Database.Tables)[0]
    if ($null -eq $template) { throw 'File .rpt không có bảng dữ liệu nào để lấy mẫu kết nối ADO.NET (XML).' }

    foreach ($m in @($req.maps)) {
        if ($null -eq $m) { continue }
        $old = $existing[[string]$m.alias]
        if ($null -eq $old) { [void]$warnings.Add("Không thấy bảng $($m.alias) trong rpt — bỏ qua."); continue }
        $new = $old.Clone($true)
        $new.Name = [string]$m.xsd
        $new.QualifiedName = [string]$m.xsd
        $new.ConnectionInfo.Attributes.Item('QE_LogonProperties').Item('File Path ') = $xsd
        $dbc.SetTableLocation($old, $new)
    }

    foreach ($a in @($req.adds)) {
        if ([string]::IsNullOrEmpty($a)) { continue }
        $alias = [string]$a
        if ($existing.ContainsKey($alias)) { [void]$warnings.Add("rpt đã có bảng tên $alias — không thêm trùng."); continue }
        $t = $template.Clone($true)
        $t.Name = $alias; $t.Alias = $alias; $t.QualifiedName = $alias
        $t.DataFields.RemoveAll()
        $t.ConnectionInfo.Attributes.Item('QE_LogonProperties').Item('File Path ') = $xsd
        $dbc.AddTable($t, $null)
    }

    # Subreport: bảng nào trùng tên với bảng xsd được trỏ sang xsd mới (giữ nguyên tên/alias).
    if ($req.subreports) {
        $xsdNames = @{}
        $ds = New-Object System.Data.DataSet; $ds.ReadXmlSchema($xsd)
        foreach ($xt in $ds.Tables) { $xsdNames[$xt.TableName] = $true }
        foreach ($s in $subNames) {
            foreach ($old in @($rcd.SubreportController.GetSubreportDatabase($s).Tables)) {
                if (-not $xsdNames.ContainsKey([string]$old.Name)) { continue }
                $new = $old.Clone($true)
                $new.ConnectionInfo.Attributes.Item('QE_LogonProperties').Item('File Path ') = $xsd
                $rcd.SubreportController.SetTableLocation($s, $old, $new)
            }
        }
    }

    try { $doc.VerifyDatabase() } catch { [void]$warnings.Add("Verify Database: $($_.Exception.Message)") }
    $doc.SaveAs([string]$req.output)
    Write-Response @{ ok = $true; warnings = $warnings; tables = (Describe-Tables $dbc.Database.Tables) }
}
catch {
    $msg = $_.Exception.Message
    if ($_.Exception.InnerException) { $msg += ' — ' + $_.Exception.InnerException.Message }
    Write-Response @{ ok = $false; error = $msg }
}
finally {
    if ($null -ne $doc) { try { $doc.Close(); $doc.Dispose() } catch { } }
}
