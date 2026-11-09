# Uma Musume: Load SQL Data (BV fork)

Loads the game's `master.mdb` (plain SQLite) and `meta` (**encrypted** SQLite) into
MySQL / MariaDB.

Fork of [SimpleSandman/UmaMusumeLoadSqlData](https://github.com/SimpleSandman/UmaMusumeLoadSqlData)
(the upstream repository was retired in October 2025). This fork keeps the importer
working against current game builds.

## What this fork adds

| Change | Why |
|---|---|
| **Automatic meta decryption** | The game ships `Persistent/meta` encrypted with SQLite3 Multiple Ciphers (SQLCipher-compatible, cipher 3). The importer used to require a pre-decrypted file; now you can point it straight at the game folder and it decrypts first. |
| **Automatic table creation** | Upstream only *printed* `CREATE TABLE` statements when the destination schema lagged behind. They are now translated from SQLite DDL to MariaDB DDL and executed, indexes included. |
| **Missing column repair** | Column diffing used the bare table name, so `meta` tables (`meta_a`, `meta_ab`, ...) always failed. The `meta_` prefix is now honoured, and new columns are added before the retry. |
| **Configurable input paths** | `--meta` / `--master` options and `UMA_META_PATH` / `UMA_MASTER_PATH` variables replace the hard-coded paths, so the same build runs on a workstation and on a server. |
| **Non-interactive friendly** | No `Console.ReadKey()` block when there is no console (scheduled task / CI). |

## Requirements

- .NET 8 runtime
- `sqlite3mc_x64.dll` — shipped in `lib/` and copied next to the executable by the csproj
- MySQL 5.7+/8+ or MariaDB 10.4+ with `utf8mb4`

## Usage

```cmd
:: decrypt the encrypted meta from the game folder, then import both databases
UmaMusumeLoadSqlData.exe --meta "C:\...\Persistent\meta" --master "C:\...\Persistent\master\master.mdb" --region jp --verbose
```

Connection string (one of):

```cmd
:: positional argument, upstream-compatible order
UmaMusumeLoadSqlData.exe Production SimpleSandman/UmaMusumeMasterMDB master "server=127.0.0.1;port=3306;database=umamusume;user=root;password=***;CharSet=utf8mb4"

:: or the environment variable
set MARIA_CONNECTION_STRING=server=127.0.0.1;port=3306;database=umamusume;user=root;password=***;CharSet=utf8mb4
UmaMusumeLoadSqlData.exe
```

Set the connection string to `N/A` to skip the SQL side (the tool then only reads the
SQLite files and prints the DDL).

### Options

| Option | Description |
|---|---|
| `--meta <path>` | Path to `meta` (encrypted or already decrypted). Falls back to `UMA_META_PATH`, then `%USERPROFILE%\AppData\LocalLow\Cygames\umamusume\meta` in `Development`, then `./meta`. |
| `--master <path>` | Path to `master.mdb`. Same fallback chain (`UMA_MASTER_PATH`, `.../master/master.mdb`, `./master.mdb`). |
| `--region jp\|global` | Selects the built-in database key. Default `jp`. |
| `--meta-key <hex>` | Overrides the built-in key when a game update rotates it. |
| `--decrypt-only` | Decrypt the meta and exit — useful to check a new game version before importing. |
| `--force-decrypt` | Re-decrypt even if a newer `meta.decrypted` already exists. |
| `--verbose` | Per-table row counts. |

Decrypted output is written next to the source as `meta.decrypted` and reused on the
next run as long as it is newer than the encrypted file.

### Where the key comes from

The key is a per-region 32/33-byte table XORed with a 13-byte base key, the same scheme
used by [UmaDecryptor](https://github.com/RanKaeder/UmaDecryptor) and UmaViewer. If a game
update changes it, pass the new one with `--meta-key` (hex) instead of editing the source.

## Original command line arguments

The upstream positional form still works:

```cmd
dotnet run "Development" "SimpleSandman/UmaMusumeMasterMDB" "master" "user id=;password=;host=;database=;character set=utf8mb4;AllowLoadLocalInfile=true"
```

- Environment — `Development` or `Production`
- Owner/Repo — upstream repository holding `master.mdb` (only used by the old download path)
- Repo branch
- MySQL connection string — `N/A` skips the import
- `verbose` (optional)

## Verification counts

After a full import on MariaDB with a current build:

```sql
SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'umamusume'; -- 691
SELECT COUNT(*) FROM meta_a;   -- ~374k
SELECT COUNT(*) FROM skill_data;
```

## License

Same as upstream (see `LICENSE`).

## Exporting resource keys (keys.txt)

While importing, the loader also dumps the game's per-resource key table to `keys.txt`
(`<hash><TAB><key>`, one per line, written next to the working directory by default).

This comes from the `e` column of the game's `meta`, which holds the key used to decrypt each
asset bundle. It is needed by tools that rebuild the database themselves: a rebuilt `meta` has the
`e` column but leaves it at `0`, so the keys have to be re-applied from an export like this one.

```
UmaMusumeLoadSqlData --meta <path>/meta --master <path>/master/master.mdb --keys-out keys.txt
```

Options:

| flag           | meaning                                                        |
| -------------- | -------------------------------------------------------------- |
| `--keys-out`   | where to write the file (default `keys.txt`)                     |
| `--no-keys`    | skip the export                                                 |
| `--decrypt-only` | decrypt (and export) without importing into MySQL             |

If the meta is already a rebuilt database (no `e` column), nothing is written and a note is printed.
