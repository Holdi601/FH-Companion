# Network Probe

This workflow records the VM network adapter passively while Forza navigates or captures a Rivals leaderboard.

It does not modify Forza, inject code, bypass TLS, or change the number of leaderboard requests.

## Capture

From the host:

```powershell
cd <repo>

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture_vm_network_probe.ps1 `
  -DurationSeconds 120
```

The active `ForzaRankScan_*` task is detected automatically. Pass `-RunId` when probing a specific run.

Outputs:

```text
data\network_probes\<timestamp>_active_scroll\forza_scroll.etl
data\network_probes\<timestamp>_active_scroll\forza_scroll.pcapng
data\network_probes\<timestamp>_active_scroll\timeline.jsonl
data\network_probes\<timestamp>_active_scroll\flows.csv
data\network_probes\<timestamp>_active_scroll\per_second.csv
data\network_probes\<timestamp>_active_scroll\report.json
```

## Analyze Existing Capture

```powershell
python .\scripts\analyze_network_probe.py `
  --probe-dir ".\data\network_probes\<probe>"
```

Install the free parser dependency if necessary:

```powershell
python -m pip install -r .\requirements-network.txt
```

## Interpretation

The current FH6 build loads:

- `libHttpClient.dll`
- `WINHTTP.dll`
- `Secur32.dll`
- `schannel.dll`

Observed leaderboard traffic is TLS application data over TCP 443. Packet capture can identify endpoints, request timing, record sizes, and response volume, but cannot expose leaderboard rows without session secrets or access to plaintext at the HTTP API boundary.

An `InternetClient` ETW trace identified the concrete leaderboard call:

```text
POST https://gameservices.fh6.forzamotorsport.net/Services/o.xtsw
Content-Type: bin/xtsw
X-ClassName: Forza.WebServices.Scoreboard, Forza.WebServices
X-Method: GetRows
```

The WebIO provider also records the request and response bodies at the HTTP boundary. Analyze an existing converted trace without copying authorization headers into reports:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture_vm_http_trace.ps1 `
  -DurationSeconds 30
```

This disables packet capture and records only the `InternetClient` ETW scenario to keep overhead lower than a full packet trace. It automatically detects the active rank-scan run, samples its rank into `rank_timeline.jsonl`, and runs the sanitized parser after conversion.

Analyze an existing converted trace:

```powershell
python .\scripts\analyze_winhttp_trace.py `
  --trace-xml ".\data\network_probes\<probe>\internetclient_trace.xml" `
  --output-dir ".\data\network_probes\<probe>\winhttp_analysis" `
  --process-id <FORZA_PID>
```

The parser reconstructs the binary bodies under `http_bodies`, correlates each request with the nearest sampled leaderboard rank, writes sanitized JSON/CSV metadata, and never exports authorization, entity-token, signature, validation, user-ID, or request-ID headers.

Raw `InternetClient` ETL/XML traces can contain live account/session credentials. Keep them private and never commit or publish them.

The current experimental next steps are:

1. Compare navigation traffic with stable 11-row capture traffic.
2. Determine whether `bin/xtsw` is serialized/compressed or protected again at the application layer.
3. Correlate request-body changes with selected rank and returned row ranges.
4. Avoid replaying requests until the body format, signature requirements, request rate, and account risk are understood.

## Capture An Entire Rank Scan

Use the provider-only watcher to preserve every WebIO request/response emitted during a rank scan:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture_vm_scoreboard_run.ps1 `
  -RunId "<rank-scan-run-id>"
```

For a background watcher, start the command with `Start-Process` and redirect its output to a private log. The watcher samples rank state, stops when the scan task finishes, converts the ETL, and runs the sanitized analyzer. Its raw ETL/XML output can contain live session headers.
