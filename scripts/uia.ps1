# Small UIA helper for exploring the replay windows.
#   uia.ps1 <process> dump                     list the replay window controls (id, name, enabled, value)
#   uia.ps1 <process> invoke <automationId>    press a button in the Replay Manage window
#   uia.ps1 <process> set <automationId> <txt> set the value of an edit (searched by parent id, e.g. dtp_IniDt)
#   uia.ps1 <process> windows                  list all top-level windows of the process
param([string]$Proc, [string]$Cmd, [string]$Id, [string]$Text)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$pids = @(Get-Process -Name $Proc -ErrorAction Stop | ForEach-Object Id)
$tops = $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition) |
    Where-Object { $pids -contains $_.Current.ProcessId }

function Describe($e, $depth) {
    $c = $e.Current
    $val = ''
    $vp = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $val = " value='" + $vp.Current.Value + "'" }
    "{0}[{1}] id='{2}' name='{3}'{4}{5}" -f ('  ' * $depth), $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.AutomationId, $c.Name, $val, $(if ($c.IsEnabled) { '' } else { ' DISABLED' })
}
function Walk($e, $depth, $max) {
    Describe $e $depth
    if ($depth -ge $max) { return }
    $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $ch = $w.GetFirstChild($e)
    while ($ch) { Walk $ch ($depth + 1) $max; $ch = $w.GetNextSibling($ch) }
}

if ($Cmd -eq 'windows') { foreach ($t in $tops) { Describe $t 0 }; return }
if ($Cmd -eq 'walk') { foreach ($t in $tops) { if ($t.Current.Name -eq $Id) { Walk $t 0 12 } }; return }

$win = $tops | Where-Object { $_.Current.Name -eq 'Replay Manage' } | Select-Object -First 1
if (-not $win) { throw "No Replay Manage window for $Proc" }
$byId = { param($root, $aid) $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $aid))) }

switch ($Cmd) {
    'dump' { Walk $win 0 3 }
    'invoke' {
        $b = & $byId $win $Id
        $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        "invoked $Id"
    }
    'set' {
        $p = & $byId $win $Id
        $edit = $p.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PART_Txt')))
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
        "set $Id to $Text"
    }
}
