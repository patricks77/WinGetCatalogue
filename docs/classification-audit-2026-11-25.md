# Classification review — 25 November 2026

## Scope and limitations

The private proposal database contained 5,377 records when audited. The automated integrity pass checked whether each identifier existed in the local WinGet index, whether its stored manifest hash matched, and whether its proposed category key existed. These are structural checks, not proof that a category describes the software accurately.

The pass found no missing identifiers and no invalid category keys. Two records had a different current manifest hash: `HagelTechnologiesLtd.DUMeter` and `wanderlp.MergeMate`. Both were already classified by the current catalogue rules, so their older proposals must not override that classification.

## Manual review

Forty-seven classifications, including four approved before this audit, had been approved at export time. The additional records were checked against the description supplied in the proposal database, and their manifest hash and current category were checked against the local index. The local `review_audit` table records the previous category, approved category, evidence summary and review time for the additional reviews.

Examples of corrected suggestions include GPXSee and GRLevel3 (maps and weather), lsdeer and Filedini (file utilities), and Lune (a Spotify client). Multi-purpose software or descriptions that do not establish a clear category remain unapproved.

In a subsequent review pass, 91 additional records were examined and approved against their descriptions, including corrections for an Ollama client and a browser-cache viewer. This brought the exported total to 138 approved records, including the four previously approved. Their rationale is recorded in the private review audit table.

The remaining 5,239 proposals have **not** been individually verified. Keyword checks can identify possible discrepancies but cannot establish correctness: a description can mention a browser or media player without the application itself being one.

## Release behavior at the time of the audit

The release included unreviewed suggestions in `Data/classifications-proposed.json`. Verified corrections in `Data/classifications-reviewed.json` took precedence. At the time, both snapshots required the source hash to match, and the application retained any WinGet-based classification. See [classification maintenance](classification-maintenance.md) for the current rules and release checks.
