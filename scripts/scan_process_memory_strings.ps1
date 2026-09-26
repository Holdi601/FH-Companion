[CmdletBinding()]
param(
    [int] $ProcessId = 0,
    [string] $ProcessName = "forzahorizon6",
    [Parameter(Mandatory = $true)]
    [string[]] $Strings,
    [int] $MaxMatchesPerString = 32,
    [int] $ChunkSizeMB = 4,
    [string] $OutputPath = ""
)

$ErrorActionPreference = "Stop"

if ($ProcessId -le 0) {
    $process = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
    $ProcessId = [int]$process.Id
}
if ($ChunkSizeMB -lt 1 -or $MaxMatchesPerString -lt 1) {
    throw "ChunkSizeMB and MaxMatchesPerString must be positive."
}

if (-not ("ForzaMemoryScanner" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public sealed class ForzaMemoryMatch
{
    public int ProcessId { get; set; }
    public string Text { get; set; }
    public string Encoding { get; set; }
    public ulong Address { get; set; }
    public ulong RegionBase { get; set; }
    public ulong RegionSize { get; set; }
    public uint Protect { get; set; }
    public uint Type { get; set; }
}

public static class ForzaMemoryScanner
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private sealed class Pattern
    {
        public string Text;
        public string Encoding;
        public byte[] Bytes;
        public int Matches;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        UIntPtr dwLength
    );

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static IEnumerable<int> FindAll(byte[] haystack, int length, byte[] needle)
    {
        if (needle.Length == 0 || length < needle.Length)
            yield break;
        byte first = needle[0];
        int limit = length - needle.Length;
        for (int i = 0; i <= limit; i++)
        {
            if (haystack[i] != first)
                continue;
            int j = 1;
            while (j < needle.Length && haystack[i + j] == needle[j])
                j++;
            if (j == needle.Length)
                yield return i;
        }
    }

    private static bool IsReadable(MEMORY_BASIC_INFORMATION info)
    {
        return info.State == MEM_COMMIT
            && (info.Protect & PAGE_NOACCESS) == 0
            && (info.Protect & PAGE_GUARD) == 0;
    }

    public static List<ForzaMemoryMatch> Scan(
        int processId,
        string[] strings,
        int maxMatchesPerString,
        int chunkSize
    )
    {
        var patterns = new List<Pattern>();
        foreach (string value in strings)
        {
            if (String.IsNullOrEmpty(value))
                continue;
            patterns.Add(new Pattern {
                Text = value,
                Encoding = "utf8",
                Bytes = Encoding.UTF8.GetBytes(value),
            });
            patterns.Add(new Pattern {
                Text = value,
                Encoding = "utf16le",
                Bytes = Encoding.Unicode.GetBytes(value),
            });
        }
        if (patterns.Count == 0)
            return new List<ForzaMemoryMatch>();

        int overlap = 0;
        foreach (Pattern pattern in patterns)
            overlap = Math.Max(overlap, pattern.Bytes.Length - 1);

        IntPtr process = OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
            false,
            processId
        );
        if (process == IntPtr.Zero)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "OpenProcess failed for PID " + processId
            );

        var results = new List<ForzaMemoryMatch>();
        try
        {
            ulong address = 0;
            ulong maximumAddress = Environment.Is64BitProcess
                ? 0x00007FFFFFFFFFFFUL
                : 0x7FFFFFFFUL;
            UIntPtr mbiSize = (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

            while (address < maximumAddress)
            {
                MEMORY_BASIC_INFORMATION info;
                UIntPtr queried = VirtualQueryEx(
                    process,
                    new IntPtr(unchecked((long)address)),
                    out info,
                    mbiSize
                );
                if (queried == UIntPtr.Zero)
                    break;

                ulong regionBase = unchecked((ulong)info.BaseAddress.ToInt64());
                ulong regionSize = info.RegionSize.ToUInt64();
                ulong nextAddress = regionBase + regionSize;
                if (nextAddress <= address)
                    break;

                if (IsReadable(info) && regionSize > 0)
                {
                    ulong regionOffset = 0;
                    byte[] previous = new byte[0];
                    while (regionOffset < regionSize)
                    {
                        int requested = (int)Math.Min(
                            (ulong)chunkSize,
                            regionSize - regionOffset
                        );
                        byte[] current = new byte[previous.Length + requested];
                        if (previous.Length > 0)
                            Buffer.BlockCopy(previous, 0, current, 0, previous.Length);

                        UIntPtr bytesRead;
                        bool read = ReadProcessMemory(
                            process,
                            new IntPtr(unchecked((long)(regionBase + regionOffset))),
                            current,
                            (UIntPtr)requested,
                            out bytesRead
                        );
                        int actual = (int)Math.Min((ulong)requested, bytesRead.ToUInt64());
                        if (read || actual > 0)
                        {
                            int available = previous.Length + actual;
                            foreach (Pattern pattern in patterns)
                            {
                                if (pattern.Matches >= maxMatchesPerString)
                                    continue;
                                foreach (int index in FindAll(current, available, pattern.Bytes))
                                {
                                    ulong matchAddress =
                                        regionBase + regionOffset
                                        - (ulong)previous.Length
                                        + (ulong)index;
                                    results.Add(new ForzaMemoryMatch {
                                        ProcessId = processId,
                                        Text = pattern.Text,
                                        Encoding = pattern.Encoding,
                                        Address = matchAddress,
                                        RegionBase = regionBase,
                                        RegionSize = regionSize,
                                        Protect = info.Protect,
                                        Type = info.Type,
                                    });
                                    pattern.Matches++;
                                    if (pattern.Matches >= maxMatchesPerString)
                                        break;
                                }
                            }

                            int keep = Math.Min(overlap, available);
                            previous = new byte[keep];
                            if (keep > 0)
                                Buffer.BlockCopy(current, available - keep, previous, 0, keep);
                        }
                        else
                        {
                            previous = new byte[0];
                        }
                        regionOffset += (ulong)requested;
                    }
                }
                address = nextAddress;
            }
        }
        finally
        {
            CloseHandle(process);
        }
        return results;
    }
}
'@
}

$matches = [ForzaMemoryScanner]::Scan(
    $ProcessId,
    $Strings,
    $MaxMatchesPerString,
    ($ChunkSizeMB * 1MB)
)

$report = [ordered]@{
    captured_at = (Get-Date).ToUniversalTime().ToString("o")
    process_id = $ProcessId
    searched_strings = @($Strings)
    match_count = @($matches).Count
    matches = @(
        $matches |
            Sort-Object Text, Encoding, Address |
            ForEach-Object {
                [ordered]@{
                    text = $_.Text
                    encoding = $_.Encoding
                    address = ("0x{0:x16}" -f $_.Address)
                    region_base = ("0x{0:x16}" -f $_.RegionBase)
                    region_size = [uint64]$_.RegionSize
                    protect = ("0x{0:x8}" -f $_.Protect)
                    type = ("0x{0:x8}" -f $_.Type)
                }
            }
    )
}

$json = $report | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $parent = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8
    Write-Host "[memory-scan] wrote $OutputPath"
}
$json
