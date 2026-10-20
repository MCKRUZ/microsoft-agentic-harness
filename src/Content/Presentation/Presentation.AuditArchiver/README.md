# Audit archiver

A small command-line job that moves finished audit log day-files into write-once storage and pins each log's newest
record there, so the application's own account cannot rewrite history it has already written (#416).

## Why it exists

Every audit log (governance, escalations, changes, egress, drift) is a hash chain stored one file per UTC day. The
hashes have no secret key. Anyone who controls the folder, including someone who has taken over the application's
account, can rewrite the records and recompute every hash, and can rewrite the application's own "last verified"
checkpoint to match. The application cannot catch that by itself.

The archiver runs **under a different identity, with its own configuration**, and does three things the application's
account cannot undo:

1. **Witness.** Every 15 to 60 minutes it reads each log's live chain and writes the newest record's sequence and hash
   into the archive as a *witness*. The archive is write-once, so a witness can never be changed. From then on, both
   the archiver and the application require the chain to still hold that hash at that sequence. Because every hash
   covers the record before it, one witness pins everything up to it.
2. **Archive.** It copies a finished day into the archive only if every record in it continues the archive's newest
   record and matches every witness. A deleted or rewritten day stops the run for that log; nothing is sealed over a gap.
3. **Verify.** Daily, it walks each log end to end, under its own identity.

What stays exposed: records written since the last witness. Run `witness` as often as that window should be small.

## Commands and exit codes

```
audit-archiver init                    # once, after the archive exists: write its marker file
audit-archiver witness                 # every 15-60 minutes
audit-archiver run                     # hourly or daily: witness, delete archived live copies, archive finished days
audit-archiver verify                  # daily: every log end to end
audit-archiver accept <log> <file> <sha256> <reason...>
    [--config <file>] [--timeout-minutes <n>]   # default timeout 30 minutes
```

| Exit | Meaning | Action |
|---|---|---|
| 0 | Every log done | none |
| 1 | A log **stopped** (gap, rewrite, changed copy, contradicted witness) or the command timed out | **page someone** |
| 2 | Bad command line or configuration | fix the job |
| 3 | Archive not ready (marker missing: not mounted, or `init` never run) | fix the mount |

Also alert, from outside the application, when the archiver's last success or the newest witness is older than you
allow ("dead-man" alert). The application cannot raise that alert itself.

## Configuration

Read from `appsettings.json` beside the program, then `--config`, then environment variables
(`AppConfig__AI__Audit__...`). Use the same audit storage paths the application uses.

| Key | Meaning |
|---|---|
| `AppConfig:AI:Audit:Archiver:BlobContainerUri` | The archive container, written with the archiver's managed identity. |
| `AppConfig:AI:Audit:ArchivePath` | Without a container URI: the archive as a folder (tests and on-premises write-once volumes only). |
| `AppConfig:AI:Audit:Archiver:MinimumAgeDays` | A day-file is archived only when at least this many days old (default 2). |
| `AppConfig:AI:Audit:Archiver:DeleteDelay` | A live copy is deleted only once its archived copy is this old (default 15 min), longer than the app mount's listing cache. |
| `AppConfig:AI:Audit:Archiver:MaxFileMegabytes` | Largest file read (default 256). |

## Setting it up safely

Every one of these matters; if any is open, the guarantee is gone.

- **Archive storage:** an Azure Blob container with a **locked** time-based retention policy at least as long as your
  audit retention requirement, protected append writes off. Turn off shared-key access on the storage account.
- **Archiver identity:** its own managed identity or service account with Storage Blob Data Contributor on that
  container only. Run it as a **separate job or pod**, never a sidecar, WebJob or node identity, which share the
  application's credentials.
- **Application identity:** read-only access to the container (Storage Blob Data Reader, or a read-and-list
  user-delegation SAS), no control-plane role on the storage account (it could read the keys), and the container
  mounted read-only at `AppConfig:AI:Audit:ArchivePath` (for example `blobfuse2 mount --read-only`) with listing and
  attribute caching off or short. The application checks every pass that it cannot write there and that the marker
  file is present, and stops ("not verified") otherwise; those checks catch a misconfigured mount, not an attacker.
- **Live folders:** the archiver reads and deletes the application's day-files and takes each log's lock file. Run it as
  the application's own operating-system user in its separate workload (on Linux the folders are owner-only); on
  Windows give its account Modify on the audit folders. It creates nothing in them.
- **Program and configuration:** keep both where the application cannot write them.
- **Rolling out:** start the archiver only after every application host runs this version with the same
  `ArchivePath`. A host without it would start the chain again from the beginning.

## When a run stops

Read the reason in the log (also in the exit summary). A gap, a rewrite or a contradicted witness means records were
removed or changed: treat it as an incident. A line torn by a crash mid-write (a day that does not end with a whole
record) is honest but cannot be repaired. Inspect the file, then let exactly those bytes through:

```
sha256sum governance/2026-10-20.jsonl        # or Get-FileHash -Algorithm SHA256
audit-archiver accept governance 2026-10-20.jsonl <sha256> "writer crashed at 02:14, see incident 123"
```

The acceptance is stored in the archive; within that file only, lines that do not chain are stepped over. If the file
changes afterwards, the acceptance no longer applies.

## What the application sees

`AuditChainVerificationService` and the compliance report verify each log from where its live files continue the
archive ("From sequence" in the report), against the archiver's newest witness, and walk back into the archive when
their last checkpoint lies there. Readers serve archived and live records together.
