# Data Sources — How Information Was Gathered
Generated: 2026-06-24 by Copilot CLI (claude-sonnet-4.6)

This document explains exactly WHERE each piece of information was sourced from
so another AI instance (or human) can reproduce or verify the findings.

---

## 1. ATF Test Reports

**How**: Read HTML report directly from disk.
```
C:\WorkEnvironment\Tools\ATFReadAndRunner\publish\Results\S3942-R1889\ATF_Report_20260624_130602.html
```
**What's in it**: All test results with full error messages, stack traces, test names.
Individual XML result files are also in this same folder (one per test per run).

---

## 2. ATF Runner Configuration

**File**: `C:\WorkEnvironment\Tools\ATFReadAndRunner\publish\appsettings.json`

Key settings found here:
- `MaxParallelism` — how many tests run in parallel
- `OverrideMaxWaitTimeStatusChangeMs` — overrides the 360 000 ms default timeout
- `OmgevingNaam` — target server name

**Modified**: `MaxParallelism` 5→2, `OverrideMaxWaitTimeStatusChangeMs` 60000→0

---

## 3. Test Project App.config

**File**: `C:\WorkEnvironment\QSP.Core\FinGen.WebApplication.SysteemTest\App.config`

Key settings:
- `Url` = `https://o21.qsp.finance.lab/`
- `MaxWaitTimeStatusChangeMiliseconds` = 360000 (6 min default)
- `OmgevingNaam` = `MGQSP-OV-FRC04.finance.lab`
- `JobServiceUser` = `financelab\npa-qsprelease`

---

## 4. Environment Config (ITP endpoint, MSMQ)

**File**: `C:\WorkEnvironment\QSP.Core\Config\o21.xml`

Key: `<endpoint name="ITPServerSoap" address="https://localhost:61168/"/>`
This shows ITP is expected on localhost of the app server, port 61168.

---

## 5. Main Windows Service Runtime Config

**File (remote)**: `\\MGQSP-OV-FRC04.finance.lab\D$\QSP\service\FinGen.Service.WindowsService.exe.config`

Key settings:
- `QspDwhEndpoint` = `https://cp-o21.qsp.finance.lab/Qsp.Dwh/`
- `QspDwhMock` = `false`
- Confirms DWH calls are real (not mocked)

---

## 6. SQL Database Queries

**Connection**: Server=`mgqsp-ov-sql04.finance.lab`, DB=`QSP_O21`
**Auth**: Windows auth or SQL auth with credential `finance.lab\npa-qsprelease`

### Status change slowness investigation
```sql
-- StatusChangeLog: how long did status changes take during the test run?
SELECT
  COUNT(*) AS Total,
  SUM(CASE WHEN DATEDIFF(SECOND, TimeSendToQueue, TimeFinished) >= 60 THEN 1 ELSE 0 END) AS SlowCount,
  MAX(DATEDIFF(SECOND, TimeSendToQueue, TimeFinished)) AS MaxSeconds
FROM [QSP_O21].[dbo].[StatusChangeLog]
WHERE TimeSendToQueue BETWEEN '2026-06-24 13:00' AND '2026-06-24 14:00'
```
**Result**: 37 of 432 (8.6%) took ≥60s, max 166s. With MaxParallelism=5 this caused timeouts.
On 2026-06-22 (no parallel tests): max 25s only.

### DWH HDNRecord empty check
```sql
USE QSP_O21_DWH
SELECT COUNT(*) FROM dwh.HDNRecord
-- Result: 0  (completely empty)

USE QSP_O21
SELECT COUNT(*) FROM HDNBericht
-- Result: 489 (source records exist but DWH never received them)
```

### ITP / external source issues
```sql
USE QSP_O21
SELECT TOP 20 * FROM Hdn.ExpectedExternalSourceRecord
WHERE ReceivedResponse = 0
ORDER BY CreatedAt DESC
-- Many rows going back to 2026-06-03: ITP has been broken for 3+ weeks
```

### Elmah error log
```sql
USE QSP_O21
SELECT TOP 20 * FROM ELMAH_Error ORDER BY TimeUtc DESC
-- Last checked: June 23rd had SQL deadlocks and NHibernate StaleStateException (caused by parallel tests)
-- No entries from today's test run (13:06 onwards)
```

---

## 7. Remote Services — WMI

**Method**: PowerShell `Get-WmiObject -ComputerName "MGQSP-OV-FRC04.finance.lab" -Credential $cred -Class Win32_Service`

```powershell
$cred = New-Object System.Management.Automation.PSCredential(
    "finance.lab\npa-qsprelease",
    (ConvertTo-SecureString "R3lea4st00l1ingv1" -AsPlainText -Force))

Get-WmiObject -ComputerName "MGQSP-OV-FRC04.finance.lab" -Credential $cred -Class Win32_Service |
    Where-Object { $_.State -eq "Running" } | Select Name, State
```

---

## 8. Remote Process List — WMI

**Method**: `Get-WmiObject -Class Win32_Process`

Used to check if ITP process was running and what DWH process name/path is.

```powershell
Get-WmiObject -ComputerName "MGQSP-OV-FRC04.finance.lab" -Credential $cred -Class Win32_Process |
    Select Name, ExecutablePath, CommandLine | Where-Object { $_.Name -like "*itp*" -or $_.Name -like "*dwh*" }
```

DWH found as: `dotnet.exe  .\Qsp.Dwh.dll` in dir `D:\Components\QSP.DWH\`
ITP: **no process found at all**.

---

## 9. Port Probe for ITP

```powershell
$tcp = New-Object System.Net.Sockets.TcpClient
$async = $tcp.BeginConnect("10.160.0.163", 61168, $null, $null)
$wait = $async.AsyncWaitHandle.WaitOne(3000, $false)
# Result: $wait = False → port not open → ITP not listening
```

---

## 10. IIS Configuration — WMI WebAdministration

```powershell
Get-WmiObject -ComputerName "MGQSP-OV-FRC04.finance.lab" -Credential $cred `
    -Namespace "root/WebAdministration" -Class Site
# Returns 4 sites: API, Components, QSP.Webapp, QSP.Webservice
```

For app list and app pool states, used remote WMI process creation to run appcmd:
```powershell
Invoke-WmiMethod -ComputerName "MGQSP-OV-FRC04.finance.lab" -Credential $cred `
    -Class Win32_Process -Name Create `
    -ArgumentList 'cmd.exe /c C:\Windows\System32\inetsrv\appcmd.exe list app > C:\Temp\applist.txt 2>&1'
# Then read: \\MGQSP-OV-FRC04.finance.lab\C$\Temp\applist.txt
```

---

## 11. IIS Access Logs for Components Site

Site ID for "Components" is `789622594`.
Log location: `\\MGQSP-OV-FRC04.finance.lab\C$\inetpub\logs\LogFiles\W3SVC789622594\`
Today's log: `u_ex260624.log`

**Key finding**: DWH is only ever called at `/Qsp.Dwh/api/bepaling-eigenaar-procesgroep` — never any HDN write endpoint.
Requests to `/Qsp.Dwh/` (root) return 404 — expected for ASP.NET Core with no root endpoint.
Last DWH activity at 12:04 was browser probing (not a service call).
**No DWH requests at all during the 13:00–14:00 test window** (IIS log stops at 12:06).

---

## 12. DWH Service Application Log

**File**: `\\MGQSP-OV-FRC04.finance.lab\E$\Logs\Components\QSP.DWH\20260624.log`

Content shows:
- Service restarted at 10:03:07 (after 09:48 shutdown)
- Last log entry at 10:03:07 — no activity logged after that
- DWH service only logs startup/shutdown events at INFO level; actual API calls are not logged here

---

## 13. DWH Component Config

**File**: `\\MGQSP-OV-FRC04.finance.lab\D$\Components\QSP.DWH\appsettings.json`

The DWH service gets its **database connection string** dynamically at startup from:
`https://cp-o21.qsp.finance.lab/Qsp.Configuration/api/V2/GetConfiguration/Dwh/nvt`

This means if `Qsp.Configuration` returns a wrong/empty connection string, DWH silently fails to write.
The IIS log shows this config call did succeed at 06:49 (startup), returning HTTP 200.
