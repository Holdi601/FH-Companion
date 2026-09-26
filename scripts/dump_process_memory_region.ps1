[CmdletBinding()]
param(
    [int] $ProcessId = 0,
    [string] $ProcessName = "forzahorizon6",
    [Parameter(Mandatory = $true)]
    [string] $Address,
    [Parameter(Mandatory = $true)]
    [int] $Length,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = "Stop"

if ($ProcessId -le 0) {
    $ProcessId = [int](
        Get-Process -Name $ProcessName -ErrorAction Stop |
            Select-Object -First 1 -ExpandProperty Id
    )
}
if ($Length -lt 1) {
    throw "Length must be positive."
}

$normalized = $Address.Trim()
if ($normalized.StartsWith("0x", [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalized = $normalized.Substring(2)
}
$addressValue = [Convert]::ToUInt64($normalized, 16)

if (-not ("ForzaMemoryReader" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class ForzaMemoryReader
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        UIntPtr nSize,
        out UIntPtr lpNumberOfBytesRead
    );

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    public static byte[] Read(int processId, ulong address, int length)
    {
        IntPtr process = OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
            false,
            processId
        );
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess failed");
        try
        {
            byte[] bytes = new byte[length];
            UIntPtr bytesRead;
            bool ok = ReadProcessMemory(
                process,
                new IntPtr(unchecked((long)address)),
                bytes,
                (UIntPtr)length,
                out bytesRead
            );
            int actual = checked((int)bytesRead.ToUInt64());
            if (!ok && actual == 0)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "ReadProcessMemory failed"
                );
            if (actual == bytes.Length)
                return bytes;
            byte[] truncated = new byte[actual];
            Buffer.BlockCopy(bytes, 0, truncated, 0, actual);
            return truncated;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
'@
}

$bytes = [ForzaMemoryReader]::Read($ProcessId, $addressValue, $Length)
$parent = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
}
[System.IO.File]::WriteAllBytes($OutputPath, $bytes)

[pscustomobject]@{
    process_id = $ProcessId
    address = ("0x{0:x16}" -f $addressValue)
    requested_length = $Length
    written_length = $bytes.Length
    output_path = $OutputPath
} | ConvertTo-Json
