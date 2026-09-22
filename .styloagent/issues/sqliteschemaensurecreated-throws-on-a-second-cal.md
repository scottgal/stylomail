**From:** adaptive-
**Timestamp:** 2026-09-22T05:39:05.4465400+01:00
**Severity:** medium
**Status:** open
**Source:** internal

# SqliteSchema.EnsureCreated throws on a second call against an existing WAL database

File: src/StyloMail.Persistence/SqliteSchema.cs (owned by another agent — not edited).

`schema_version` / `EnsureCreated` is documented as "Safe to call on every start", but the second call on an existing database throws:

  Microsoft.Data.Sqlite.SqliteException : SQLite Error 1: 'cannot change into wal mode from within a transaction'

Cause: the first statement in `EnsureCreated` is

  PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;

executed on a command whose `Transaction` is set to the surrounding `BeginTransaction()`. SQLite refuses to set `journal_mode` inside a transaction. The first call happens to succeed (the database is created in the same call); every subsequent call on an already-WAL database fails.

Impact: any component that initialises on start — which is the documented usage — gets an exception on its second run. I worked around it in `SqliteAdaptiveProfileStore.EnsureCreated` by checking `sqlite_master` for the `profiles` table first, but the fix belongs in Persistence.

Suggested fix: move the pragmas outside the transaction (or run them on a separate command before `BeginTransaction`), since `journal_mode` is a persistent database property and does not need to be transactional.
