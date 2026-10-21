# Local renamed-family qualification

The macOS `osx-arm64`, Debug/source-mode qualification completed using .NET SDK
**10.0.100**, Node **24.18.1**, and npm **11.19.0**. Package versions and private
contracts remain **2.0.0**; all 19 new public contract families are **1.0.0**;
platform `sunder.stack.contributor` remains **2.0.0**.

## Recorded results

Evidence root:
`artifacts/identity-migration/v1-20261014-qualification-01/`.

- `summary.json`: final measured inputs, archive identities, per-project results,
  capability review, protected-file proof and original failure/recovery commands.
  SHA-256: `39bef15e694b6b63ac2d47e1e32e13e124ede3837483c85168f0b832f7639b64`.
- `final-results-02-selection.json`: explicit selection of byte-identical raw TRX
  reports. Main's recovery partitions are disjoint; no TRX result was edited.
- `commands/final-metadata-gate-02.log`: the owning macOS inventory/skip gate,
  with **zero evidence violations** and all 18 skips matched by identity, owner
  and reason. Linux canonical identity hashes were not evaluated on macOS.
- `source-family-source-artifact-03/source-artifact-evidence.json`: the successful
  fresh all-family source-artifact case and its **15 retained archives**.
- `source-commands-source-artifact-03/`: actual command before/after records,
  logs and binlogs from that case.
- `commands/provider-fixture-01.request.json`: exact inputs/hashes supplied to
  `prepare-shared-provider-fixture.py`; preparation used the real new-owner
  archive and pinned local Core catalog, with owning Accept/Freeze/Add/Implement/
  Generate operations rather than authored receipts.

| Project suffix (`Sunder.Package.Agent.*`) | Passed | Failed | Expected skips |
| --- | ---: | ---: | ---: |
| `Execution.Local.Tests` | 287 | 0 | 12 |
| `Memory.Semantic.Tests` | 50 | 0 | 0 |
| `Presentation.State.Tests` | 38 | 0 | 0 |
| `Presentation.Tests` | 203 | 0 | 0 |
| `Provider.Anthropic.Tests` | 47 | 0 | 0 |
| `Provider.Gemini.Tests` | 48 | 0 | 0 |
| `Provider.LMStudio.Tests` | 47 | 0 | 0 |
| `Provider.OpenAI.Tests` | 237 | 0 | 0 |
| `Skills.Tests` | 61 | 0 | 0 |
| `Subagents.Tests` | 2 | 0 | 0 |
| `Tests` | 1,883 | 0 | 6 |
| **Total** | **2,903** | **0** | **18** |

The normal parallel Agent solution build passed with zero warnings/errors. A
final parallel build also passed after a retained Core stack-transport generator
process exit 139. The first build's missing default descriptor-cache inputs were
resolved by explicitly selecting the retained local cache. No serial MSBuild gate
or relaxed internal command watchdog was introduced.

## Identity-specific reader repairs

- Extended only the new source inverse to cover the existing historical readers'
  package inventory and relocated prompt-interface text. The inverse is
  `88e0b60f707203d43f581a22f51d84fcf3a882c972cbf38fafb338465e01ece3`.
  The prior inverse is retained under `inverse-before-reader-recovery-01/`.
- Main metadata is hashed after authenticated reconstruction. Historical
  dependencies use historical IDs; current selected dependencies are checked
  independently against the new owner.
- Owner, credential and OpenAI private-history tests load the SHA-bound archived
  baseline and old contract ID, then exercise current generated services separately.
- The executable App-view probe receives the explicit mapped package ID instead
  of deriving runtime identity from a CLR assembly name. It checks Maker's approved
  display name and the automatic main profile provider ID.
- Test project-suite IDs retain their original CLR/project names. The report gate
  projects **only** reviewed runtime-owner IDs when checking the retained skip-policy
  hash. Test identities, predicates, reasons and counts are unchanged by that
  projection; current expected and actual skips are still compared exactly.

The initial incomplete Main run, diagnostic aborts, failed historical joins and
App-probe attempts remain in `commands/` and `results/`. No counts were claimed
for the two-hour Main attempt that did not produce a final TRX. Its successful
recovery consists of the remaining 1,887 cases, the separate owner case and the
separate full-family artifact case. Internal 900-second command watchdogs remained
intact. The shorter diagnostic timers exposed a legitimate owner build lane;
that lane subsequently passed under its original command bounds.

## Artifact and preservation proof

All **15 fresh archives are byte-identical** to the original qualified archives
in `v1-20261014-local-01`. The source-artifact case verified 15 Runtime and 12 App
roles, all owned/imported policies, real consumer/provider selections and behavior.
The main archive remains:

```text
archive SHA-256: bfc3259bda5445f0358a27b89c1d6ec9da1d4ed83211613a9ddc993883d97b62
content identity: 05ebb9f77541e9a03960780abdd4ca71733f6091edbf1e02eb41efe1b42728f0
```

The summary contains every family archive SHA/content identity and the measured
current role/authoring receipt hashes. Current owned baselines and locks retain
their qualified bytes. Both current-successor JSON files retain their previously
generated SHA-256 values:

- Public: `769f4ac4038f4ed620bdf395e0909635abd2a5c3a8d3684438346ca8e6e7f67f`.
- Private: `f1ea85ce5d4609e4021538faad1f90ea8bc45a8b5f14f02db51a3b7931186f46`.

The original map, all 33 predecessor files, frozen historical fixtures, 19 old
current public tuples, 35 history tuples, retired-private evidence and staged
entries remain unchanged. The reviewed capability delta remains eight removed
authored declarations and five distinct removed inventory grants. The all-family
policy check verifies the current target capability sets. The final read-only
physical-residue check reports zero residue.

## Separate scopes

This is not qualification of Linux/Windows canonical identity lanes, a real Docker
daemon, released/native archives across all six RIDs, publication, or SDK freeze.
Those scopes retain their existing gates and requirements. No Registry/state reset,
publication, staging or commit was performed by this qualification.
