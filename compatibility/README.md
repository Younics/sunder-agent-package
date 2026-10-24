# Current Agent compatibility inputs

`agent-current-inventory.json` is the independently reviewed expectation for the
current product: **15 package/project pairs, 12 App / 15 Worker roles, 18 public
APIs, 13 package-private APIs and 64 providers**. Runtime identities use Younics,
including `younics.sunder.package.maker`; authored C# names remain stable.
`packages.json` owns the reviewed release titles, artifact names and capability sets.

Public APIs started at `1.0.0`; `chat-provider` and `execution-target` have locally
reviewed `2.0.0` successors, retaining their frozen `1.0.0` records. Per-contract
inventory overrides express this without changing the other sixteen APIs.
Package/private versions are `2.0.0`. The external
`sunder.stack.contributor` API is owned by platform `sunder.core`, version `2.0.0`.
Workspace path migration is absent from the current API and provider inventory.

## Current contract byte records

`agent-current-contracts.json` is the separately reviewed current input. The initial
record was qualified from current source, normal Core public Accept/Freeze,
dependency selection and exact-RID artifacts. It contains all 18 public and 13
private descriptors, bound to the independent inventory. Public owned history started
empty and now retains the two evolved APIs' frozen `1.0.0` records; private-only
packages have no owned baseline file. The local record does not
claim publication or SDK freeze: the SDK ledger remains UNFROZEN / UNRELEASED.

Normal tests never refresh these expected bytes. Future changes require current
source/intent review and the normal owning lifecycle; old 19-API receipts cannot
qualify the current inventory.

The read-only verifier checks current qualified artifacts against the independent
inventory. `--propose` prints candidate records without writing expectations:

```sh
python3 -B scripts/test/verify-current-contracts.py --source-only
python3 -B scripts/test/verify-current-contracts.fixture.py
python3 -B scripts/test/verify-current-contracts.py --evidence artifacts/current/inputs.json --propose
python3 -B scripts/test/verify-current-contracts.py --evidence artifacts/current/inputs.json
```

Review method/value shapes, authority, stream kinds, policies and source intent
before accepting candidate bytes. A generator output is an observation, not its
own expected result. See [current test baseline](../docs/current-test-baseline.md)
for the input format, coverage transfer and qualification prerequisites.

Current public owned-baseline evolution, projection reviews and generic SDK future
release compatibility remain supported. Local unpublished selections use exact
candidates/catalogs; published selections use an explicit pinned Registry.
Native/cold/artifact checks and the required all-family behavioral gate remain
mandatory in their owning lanes. The Protocol boundary check enforces absence of
dependencies/artifacts; remote feed absence is not a release prerequisite.

## Inert protected history

These files retain their original bytes and identities solely as historical data:

- `agent-compatibility-candidate.json`, `agent-rpc-migration-baseline.json`,
  `agent-release-evidence.json`;
- `agent-service-public-successors.json`, `agent-ordinary-private-successors.json`
  (the former 19-public/13-private checkpoint);
- `identity-migration/v1/` captured maps, manifests, inverse data and proof records;
- `identity-migration/v1/fork-receipts/` (byte-identical receipts relocated from
  `src/<project>/sunder-identity-fork.receipt.json`);
- `tests/Sunder.Package.Agent.Tests/Fixtures/PreCanonicalProjects/`.

Historical documentation inside those captures describes its original checkpoint.
Its reconstruction/capture executables have been removed. Normal tests, builds,
product discovery and release neither reconstruct nor consume these captures.
Their preservation does not authorize a pre-V1 reader or migration policy override.
