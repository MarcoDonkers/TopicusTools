# ATF Environment Debug Notes — S3942-R1889
Generated: 2026-06-24 by Copilot CLI (claude-sonnet-4.6)

## Environment Overview

| Item | Value |
|---|---|
| Target environment | **o21** |
| Web app URL | `https://o21.qsp.finance.lab/` |
| Application server | `MGQSP-OV-FRC04.finance.lab` (IP: 10.160.0.163) |
| SQL server | `mgqsp-ov-sql04.finance.lab` (IP: 10.160.0.150), SQL 15.00.2145 |
| Main database | `QSP_O21` |
| DWH database | `QSP_O21_DWH` |
| Components base URL | `https://cp-o21.qsp.finance.lab/` |
| Remote credentials | `finance.lab\npa-qsprelease` / `R3lea4st00l1ingv1` |

## Architecture Summary

```
[ATF Test Runner]
    └─> Selenium → https://o21.qsp.finance.lab/  (IIS on MGQSP-OV-FRC04, site: QSP.Webapp)
                        └─> FinGen Windows Service  (D:\QSP\service\FinGen.Service.WindowsService.exe)
                                ├─> SQL Server: mgqsp-ov-sql04\QSP_O21
                                ├─> MSMQ queues: net.msmq://localhost/private/qsp-o21.statuschange.*
                                │       └─> Processed by: QSPJobservice (Windows Service)
                                ├─> DWH API: https://cp-o21.qsp.finance.lab/Qsp.Dwh/
                                │       └─> dotnet Qsp.Dwh.dll (D:\Components\QSP.DWH\)
                                │       └─> Writes to: QSP_O21_DWH.dwh.HDNRecord
                                ├─> ECH: https://cp-o21.qsp.finance.lab/Qsp.ECH/
                                │       └─> Also: Qsp.ECH.WindowsService (Windows Service)
                                └─> ITP SOAP: https://localhost:61168/  ← NOT INSTALLED
```

## Remote Server — Key Directories

| Path | Contents |
|---|---|
| `D:\QSP\service\` | Main FinGen Windows Service + config |
| `D:\Components\QSP.DWH\` | DWH ASP.NET Core service |
| `D:\Components\QSP.ECH\` | ECH component |
| `D:\API\` | API services |
| `D:\QSP\mock\` | Mock services (no ITP mock present) |
| `E:\Logs\Components\QSP.DWH\` | DWH application logs |
| `E:\Logs\` | All service logs |
| `C:\inetpub\logs\LogFiles\W3SVC789622594\` | IIS logs for Components site (cp-o21) |
| `C:\Temp\` | Writable via WMI remote process creation |

## Key Config Files

| File | Purpose |
|---|---|
| `C:\WorkEnvironment\Tools\ATFReadAndRunner\publish\appsettings.json` | ATF runner: MaxParallelism, timeout overrides |
| `C:\WorkEnvironment\QSP.Core\FinGen.WebApplication.SysteemTest\App.config` | Test project: target URL, DB connections, credentials, timeouts |
| `C:\WorkEnvironment\QSP.Core\Config\o21.xml` | App environment config: ITP endpoint, MSMQ, job schedules |
| `\\MGQSP-OV-FRC04.finance.lab\D$\QSP\service\FinGen.Service.WindowsService.exe.config` | Runtime config for the main Windows service |
| `\\MGQSP-OV-FRC04.finance.lab\D$\Components\QSP.DWH\appsettings.json` | DWH service config (reads DB conn from Qsp.Configuration API) |
| `\\MGQSP-OV-FRC04.finance.lab\D$\Components\QSP.DWH\web.config` | IIS ASP.NET Core hosting config for DWH |

## Running Services on MGQSP-OV-FRC04 (confirmed via WMI)

- `QSPJobservice` — processes MSMQ status change queue
- `Qsp.ECH.WindowsService`
- `QSP.API.HDN.WindowsService`
- `Qsp.Document.WindowsService`
- `QSP.Notifications.WindowsService`
- `QSP.Traceability.WindowsService`
- `QSP-Memcached-Storage`
- `dotnet .\Qsp.Dwh.dll` (as IIS-hosted app in site "Components")
- `FinGen.Service.WindowsService.exe`
- `QSP.ECH.WindowsService.exe`

**NOT running / NOT installed:**
- ITP Server (expected at `https://localhost:61168/`) — **no process, no service, no files**

## IIS Sites on MGQSP-OV-FRC04

| Site | Binding | App Pool |
|---|---|---|
| API | `cp-o21.qsp.finance.lab:80/443` | AP_API |
| Components | `cp-o21.qsp.finance.lab:80/443` | AP_Components |
| QSP.Webapp | (QSP web app) | AP_QSP.Webapp |
| QSP.Webservice | (SOAP service) | AP_QSP.Webservice |

`Qsp.Dwh` runs under app pool `AP_COMPONENTS` (capital, different from `AP_Components`).
Physical path: `D:\Components\QSP.DWH` (note: IIS uses uppercase `QSP.DWH` as the app but dir is also `QSP.DWH`).

## Remote Access Methods

- **File shares**: `net use \\MGQSP-OV-FRC04.finance.lab\D$` (works)
- **WMI Win32_Process.Create**: Works for running remote commands, output redirect to `C:\Temp\`
- **WMI root/WebAdministration**: Works for IIS site/app pool queries
- **PSRemoting / WinRM**: Does NOT work
- **SQL**: Works directly — `mgqsp-ov-sql04.finance.lab`, `QSP_O21`, with credential above
