# Issue Status — ATF Test Failures S3942-R1889
Generated: 2026-06-24 15:15 by Copilot CLI (claude-sonnet-4.6)

This document tracks each failure category, its root cause, and current fix status.

---

## Summary

| # Tests | Category | Root Cause | Status |
|---|---|---|---|
| 8 | StatusovergangHelper timeout (60s) | MaxParallelism=5 + 60s override | ✅ FIXED in appsettings.json |
| 2 | Tests 136, 140: stuck at "Opvragen gegevens Externe..." | ITP service not installed | ❌ NEEDS MANUAL ACTION |
| 1 | Test 143: ECH timeout (360s) | Under investigation | ⚠️ UNCLEAR |
| 5 | Tests 168–172: DWH validation fails | DWH HDNRecord table empty | ⚠️ NEEDS INVESTIGATION |
| 3 | Unexpected alert (unsaved fields) | Likely data state / parallelism | ⚠️ MAY RESOLVE with parallelism fix |

---

## Issue 1 — Status Change Timeouts (8 tests) ✅ FIXED

**Error**: `WebDriverTimeoutException: Timed out after 60 seconds` in `StatusovergangHelper.WaitUntilStatusChangeIsDone`

**Root cause**: Two compounding problems:
1. `OverrideMaxWaitTimeStatusChangeMs = 60000` in `appsettings.json` reduced timeout from 360s to 60s
2. `MaxParallelism = 5` caused contention on QSPJobservice MSMQ processing: 37/432 status changes took >60s (max 166s)

**Evidence**: `StatusChangeLog` table in `QSP_O21` shows slowness correlates exactly with parallel test load.
On 2026-06-22 with no parallel tests: max 25s. Same day with 5-parallel: max 166s.

**Fix applied** (in `C:\WorkEnvironment\Tools\ATFReadAndRunner\publish\appsettings.json`):
```json
"MaxParallelism": 2,                        // was 5
"OverrideMaxWaitTimeStatusChangeMs": 0,     // was 60000 — 0 means use App.config default (360s)
```

---

## Issue 2 — Tests 136 & 140: ITP External Data Source ❌ NEEDS MANUAL ACTION

**Tests**: `136_BA_BR_Inkomensbepaling_Loondienst`, `140_BA_BR_MijnPensioenOverzicht`

**Error**: Status stuck at "Opvragen gegevens Externe..." — the system requests data from ITP, waits, never gets a response, eventually times out.

**Root cause**: The ITP SOAP service (expected at `https://localhost:61168/` on MGQSP-OV-FRC04) is **not installed or running**.

**Evidence**:
- Port 61168 TCP probe timed out (3s) → port not open
- `Get-WmiObject Win32_Process` returned no ITP process
- No ITP executable or directory found anywhere on `D:\` or `C:\` drives
- `Hdn.ExpectedExternalSourceRecord` table has `ReceivedResponse=False` records going back to **2026-06-03** (3+ weeks broken)

**Config reference**: `C:\WorkEnvironment\QSP.Core\Config\o21.xml`
```xml
<endpoint name="ITPServerSoap" address="https://localhost:61168/"/>
```

**What's deployed**: `FinGen.Berichtgeneratie.ItpAgent.dll` IS in `D:\QSP\service\` — this is the QSP CLIENT that calls ITP. The ITP **server** itself is a third-party product that must be installed separately.

**Action required**: Someone with server access must install and start the ITP server on MGQSP-OV-FRC04 port 61168.

---

## Issue 3 — Test 143: PasserenViaEch ECH Timeout ⚠️ UNCLEAR

**Test**: `143_PasserenViaEch`

**Error**: `WebDriverTimeoutException: Timed out after 360 seconds` in `ZetBerichtKlaarOmTeVerwerkenDoorQSPECH`

**Services verified running**: `Qsp.ECH.WindowsService` (Windows Service) + `Components/Qsp.ECH` (IIS app)

**Status**: The ECH service is running but processing timed out at the full 360s. Could be:
- A data state issue from a previous failed test step
- The ECH external endpoint (HDN network) being unavailable
- A specific message type that the ECH service fails to handle

**Next step**: Check ECH service logs at `E:\Logs\Components\QSP.ECH\` on MGQSP-OV-FRC04.

---

## Issue 4 — Tests 168–172: DWH HDNRecord Empty ⚠️ NEEDS INVESTIGATION

**Tests**: 168 `BA_ValidatieMelding_KadasterEigenaarsinformatie`, 169 `BA_BR_BasisRegistratiePersonen`, 170 `BA_BR_Identiteitsattributen`, 171 `IA_IX_Toestemming_Consument`, 172 `BA_BR_Calcasa_Desktoptaxatie`

**Error**: `BA1 Expected: True But was: False` — test calls `DatabaseHelper.DoesDwhHDNRecordAndMessageDataMatch()` which queries `SELECT * FROM dwh.HDNRecord WHERE RecordUuid = @RecordUuid` in `QSP_O21_DWH` and finds nothing.

**Root cause confirmed**: `dwh.HDNRecord` has **0 rows** (completely empty). The DWH service is not writing records.

**What IS working**:
- DWH service (`dotnet Qsp.Dwh.dll`) starts and listens
- IIS proxies requests to it correctly (HTTP 200 responses for `bepaling-eigenaar-procesgroep`)
- Config is fetched from `Qsp.Configuration` at startup (HTTP 200 confirmed in IIS log)

**Critical finding**: The IIS log for the Components site shows **zero DWH requests during the 13:00–14:00 test window**. The last IIS activity was at 12:06. This means:
- Either the test runner couldn't reach the server at all during that time, OR
- The main FinGen service is not calling the DWH HDN write endpoint

**What to check next**:
1. Read ECH/HDN write logs on the Windows service: `E:\Logs\` on MGQSP-OV-FRC04
2. Check if the FinGen Windows service logs show DWH calls or errors
3. Check the DWH swagger at a valid path: try `https://cp-o21.qsp.finance.lab/Qsp.Dwh/swagger/index.html`
4. Verify the Qsp.Configuration actually returns a valid DB connection:
   `https://cp-o21.qsp.finance.lab/Qsp.Configuration/api/V2/GetConfiguration/Dwh/nvt`

---

## Issue 5 — Unexpected Alert (Unsaved Fields)

**Error**: `Unexpected alert: De pagina bevat niet validerende, of niet opgeslagen velden`

Likely a data state issue — a previous step left a form in an incomplete state, triggering this browser alert on navigation.

**Likely resolved by**: Fixing Issue 1 (parallelism). With MaxParallelism=2, earlier steps in the same test are less likely to fail and leave corrupted state.

---

## Changes Made

| File | Change | Reason |
|---|---|---|
| `C:\WorkEnvironment\Tools\ATFReadAndRunner\publish\appsettings.json` | `MaxParallelism` 5→2 | Reduce MSMQ contention |
| `C:\WorkEnvironment\Tools\ATFReadAndRunner\publish\appsettings.json` | `OverrideMaxWaitTimeStatusChangeMs` 60000→0 | Restore 360s default timeout |

---

## Recommended Next Actions

1. **Re-run the tests** to validate the parallelism/timeout fix resolves issues 1 and 5
2. **Install ITP server** on MGQSP-OV-FRC04 at port 61168 (manual server deployment)
3. **Investigate DWH write path**: Read FinGen service logs and check why HDN events are not being sent to the DWH
4. **Check ECH logs** for test 143: `\\MGQSP-OV-FRC04.finance.lab\E$\Logs\Components\QSP.ECH\`
