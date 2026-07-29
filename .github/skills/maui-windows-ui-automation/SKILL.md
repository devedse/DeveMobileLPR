---
name: maui-windows-ui-automation
description: 'Test and debug the DeveMobileLPR .NET MAUI Windows UI using native UI Automation and screenshots. Use when verifying Windows runtime behavior, Shell tabs, buttons, sliders, progress, empty states, bounding boxes, layout bounds, accessibility, webcam diagnostics, or the exact published executable.'
argument-hint: 'Describe the Windows UI behavior or visual state to verify'
user-invocable: true
---

# MAUI Windows UI Automation

Use this workflow to verify the actual Windows app after implementation. Compilation is not proof of UI behavior. Prefer semantic UI Automation patterns and explicit state assertions; use screenshots to verify visual composition.

## Safety rules

- Build and test the current workspace artifact, not a downloaded release.
- Do not click Delete, clear history, import, or other destructive controls against user data.
- Do not type secrets into automation commands.
- Stop only the process launched for the test. Never stop another `DeveMobileLPR` process unless its executable path is under the workspace and it is known to be a test instance.
- Keep the process ID and clean it up even after a failed assertion.
- Do not use fixed sleeps. Use `WaitForInputIdle`, observable UI state, repository/file changes, or terminal completion.
- If camera hardware is unavailable, report that limitation and verify the exact diagnostic instead of claiming frame capture worked.

## 1. Produce the artifact

For a final or cross-platform check:

```powershell
.\build.ps1 -Configuration Release
```

The executable under test is `artifacts/windows/win-x64/DeveMobileLPR.exe`.

For an early compile check before publishing:

```powershell
dotnet build .\src\DeveMobileLPR.App\DeveMobileLPR.App.csproj `
  -f net10.0-windows10.0.19041.0 -c Release --no-restore
```

If publishing reports a locked file, list processes and stop only the workspace instance:

```powershell
Get-Process DeveMobileLPR -ErrorAction SilentlyContinue |
  Select-Object Id, Path

Get-Process DeveMobileLPR -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -like "$PWD\artifacts\windows\*" } |
  Stop-Process -Force
```

## 2. Launch and attach

```powershell
$process = Start-Process '.\artifacts\windows\win-x64\DeveMobileLPR.exe' -PassThru
$process.WaitForInputIdle(5000) | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $process.Id)
$window = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    $condition)

if ($null -eq $window) {
    throw "Could not find the app window for process $($process.Id)."
}
```

Keep `$process` and `$window` for all later steps.

## 3. Inspect before acting

Dump semantic names and control types:

```powershell
$elements = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$elements | ForEach-Object {
    [pscustomobject]@{
        Name = $_.Current.Name
        Type = $_.Current.ControlType.ProgrammaticName
        AutomationId = $_.Current.AutomationId
        Enabled = $_.Current.IsEnabled
    }
} | Where-Object Name | Format-Table -AutoSize
```

Use the runtime-supported pattern. Do not assume every element is an invokable button.

## 4. Navigate Shell tabs

MAUI Shell tabs normally expose `SelectionItemPattern`:

```powershell
$tab = $elements |
  Where-Object { $_.Current.Name -eq 'Analyze' } |
  Select-Object -First 1

$tab.GetCurrentPattern(
  [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$process.WaitForInputIdle(3000) | Out-Null
```

Use the same pattern for Drive, History, and Settings. After navigation, reacquire `$elements`; old elements may be stale.

## 5. Invoke buttons and semantic icon controls

Text buttons usually expose `InvokePattern`:

```powershell
$button = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition) |
  Where-Object { $_.Current.Name -eq 'Next frame' } |
  Select-Object -First 1

$button.GetCurrentPattern(
  [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$process.WaitForInputIdle(3000) | Out-Null
```

Icon-only controls must set `SemanticProperties.Description`; query that semantic name, not the glyph. If `GetCurrentPattern` reports unsupported, inspect the control type/patterns instead of retrying blindly.

Cards with `TapGestureRecognizer` may not expose InvokePattern. Prefer adding a semantic command surface. For a one-off non-destructive test, foreground the window and click the card's accessible bounds only after confirming the target:

```powershell
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeInput {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@

[NativeInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
$bounds = $target.Current.BoundingRectangle
[NativeInput]::SetCursorPos(
    [int]($bounds.Left + $bounds.Width / 2),
    [int]($bounds.Top + $bounds.Height / 2)) | Out-Null
[NativeInput]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
[NativeInput]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
```

Never use coordinate clicks for destructive actions.

## 6. Assert slider and progress behavior

Read a slider through `RangeValuePattern`:

```powershell
$slider = $window.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Slider)))

$range = $slider.GetCurrentPattern(
    [System.Windows.Automation.RangeValuePattern]::Pattern)
$before = $range.Current.Value

# Invoke the navigation action, reacquire slider/range, then compare.
$after = $range.Current.Value
if ($after -le $before) {
    throw "Expected the slider to move forward: before=$before after=$after"
}
```

For tab-switch persistence:

1. Start processing through the UI or open a legacy analysis that starts geometry enrichment.
2. Record progress text/value.
3. Select another Shell tab.
4. Return to Analyze.
5. Verify processing state still exists and progress did not reset to cancelled.
6. Use the explicit Cancel command to clean up only if needed.

## 7. Assert layout bounds

UI Automation bounds are useful for overlap and placement checks:

```powershell
$targetNames = @('Route distance', 'No drives yet')
$bounds = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition) |
  Where-Object { $targetNames -contains $_.Current.Name } |
  ForEach-Object {
      $rect = $_.Current.BoundingRectangle
      [pscustomobject]@{
          Name = $_.Current.Name
          Left = $rect.Left
          Top = $rect.Top
          Right = $rect.Right
          Bottom = $rect.Bottom
      }
  }

$bounds | Format-Table -AutoSize
```

Make the assertion explicit. For example, an empty-state card below metrics must have `empty.Top > metric.Bottom`. Also inspect right/bottom edges against the window bounds for clipping.

## 8. Capture and inspect a screenshot

```powershell
Add-Type -AssemblyName System.Drawing
$rect = $window.Current.BoundingRectangle
$bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen(
    [int]$rect.Left,
    [int]$rect.Top,
    0,
    0,
    $bitmap.Size)
$screenshot = Join-Path $env:TEMP 'DeveMobileLPR-ui-check.png'
$bitmap.Save($screenshot, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
$screenshot
```

Open the image with the image-viewing tool. Check the actual requested property:

- detection rectangles and labels are visibly drawn over the plate;
- preview scaling and offsets align with the image;
- timeline and navigation remain in view;
- empty messages do not overlap cards;
- text and controls are not clipped;
- z-order is correct.

Do not claim bounding boxes work from an old analysis that contains no persisted geometry. Reprocess or enrich a real analysis, verify non-empty bounds/source dimensions in JSON, navigate to that frame, and capture the visible box.

## 9. Hardware diagnostics

For webcam issues, first distinguish code failure from absent hardware/privacy:

```powershell
Get-PnpDevice -PresentOnly |
  Where-Object { $_.Class -in @('Camera', 'Image') } |
  Select-Object Class, FriendlyName, Status, InstanceId

Get-ItemProperty `
  -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam' `
  -ErrorAction SilentlyContinue |
  Select-Object Value, LastUsedTimeStart, LastUsedTimeStop
```

Then read the app's accessible status text. If no video-capture device exists, validate the clear error state; do not say live frames were tested.

## 10. Cleanup and report

```powershell
Get-Process -Id $process.Id -ErrorAction SilentlyContinue | Stop-Process -Force
```

Report:

- exact artifact tested;
- actions performed;
- before/after values or bounds;
- screenshot result;
- hardware/data limitations;
- whether destructive controls were deliberately not invoked.

After code changes, still run the repository's required `./build.ps1 -Configuration Release`, `git diff --check`, and final status check.