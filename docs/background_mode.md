# True Background Mode

The idle mode is only a convenience layer. True background operation needs Forza, the input automation, and screenshots to run in a separate interactive desktop from your normal Windows desktop.

## Recommended Path: Windows VM With GPU-P

Most practical target:

1. Host Windows stays usable for normal work.
2. A Windows guest VM runs Steam, Forza, and this scraper.
3. The guest VM gets GPU acceleration through Hyper-V GPU partitioning (GPU-P) or another working GPU passthrough setup.
4. The automation runs inside the guest, so foreground focus and keyboard input are guest-local and do not steal your host desktop.

This is the highest-value experiment because it keeps the current OCR/screenshot pipeline and removes the foreground-input conflict.

Run the preflight once from a normal shell:

```powershell
scripts\check_background_options.ps1
```

Run it once from an elevated PowerShell for the full Hyper-V/GPU-P result:

```powershell
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File "<repo>\scripts\check_background_options.ps1"'
```

The report is written here:

```text
<repo>\data\background\background_options.json
```

What we need to see:

- Hyper-V PowerShell cmdlets available.
- Hyper-V optional features enabled or enable-able.
- At least one partitionable GPU from `Get-VMHostPartitionableGpu`.
- A guest desktop that can stay logged in and render Forza while the host desktop remains free.

On this machine, the elevated report currently shows two partitionable GPU-P devices: the AMD integrated graphics and the NVIDIA RTX device. No Hyper-V VMs exist yet.

Important: the automation still needs an unlocked, active desktop inside the VM. The host can be used normally, but the guest must not be locked or minimized into a non-rendering state.

## Create The GPU-P VM

First get a Windows ISO. The scraper does not currently find one in `Downloads` or `Documents`.

Then preview the VM plan:

```powershell
scripts\new_forza_gpu_vm.ps1 `
  -IsoPath "C:\Path\To\Windows.iso" `
  -PlanOnly
```

Create it from an elevated PowerShell:

```powershell
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File "<repo>\scripts\new_forza_gpu_vm.ps1" -IsoPath "C:\Path\To\Windows.iso" -VMName "ForzaScrapeVM" -MemoryGB 16 -CpuCount 12 -DiskGB 256 -EnableTpm -StartAfterCreate'
```

Defaults:

- Uses Hyper-V Generation 2.
- Uses the `Default Switch`.
- Uses the NVIDIA GPU-P device by matching `VEN_10DE`.
- Creates the VM under `D:\HyperV\ForzaScrapeVM` when `D:\` exists, otherwise `C:\HyperV\ForzaScrapeVM`.

After Windows is installed in the guest:

1. Install current GPU drivers in the guest if Device Manager does not show the virtual GPU cleanly.
2. Install Steam and Forza.
3. Copy or clone this project into the guest.
4. Install the Python/OCR dependencies inside the guest.
5. Run the scanner inside the guest desktop.

Current VM status:

- VM name: `ForzaScrapeVM`
- ISO: `H:\WinImage\Windows10.iso`
- VHD: `D:\HyperV\ForzaScrapeVM\ForzaScrapeVM.vhdx`
- GPU-P: NVIDIA device matched by `VEN_10DE`
- Memory: 16 GB
- CPU count: 12

## Screenshot-Only VM, Host-Side OCR

The host share is already configured:

```text
\\<host-pc>\ForzaCapture
\\172.17.128.1\ForzaCapture
```

Host path:

```text
<repo>\data\vm_share
```

In the VM, map it as `Z:`:

```powershell
net use Z: \\172.17.128.1\ForzaCapture /user:<host-pc>\<user>
```

Use the host account password when Windows asks for credentials.

The previous interrupted run has been prepared for VM resume here:

```text
<repo>\data\vm_share\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json
```

Inside the VM, resume capture-only mode:

```powershell
scripts\run_leaderboard_rank_scan.ps1 `
  -ResumeFrom "Z:\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json" `
  -SkipExtract
```

On the host, process chunks as they appear:

```powershell
scripts\run_host_extract_from_vm_captures.ps1 `
  -StatePath "<repo>\data\vm_share\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json" `
  -Watch
```

Host output:

```text
<repo>\data\processed\vm_rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\leaderboard_entries.parquet
```

Note: the old pre-VM processed data is incomplete; the current merged host-side parquet has only about 991 complete ranks out of the old scanned window. That is independent from the VM setup. Later we should rescan or retry the missing old ranks.

## Resume Data In A VM

Your current run state is host-path based:

```text
<repo>\data\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json
```

If the VM uses the same path, resume works directly inside the guest.

If the VM uses a different workspace path, the `state.json` paths need to be rebased before resuming. The safe approach is to copy the run folder and processed folder into the VM workspace, then rewrite absolute paths in a copied state file.

Example inside the VM:

```powershell
scripts\rebase_rank_scan_state.ps1 `
  -StatePath "<repo>\data\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.json" `
  -OldWorkspaceRoot "<repo>" `
  -NewWorkspaceRoot "D:\Forza"
```

Then resume with the generated `state.rebased.json`:

```powershell
scripts\run_leaderboard_rank_scan.ps1 `
  -ResumeFrom "D:\Forza\data\rank_scans\20260609_170935_977_highway_circuit_d_09fcb916\state.rebased.json"
```

## Network/VPN Path

A VPN/proxy can help observe metadata:

- DNS names and IPs contacted by Forza/Xbox services.
- Request timing and traffic volume around leaderboard loads.
- Whether there appears to be a stable service endpoint worth investigating.

It does not automatically provide leaderboard rows. Game traffic is likely TLS-encrypted and may also use certificate pinning or in-process game protocols. If an official or plain API endpoint is discovered, we can use it. If the only route is breaking TLS, bypassing pinning, injecting into the game, or reading decrypted memory, that is not a good path for this project because it is account-risky and ToS/anti-cheat sensitive.

So the network route is worth using as a discovery tool, while the VM route is the practical route for reliable background scraping.

## Current Best Next Step

1. Run `scripts\check_background_options.ps1` as Administrator.
2. Open or paste `data\background\background_options.json`.
3. If GPU-P looks available, set up a Windows VM and install Steam/Forza plus this repo inside the guest.
4. Run the rank scan inside the guest.

Once the VM route is confirmed, idle mode becomes optional; the guest can own its foreground window while your host PC remains usable.
