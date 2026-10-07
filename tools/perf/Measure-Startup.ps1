<#
.SYNOPSIS
  Measures, from outside the app, the time from process start to a visible main window and the memory of the
  whole process tree (all processes with the same image name) after a settle time. Works for any desktop app,
  so the native app and the Electron app are measured the same way.
.EXAMPLE
  .\Measure-Startup.ps1 -Exe ..\..\artifacts\publish\BearingPoint.RemoteDesktop.exe -Arguments '--data-dir','C:\perf\data' -Runs 5
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [string[]]$Arguments = @(),
    [int]$Runs = 5,
    [int]$SettleSeconds = 10,
    [string]$Label = ''
)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  public static bool HasVisibleWindow(HashSet<uint> pids) {
    bool found = false;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (pids.Contains(p) && IsWindowVisible(h) && GetWindowTextLength(h) > 0) { found = true; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
$name = [IO.Path]::GetFileNameWithoutExtension($Exe)
$results = @()
for ($i = 1; $i -le $Runs; $i++) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = if ($Arguments.Count) { Start-Process $Exe -ArgumentList $Arguments -PassThru } else { Start-Process $Exe -PassThru }
    $visible = $null
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        $pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object { [void]$pids.Add([uint32]$_.Id) }
        if ($pids.Count -and [Win]::HasVisibleWindow($pids)) { $visible = $sw.Elapsed.TotalMilliseconds; break }
        Start-Sleep -Milliseconds 10
    }
    Start-Sleep $SettleSeconds
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    $ws = ($procs | Measure-Object WorkingSet64 -Sum).Sum / 1MB
    $priv = ($procs | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB
    $results += [pscustomobject]@{ Run = $i; VisibleMs = [math]::Round($visible); Processes = $procs.Count; WorkingSetMB = [math]::Round($ws, 1); PrivateMB = [math]::Round($priv, 1) }
    $procs | Stop-Process -Force
}
$sorted = $results | Sort-Object VisibleMs
$median = $sorted[[int][math]::Floor($sorted.Count / 2)]
$results | Format-Table -AutoSize | Out-String | Write-Host
[pscustomobject]@{
    Label = if ($Label) { $Label } else { $name }
    MedianVisibleMs = $median.VisibleMs
    MinVisibleMs = ($results | Measure-Object VisibleMs -Minimum).Minimum
    MedianWorkingSetMB = ($results | Sort-Object WorkingSetMB)[[int][math]::Floor($results.Count / 2)].WorkingSetMB
    MedianPrivateMB = ($results | Sort-Object PrivateMB)[[int][math]::Floor($results.Count / 2)].PrivateMB
    Processes = $median.Processes
}
