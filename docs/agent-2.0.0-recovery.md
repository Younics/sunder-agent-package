# Agent 2.0.0 same-archive recovery

[Release 45150131370, attempt 3](https://github.com/Younics/sunder-agent-package/actions/runs/45150131370/attempts/3)
failed native Intel Worker teardown. The failed execution remains failed.
The repair is in Core Host/App 1.1.1; none of the Agent archives is rebuilt.

This PR proves the repaired path. It does **not** authorize staging or support
publishing from the branch. The recovery input inventory is a byte pin, not a
test result. The collector verifies the original protected-main run, qualified
tree, separate Core build and test coordinates, numeric artifact IDs and ZIP
digests. The original candidate reader verifies the 15 archives and all 90
leaves. Each Windows filesystem suite's original 34 passing cases are imported
from its original TRX, checked against the same execution in all three
attempts, and never rerun by the recovery job.

After review, dispatch the existing **Qualify** workflow once on
`fix/agent-2.0.0-release-recovery` with `packages=recovery-intel-2.0.0`. Only
the Intel job runs. It must use `macos-15-intel`, never Rosetta. It uses no
Registry environment or Registry credential, and leaves all ordinary
qualification and release readers unchanged.

The job runs the original development and Maker tests from the qualified
Agent checkout, with the original catalog and Intel Maker feed. It builds the
Runtime test host and App from the pinned, repaired Core source. Its separate
testhost observer records loaded Runtime assemblies only at process exit.
The original App RPC fixture verifies the startup hook's readiness/PID; the
proof also binds the launched App executable, its assembly and the hook to
the exact build output. These are **source-built test targets**, not evidence
that a released Host supervisor or App installer was executed.

Keep the TRX, all 15 metric reports, exact archive scope, original App process
logs, and Maker evidence, including on failure. This detached proof cannot
replace the protected six-RID final smokes, fan-in, or six anonymous
installed-acceptance receipts required before listing.
