# lib/sqlite3mc_x64.dll

SQLite3 Multiple Ciphers (SQLite3MC) — SQLite with pluggable encryption ciphers.
The game's `Persistent/meta` database is encrypted with the SQLCipher-compatible
scheme (cipher 3), so the importer needs this library to read it.

- Upstream: https://github.com/utelle/SQLite3MultipleCiphers
- License: MIT (see the upstream repository)
- File: Windows x64 build, `sqlite3mc_x64.dll`

The csproj copies this file next to the executable, where the P/Invoke wrapper
(`Encryption/Sqlite3MC.cs`) looks for it by name.

`sqlite3mc_x64.so` is not shipped: the decryptor only targets Windows today.
On Linux, drop the corresponding shared object into the build output and adjust
the `DLL` constant in `Encryption/Sqlite3MC.cs` if you need it.
