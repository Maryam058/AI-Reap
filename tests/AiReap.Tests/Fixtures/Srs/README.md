# Phase 0 golden SRS fixtures

Everything in this folder is **synthetic**: invented for automated tests, describing a fictional
"Harbourview Community Clinic" room-booking system. It contains no real customer SRS, no real
organisation, person, product, email address or credential. The only email domain allowed in a
fixture is `example.test` (FixtureSafetyTests enforces this and scans for key patterns).

| File | Purpose |
|---|---|
| `clinic-booking.srs.md` | Main fixture: numbered FRs/NFRs, stated NFR targets, planted gaps (G1-G3), contradictions (C1-C2), duplicates (D1-D2), background text and a mention-only ID. |
| `clinic-booking.expected.json` | Its deterministic expected results (GoldenFixture). Every anchor quote must be found verbatim in the fixture (GoldenFixtureIntegrityTests). |
| `vague-request.srs.txt` / `.expected.json` | A short, vague request: no IDs, no stated targets; should produce clarification questions only. |
| `candidates/*.json` | Hand-written AI outputs used to test the scorer itself (SrsQualityScorerTests). Not real model output. |

The unnumbered variant of the clinic fixture and the large multi-part fixture are derived in code
(GoldenFixture.Unnumbered / GoldenFixture.Large) so they cannot drift from the main fixture. DOCX and
PDF fixtures are built in memory at test time (DocxFixtureBuilder / PdfFixtureBuilder); no binary
file is committed and nothing is downloaded.

Fixture text is read with line endings normalized to `\n`, so results do not depend on how git
checks the files out.

To extend: add the planted item to the fixture, add its expectation (with a verbatim anchor quote)
to the expected file, run `dotnet test --filter Category=Phase0`, and update any exact metric the
change moves in SrsQualityScorerTests.
