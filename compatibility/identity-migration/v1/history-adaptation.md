# Post-cutover historical-reader handoff

This adaptation consumes the original map and predecessor capture without changing
their bytes. `handoff.json` remains the **pre-edit** proof; it is not a claim that
the working tree still has its old identities.

The current successor generation is now complete from the actual frozen owning
baselines and receipts. [current-successor-proof.json](current-successor-proof.json)
binds the inputs, all 15 qualified archives/final metadata sets, and both output
records at that pre-qualification checkpoint. The later .NET qualification is
recorded in [qualification.md](qualification.md); the earlier proof remains exact.

The post-cutover check reports both raw index and staged-entry digests. The raw
index has changed since capture; the complete staged mode/blob/path entry
stream still matches the original capture (`be5f26ec14ebcffc3f24594a7f3cb72a0b230967563ca6d7499b79a7cc4a50ed`).
No staging or index restoration is part of this adaptation.

## Source inverse

`source-inverse.json` contains exact, line-oriented byte edits for changed
production `.cs`/`.axaml`, project/MSBuild inputs, and the test sources named by
existing frozen source-history fixtures. It excludes the historical reader
implementation itself, active owned baselines/locks/receipts, generated outputs,
and ordinary test sources which have no historical source obligation. It does
not create a blanket hash freeze on all ordinary tests.

`AgentSourceMigrationProjectChanges.IdentityPins.cs` pins this new inverse's
whole-file SHA. `IdentitySource.cs` authenticates each current file's **raw after
SHA**, checks each exact edit span, reconstructs the **raw captured before SHA**,
and only then normalizes text for the existing historical inverses. A second
reader can supply that exact reconstructed predecessor; it still authenticates
the live after-file, and arbitrary text is rejected. Unchanged production/MSBuild
inputs continue to be checked against the captured raw hash.

The order is identity inverse → existing `ApprovedImportedServiceNames` → the
existing unnamed-provider/domain/source-split/earlier inverses. None of the
existing 35 imported-name before/after pins changed. Direct current readers for
owned services, central-interface relocation, metadata, source-split additions,
domain-boundary verification and retained/canonical MSBuild inputs use the same
outer boundary. The retained-suite XML inverse explicitly marks its already
reconstructed identity boundary before applying older transforms.

The seven deleted files have explicit absence guards and authenticated before
text. Historical enumeration includes the six App `AssemblyInfo.cs` files and
the Subagents namespace marker as **data only**. No retired source is compiled or
restored to the tree. The preparer verifies that all affected families retain
their friend-declaration sets; project role/source declarations remain part of
the exact before/after proof.

Read-only checks:

```sh
python3 -B compatibility/identity-migration/v1/source_inverse.py check
python3 -B compatibility/identity-migration/v1/source_inverse.fixture.py
```

The approved Main-only `AgentTranscriptPageCursor.cs` global alias was already
included in the inverse. Its exact before/after hashes are
`91d06616db344f59ff889fb0a146974755431841a4493ae696d94a8714daaea4` and
`a3bf998de9cfe0da71e39b9e73e166a1a9227b89fa5ca6213c5b83dcfe7742ce`.
The alias is the only change at that boundary; its Shared role annotation remains
part of the authenticated before/after source. No additional re-pin was needed.
The pre-qualification inverse SHA was
`c4cb4ba1d22dfa39517a873a55af32404a660783d5e6941b0b53f000678e96b0`.
The qualified inverse SHA is now
`88e0b60f707203d43f581a22f51d84fcf3a882c972cbf38fafb338465e01ece3`:
it adds the captured `packages.json` and relocated test-only
`LocalProviderFakes/IAgentSystemPromptContributor.cs` inputs needed by existing
historical readers. Removing precisely those two records reconstructs the prior
inverse bytes/hash; all preceding records and original historical hashes remain
unchanged. The qualified reader hashes Main's reconstructed metadata and compares
historical dependency declarations to the historical owner, separately checking
current selected dependencies.

Any further source mismatch must be reported, not automatically approved. Only
after a new explicit review may the new inverse and its C# pin be regenerated:

```sh
python3 -B compatibility/identity-migration/v1/source_inverse.py inspect
python3 -B compatibility/identity-migration/v1/source_inverse.py write \
  --expected-inverse-sha256 <current-IdentitySourceInverseSha256>
python3 -B compatibility/identity-migration/v1/source_inverse.py check
```

The exact old temporary before-images are needed only for preparing/re-pinning,
not for subsequent C# reconstruction or the permanent inverse check. Preparation
refuses unknown additions/deletions and changed frozen history. The original map,
capture, old fixtures and old hashes are never regenerated.

## Historical/current joins

`AgentSourceMigrationProjectChanges.IdentityEvidence.cs` verifies the fixed map,
predecessor manifest, all 33 archived files, and the source manifest. It owns the
explicit 15-package current identity join (including Maker), 19 public / 13 private
contract joins, and the automatic provider join. `Name.ToLowerInvariant()` is now
only the historical package ID on `CanonicalProjectHistory`.

The old 31-contract oracle, 19 old current public tuples, 35 public history tuples,
and retired-private payloads remain old evidence. Current policies map identifiers
in memory and obtain descriptor bytes/hashes from current owning evidence; they
never relabel old hashes. Canonical closure checks read archived owned baselines
for historical assertions and separately qualify active new families. Archive
inventory checks use current successor records without requiring live `obj`
outputs just to load the inventory.

Capability review has two different counts:

- Eight authored `SunderSdkCapability` declarations were removed: Agent/MCP/
  Subagents `rpc.extended-deadline.v1`; Local's two explicit floors; and each Tools
  family's `logging.v1` declaration.
- Only five distinct current inventory grants disappear: Local's
  `background-services.v1` and `runtime-generations.v1`, and Files/Shell/Web
  `logging.v1`. The three extended-deadline capabilities remain inferred in the
  current inventory. `AssertCurrentInventory` applies that exact reviewed delta.

## Successor generation: completed from actual Core outputs

Core's `artifacts/identity-migration/v1-20261014-local-01/{handoff,validation}.json`
records 15 forks, 14 retargets and 15 current-RID archives. The final proof checks
all current owned/lock/fork-receipt bytes against the saved `final/<key>/` files,
the role/dev metadata hashes, every archive hash, and all 32 owned archive
descriptors against the actual authoring receipts. Subsequent Core implementation
changes are outside that recorded artifact evidence. The subsequent qualified
normal build, historical tests and fresh source-artifact lane are separate evidence.

The owning reconciler was run with this explicit complete-cutover mode:

```sh
# Read-only verification of current records against the owning evidence:
python3 -B scripts/test/reconcile-service-successors.py \
  --identity-migration --configuration Debug --rid osx-arm64

# Explicitly generate only the two CURRENT successor JSON records:
python3 -B scripts/test/reconcile-service-successors.py \
  --identity-migration --configuration Debug --rid osx-arm64 --write
```

The mode requires the complete 15-package / 19-public / 13-private mapping and all
15 actual owning receipts. It authenticates the entire permanent predecessor
archive, retains the 19+35 public history closure, and compares public descriptor
and ABI trees under only reviewed ID/version/generated-reference renaming. It also
compares all 19 public model/reference projections and method-policy sets. Private
ordinary-service shapes are authenticated by reversing those renames and matching
the **actual old ordinary-private descriptor SHA** (and ABI SHA when the receipt
contains its projection). The retired private wrapper descriptors are not
substitutes for those ordinary-private predecessors. Services/methods, shapes,
constraints, stream kinds and errors cannot change silently.

Actual receipts contain both owned and imported contracts. The reader uses the
bundle authority/source classification and validates imported tuples against the
receipt's exact pinned lock; platform/imported contracts are not new owned
families. The linked C# current-descriptor helpers enforce the same ownership
distinction without acquiring a dependency on the historical inverse class.

Private borrowed definitions use Core's `Imported_<SHA256>` naming recipe:
authority plus published model name, or authority plus contract ID plus definition
name for non-model definitions (including service references). The reconciler
derives **294 exact name mappings** from pinned predecessor public projection
reviews and actual new public projections. Both definition keys and `$ref` values
are renamed; arbitrary same-shaped graph matches are rejected. These are
equivalence-only definition names, not synthesized new descriptor/artifact hashes.

The hashes written to current records come from the actual owned baseline and
generated receipt. Reverse normalization is solely an equivalence witness, never
a fabricated new artifact or descriptor. Inputs are checked again before either
current record is written. There is no partial private selection in identity mode.

**Fork is not Freeze.** The actual new public baselines used here are
`release-frozen`, package **2.0.0**, with all 19 new public families at **1.0.0** and
no inherited history. The reconciler still rejects drafts. All 13 new private
families remain **2.0.0**. No product build, .NET test, publication, Registry call,
staging or commit was performed by this adaptation task.

Current outputs (repository-relative paths):

| Record | SHA-256 |
| --- | --- |
| `compatibility/agent-service-public-successors.json` | `769f4ac4038f4ed620bdf395e0909635abd2a5c3a8d3684438346ca8e6e7f67f` |
| `compatibility/agent-ordinary-private-successors.json` | `f1ea85ce5d4609e4021538faad1f90ea8bc45a8b5f14f02db51a3b7931186f46` |

The original map SHA remains
`31ad8ca21dc07dcf562e5cb53e5168097b2f626ec3be4fc14edb941eb3de36a9`;
the predecessor manifest SHA remains
`7dec732776212922f3c1534ffe8fae9119f57b6d0876efc631c9eba1d3afb904`.
All 33 captured files and frozen oracles retain their bytes, including all retired
private evidence and the existing imported-name inverse/pins.

The static fixture suite creates isolated, explicitly synthetic receipts and
private test predecessors. It does not consume or alter live `obj`, locks, owned
baselines, current successors, or the permanent predecessor tree:

```sh
python3 -B scripts/test/reconcile-service-successors.fixture.py
```

The 12 reconciler fixtures and six inverse guards passed. Read-only reconciliation
also passed after generation and after .NET qualification. Check current source
and successor boundaries with:

```sh
python3 -B compatibility/identity-migration/v1/source_inverse.py check
python3 -B scripts/test/reconcile-service-successors.py --identity-migration --configuration Debug --rid osx-arm64
```

The older `current_successor_proof.py check` deliberately requires its original
live role-input receipt hashes and inverse pin. A later normal build can reissue
those receipts; preserve the earlier proof instead of rewriting it to claim the
new checkpoint. The qualification summary records the measured current receipt
identities and verifies the unchanged baseline/lock and archive bytes.

`CurrentPublicRpcDescriptors.cs` and `CurrentPrivateRpcDescriptors.cs`
are already linked by `tests/RegressionTestHarness.props`; their ownership/map
checks add no new project linkage. The identity historical readers are default
compile inputs of `Sunder.Package.Agent.Tests`. OpenAI's standalone source test
uses its local hash-bound predecessor reader. Local .NET qualification is complete
in the recorded macOS scope; native/released-artifact and other-OS gates remain
separate.
