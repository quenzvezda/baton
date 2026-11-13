# Runbook: backup and restore

**Goal:** a backup that restores into an empty database with everything Baton needs — configuration, runs,
history partitions, the lease-token sequence and the Data Protection key ring that decrypts webhook secrets.
Verified by `G1BackupTests` (`dotnet test tests/Baton.Integration.Tests --filter Category=G1`).

## What a backup holds

Everything is in the one Baton database (ADR-0003). **Treat every backup as secret material:** it contains
`data_protection_keys` next to the protected webhook secrets in `automation_triggers`. Unless the key ring is
encrypted with a certificate (`Baton:DataProtection:CertificatePath`, G1 security review M3), anyone with the
backup can recover every webhook secret. With a certificate, keep the certificate backed up **separately**:
without it the restored key ring cannot be read and every webhook secret must be rotated.

## Take a backup

```bash
pg_dump --format=custom --file=baton-$(date +%Y%m%d-%H%M).dump "host=<host> dbname=baton user=<user>"
```

- `--format=custom` is compressed and restores selectively; it is a consistent snapshot taken without
  stopping Baton (single transaction, `REPEATABLE READ`).
- Run it daily at least; keep 90 days to match the history retention. Store it encrypted, off the database host.
- Locally: `docker compose exec postgres pg_dump -U baton -d baton --format=custom --file=/tmp/baton.dump`,
  then `docker compose cp postgres:/tmp/baton.dump .`.

## Restore

1. **Stop the writers**: orchestrators and the Control Plane API (agents may stay up; they reconnect).
2. **Restore into an empty database** — never over the live one:

   ```bash
   createdb -h <host> -U <user> baton_restored
   pg_restore -h <host> -U <user> -d baton_restored --no-owner --exit-on-error baton-YYYYMMDD-HHMM.dump
   ```

3. **Check it** (values against the backup's source, if it still exists):

   ```sql
   SELECT count(*) FROM workflow_runs;
   SELECT last_value FROM lease_token_seq;               -- fencing tokens continue from here
   SELECT ensure_history_partitions();                    -- 0 = the partitions are all there
   SELECT count(*) FROM data_protection_keys;             -- > 0, or webhook secrets are unreadable
   ```

4. **Switch over**: rename the databases (`ALTER DATABASE baton RENAME TO baton_broken;`,
   `ALTER DATABASE baton_restored RENAME TO baton;`) or point `ConnectionStrings__Baton` (and
   `ConnectionStrings__BatonListen`) at `baton_restored`.
5. **Start** the API (it registers the workflow definitions of its build; the restored database already has the schema) and the orchestrators.

## After a restore

- Runs and tasks that were active at backup time resume from that state: their leases have expired, so the
  reaper retries them — handlers run again (idempotency keys make that safe).
- Everything accepted after the backup is gone. Cron triggers catch up once (`fire_once`); webhook senders
  that retry re-create their runs; manual runs must be started again.
- Agents keep working: hosts and credentials are in the backup. Tokens issued after the backup are gone —
  reissue them (`POST /api/host-groups/{id}/agent-tokens`).

## Confirm

`/healthz` on every orchestrator is `ok`, a test webhook delivery is accepted (202), and the stuck-queue
queries show normal waits.
