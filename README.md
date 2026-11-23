# Huy Hoang Scanner Agent

Small Windows helper for scanning from the customer page.

The customer page calls this helper at `http://127.0.0.1:9876/scan`. The helper opens the Windows scanner flow, saves a JPG locally, then uploads it to Laravel using a short-lived signed upload URL.

## First Setup

1. Install [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) on the Windows computer for development, or use the published EXE after it is built.
2. Create this folder for local scan copies:

```text
C:\HuyHoangScannerAgent\HuyHoangScans
```

3. Confirm the scanner works in Windows first. If you use PaperStream, keep it installed because it provides the scanner driver.

4. Run the helper:

```powershell
cd HuyHoangScannerAgent
dotnet run
```

5. Open this URL on the same computer to confirm it is running:

```text
http://127.0.0.1:9876/health
```

Expected result:

```json
{"ok":true}
```

## API

Scan and upload from the local scanner:

```text
POST http://127.0.0.1:9876/scan
```

Example body:

```json
{
  "uploadUrl": "https://booking.huyhoangcargo.com/api/scanner/customer/5319/scan-upload?expires=...&token=...",
  "fieldName": "scan_file"
}
```

Fallback/check newest scanned file:

```text
GET http://127.0.0.1:9876/latest
```

Fallback/download newest scanned file:

```text
GET http://127.0.0.1:9876/download-latest
```

## Auto-Crop Scans

The agent automatically trims blank scanner-bed space around JPG scans before upload.

You can adjust it in `HuyHoangScannerAgent\appsettings.json`:

```json
{
  "ScannerAgent": {
    "AutoCropEnabled": true,
    "AutoCropMarginPixels": 30,
    "AutoCropWhiteThreshold": 245,
    "AutoCropMinDifference": 18
  }
}
```

If the crop is too tight, increase `AutoCropMarginPixels`.

If the crop does not remove enough blank area, lower `AutoCropWhiteThreshold` slightly, for example `235`.

If the scanner bed has colored shadows/noise and crops too aggressively, increase `AutoCropMinDifference`, for example `25`.

## Build EXE

On Windows:

```powershell
dotnet publish HuyHoangScannerAgent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The EXE will be in:

```text
HuyHoangScannerAgent\bin\Release\net8.0-windows\win-x64\publish
```

Recommended install folder:

```text
C:\HuyHoangScannerAgent
```

Copy the published EXE and appsettings file into that folder.

## Start Automatically With Windows

Use Windows Task Scheduler to keep the local agent running automatically.

Recommended setup for most staff computers:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\install-task-at-logon.ps1
```

This starts the agent one minute after the Windows user logs in, giving the desktop session time to initialize. Run the installer from the same Windows account that will use the scanner, with PowerShell elevated. If you use a separate administrator account to install the task, it will be registered for that account's sign-in.

After restarting, sign in and wait one minute before checking http://127.0.0.1:9876/health. If the agent is unavailable, check the task's **History** and **Last Run Result** before starting it manually, so you can see what happened at sign-in.

The logon installer also enables **Start scanner agent** on the customer page for that Windows account. Rerun the updated installer once on each workstation to enable the button. If the agent is down, click the button, approve the browser prompt, and then click **Scan document**.

If `dotnet run` works but the scheduled task does not, stop the terminal run with Ctrl+C so port 9876 is available. In PowerShell as administrator, stop the old task if it is running, then publish from the repository root and replace the installed EXE. Preserve your installed `appsettings.json` if you customized it.

```powershell
Stop-ScheduledTask -TaskName "Huy Hoang Scanner Agent"
dotnet publish HuyHoangScannerAgent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
Copy-Item .\HuyHoangScannerAgent\bin\Release\net8.0-windows\win-x64\publish\HuyHoangScannerAgent.exe C:\HuyHoangScannerAgent\ -Force
.\scripts\install-task-at-logon.ps1
```

Open http://127.0.0.1:9876/health and try a scan. If the agent exits, check the task's **Last Run Result** with `Get-ScheduledTaskInfo -TaskName "Huy Hoang Scanner Agent"`. You can also launch the installed EXE from `C:\HuyHoangScannerAgent` to see its startup error.

If the `fi-65F` direct scan works without showing scanner UI, you can install a true startup task instead:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\install-task-at-startup.ps1
```

To remove the scheduled task:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\uninstall-task.ps1
```

Manual setup:

1. Open **Task Scheduler**.
2. Click **Create Task**, not Create Basic Task.
3. On the **General** tab:
   - Name: `Huy Hoang Scanner Agent`
   - Select: `Run whether user is logged on or not`
   - Check: `Run with highest privileges`
4. On the **Triggers** tab:
   - Click **New**
   - Begin the task: `At startup`
   - Optional: delay task for `30 seconds`
5. On the **Actions** tab:
   - Click **New**
   - Action: `Start a program`
   - Program/script:

```text
C:\HuyHoangScannerAgent\HuyHoangScannerAgent.exe
```

   - Start in:

```text
C:\HuyHoangScannerAgent
```

6. On the **Conditions** tab:
   - Uncheck `Start the task only if the computer is on AC power` if needed.
7. On the **Settings** tab:
   - Uncheck `Stop the task if it runs longer than`
   - Check `Run task as soon as possible after a scheduled start is missed`
   - If the task fails, restart every `1 minute`
   - Attempt restart `3 times`
8. Save and enter the Windows password if asked.

Test after restart:

```text
http://127.0.0.1:9876/health
```

Expected result:

```json
{"ok":true}
```

## Notes

- This version uses Windows Image Acquisition through the installed scanner driver.
- The helper saves local JPG copies into `C:\HuyHoangScannerAgent\HuyHoangScans`.
- The helper uploads directly to Laravel using a temporary scanner token generated by the customer page.
- If WIA cannot control the scanner, keep PaperStream installed and test whether the scanner appears in Windows scanner dialogs.
- If the website cannot call `127.0.0.1`, check firewall and browser security settings.
