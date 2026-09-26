# Licence texts of the bundled components

The app download ships these next to the program, because MIT and Apache-2.0
require the licence text to travel with the binaries. Which component uses which
text is listed in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

| File | Covers |
|---|---|
| `dotnet-runtime-MIT.txt` | .NET runtime and Windows Desktop runtime, Microsoft.Data.Sqlite, System.Memory (MIT, .NET Foundation and Contributors) |
| `dotnet-runtime-THIRD-PARTY-NOTICES.txt`, `dotnet-winforms-THIRD-PARTY-NOTICES.txt` | Code inside the .NET runtime and Windows Forms that comes from third parties |
| `Apache-2.0.txt` | HidSharp (James F. Bellinger), SQLitePCLRaw (SourceGear) |
| `SDL-zlib.txt` | SDL3 (Sam Lantinga) |

SQLite itself is in the public domain. The DualSense trigger code adapted from
John "Nielk1" Klein is MIT; his notice is in THIRD_PARTY_NOTICES.md and in the
source file. The web fonts used by the website carry their own OFL texts in
`server/fonts/`.
