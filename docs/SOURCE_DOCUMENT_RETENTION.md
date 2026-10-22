# Original source documents: retention, storage and viewing

AI-REAP keeps the **original PDF or DOCX** of each uploaded requirement source. When a reviewer clicks a
section reference in a clarification question's "Why it matters", the in-app viewer can then show the
document itself, open at the cited place. This page covers what is kept, where it is kept, who can read
it, and when it is deleted.

## What is kept

| Upload | Text (`RequirementSources.RawText`) | Original file |
|---|---|---|
| PDF | Extracted, as before | Kept |
| DOCX | Extracted, as before | Kept |
| TXT | The file's text, as before | Not kept (the text *is* the file) |
| Pasted text | As entered | Not applicable |
| Uploaded before this change | As before | None exists; the viewer shows the text |

Only files uploaded through `POST /api/projects/{projectId}/requirement-sources/upload` are kept. Project
knowledge documents (`/documents/upload`) are not affected.

The extracted text is unchanged, so analysis, CQ generation, evidence and RAG behave exactly as before.

## Upload checks

- **File type:** `.pdf`, `.docx` or `.txt` only.
- **Content check:** the content must match the extension.
  - **PDF:** `%PDF-` must appear in the first 1 KB, and PdfPig must open the file.
  - **DOCX:** the file must be a ZIP with a Word main document. These are refused: macro-enabled documents, templates, and password-protected or legacy `.doc` (OLE) files.
  - **TXT:** the file must not carry a PDF, ZIP, OLE or executable signature.
- **DOCX expansion limit:** a DOCX may expand to at most 200 MB and 5,000 ZIP entries.
- **Size limit:** `OriginalDocuments:MaxUploadBytes`, 5 MB by default. A larger upload gets **413**.
- **Error messages:** they never quote the file's content.

## Storage

- **Interface:** `IOriginalDocumentStore`, implemented by `FileSystemOriginalDocumentStore`, a private directory.
- **Location:** `OriginalDocuments:RootPath`.
  - The default is `%LOCALAPPDATA%\AiReap\source-documents\{database name}` for the account the API runs as.
  - That default is outside the repository and outside anything the API or the web app serves.
  - Each database gets its own directory, so a dev API and a test run on one machine never share one.
  - On a server, set `RootPath` to a dedicated directory that only the API's service account can read. Use one directory per database.
- **Owner marker:** the first database to run the clean-up records itself (a hash of server and database name) in `{root}/.aireap-owner`. The clean-up never deletes orphans in a directory that belongs to another database; it logs a warning instead. This stops two databases sharing a directory from deleting each other's files.
- **Tests:** automated test hosts always use a temporary directory per test database.
- **Layout:** `{root}/{key[0..2]}/{key}.bin`.
  - The key is a random GUID made by the server. It is never derived from the file name or from anything the user sends.
  - Any other key shape is refused before a path is built, which also blocks path traversal.
  - A write goes to a temporary file that is then renamed into place, and an existing key is never overwritten.
- **Metadata:** the `RequirementSourceFiles` table holds one row per source with:
  - the storage key;
  - the detected format;
  - the length;
  - the SHA-256 hash;
  - for PDFs, the text line each page starts on;
  - the time it was stored.

  The uploaded file name stays on the source, as display text only.
- **Encryption:** AI-REAP does not encrypt the files itself. Put `RootPath` on an encrypted volume (BitLocker, LUKS, or an encrypted cloud disk).

## Who can read an original

`GET /api/requirement-sources/{id}/original` is the only way the bytes leave the server, and the in-app
viewer is its only client.

| Caller | Response |
|---|---|
| Not signed in | 401 |
| Not a member of the source's project (and not an Administrator) | **404**, the same answer as for an ID that does not exist |
| Member whose role may not read raw sources (Developer only; S2) | 403 |
| Member who may read raw sources | 200 with the bytes |
| Source has no kept original | 404, `type: urn:aireap:original-not-retained` |
| Kept file missing, or fails its length or SHA-256 check | 404, `type: urn:aireap:original-unavailable`, logged by source ID |

How a successful response is sent:

- **Headers:** `Content-Type` is `application/pdf` or the DOCX type, chosen from the detected format. The response also carries:
  - `Content-Disposition: inline` with a sanitized file name;
  - `X-Content-Type-Options: nosniff`;
  - `Cache-Control: no-store, private`;
  - `Content-Security-Policy: sandbox; default-src 'none'`.
- **No URLs:** there are no public or permanent URLs. The viewer fetches the bytes with the user's token, and the storage key and location are never sent to a client.
- **Logging:** the logs never contain document content.

Existing behaviour I did not change: `GET /api/requirement-sources/{id}` still answers 403 to a
non-member when the source exists, and 404 when it does not.

## How the viewer finds the cited place

Grounding is unchanged. A reference is resolved and checked against the extracted text by the server
(`SourceReferenceIndex`), and the client checks it again (`locateReference`). In the original document,
that verified text location is then found again:

**PDF** (pdf.js, canvas plus text layer)
- **Page:** the cited line's page comes from the page map recorded at upload.
- **Highlight:** the heading, or the quoted passage, is marked in that page's text layer.
- **Repeated text:** when the same words appear several times on a page, the match on the cited line is used, not the first match.
- **Text not matched:** the viewer opens the page and says it could not highlight the text.
- **Inert content:** links, forms and scripts in the PDF are never active, because no annotation layer is rendered.

**DOCX** (docx-preview, in an `<iframe sandbox="allow-same-origin">` with no scripts, popups or navigation)
- **Paragraph mapping:** the extracted text has one line per body paragraph, so the cited lines map to paragraphs.
- **Verification:** each paragraph must have the same text as its line.
- **Repeated headings:** a repeated heading is resolved by which repeat it is.
- **Highlight:** the cited paragraphs are highlighted, and a quote is marked inside them.
- **Sanitizing:** the rendered markup is cleaned before it is shown. The cleaning removes:
  - scripts, frames and forms;
  - event handlers;
  - external or `javascript:` links;
  - external loads.
- **Not rendered:** comments, tracked changes and embedded HTML (`altChunk`).

**Fallback:** if the original is missing or cannot be displayed, the viewer shows the extracted text and
says why. A reference that cannot be verified is never highlighted anywhere.

## Lifecycle and deletion

| Event | What happens to the original |
|---|---|
| Upload succeeds | The file is stored, then the source and file row are saved together. The upload reports success only after both. |
| Upload fails after the file was stored | The file is deleted again. If that delete also fails, the file is an orphan (see clean-up below). |
| Analysis or CQ generation finishes | Nothing. Originals are not deleted when processing finishes. |
| Source deleted | Not possible today: AI-REAP has no per-source delete. The file row would cascade with the source. |
| Project deleted | Rows are removed in the same transaction as the project. Files are deleted **after** the commit. A failed file delete leaves an orphan for the clean-up. |
| `RetentionDays` set and an original is older than that | Row and file are deleted. The source, its text, its CQs and their references stay, and the viewer shows the text. |
| File missing or corrupted | Served as "unavailable" (above), never as wrong content. |

**Clean-up (`OriginalDocumentJanitor`)** runs every `CleanupIntervalMinutes`, 6 hours by default.
- **Retention:** it applies `RetentionDays`.
- **Orphans:** it deletes stored files that no row references and that are older than `OrphanGraceMinutes` (60 by default). It also deletes temporary files left by interrupted writes.
- **Safe failure:** it deletes nothing if it cannot read the database (for example, before the migration is applied).
- **Scope:** it touches only files with its own key shape under `RootPath`.
- **Ownership:** it removes orphans only when `RootPath` belongs to its own database (owner marker, above).

**Default retention:** an original is kept as long as its source exists, which matches §6 for the source
text ("retained verbatim"). This is a policy choice. Set `RetentionDays` if originals must expire sooner.

**Backups:** database backups now contain the `RequirementSourceFiles` metadata but **not the files**.
Back up `RootPath` alongside the database. Restoring only one of them gives either "original unavailable"
(rows without files) or orphans that the clean-up later deletes (files without rows).

## Configuration

```json
"OriginalDocuments": {
  "Enabled": true,
  "RootPath": "D:\\AiReapData\\source-documents",
  "MaxUploadBytes": 5242880,
  "RetentionDays": null,
  "CleanupIntervalMinutes": 360,
  "OrphanGraceMinutes": 60
}
```

- **Enabled:** `false` stops keeping new originals. Existing ones are still served and cleaned up.
- **Other settings:** all have the defaults shown and can be left out.

## Deploying: the database migration

The migration `20261022075854_AddRequirementSourceFiles` adds one table and one unique index. It does
not change or delete existing tables or rows. The API does not apply migrations itself.

1. Back up the database.
2. From the repository root, apply the migration:
   ```
   dotnet ef database update --project src/AiReap.Infrastructure --startup-project src/AiReap.Api
   ```
   To review the SQL first, or to hand it to a DBA, generate an idempotent script instead:
   ```
   dotnet ef migrations script 20261020090000_AddArtifactRowVersion AddRequirementSourceFiles --idempotent --project src/AiReap.Infrastructure --startup-project src/AiReap.Api -o add-requirement-source-files.sql
   ```
3. Set `OriginalDocuments:RootPath` on servers, and back that directory up.
4. Restart the API.

**Until the migration is applied:**
- PDF/DOCX uploads fail with a database error. The stored file is removed again.
- Opening a source's details fails.
- The clean-up logs a warning and deletes nothing.

**To roll back:**
```
dotnet ef database update AddArtifactRowVersion --project src/AiReap.Infrastructure --startup-project src/AiReap.Api
```
This drops the table. The files under `RootPath` are not deleted; delete them by hand if wanted.

## Known limitations

- **Old uploads:** sources uploaded before this change have no original. Only their text can be shown, and nothing is reconstructed from the text.
- **PDF matching:** the PDF page comes from the extracted text's page map. Highlighting needs the page's text layer to contain the same words. Scanned PDFs are refused at upload, as before.
- **Unusual PDF layouts:** for multi-column or unusually encoded PDFs, the viewer may reach the right page but not highlight anything, and it says so.
- **DOCX rendering:** docx-preview approximates Word's layout, so page breaks, fonts and complex tables can look different from Word.
- **DOCX text boxes:** text boxes and other paragraphs outside the main body can shift the paragraph mapping. Matching by text and by repeat handles most cases; otherwise the viewer says it could not match.
- **DOCX tables:** paragraphs inside tables are part of both the extracted text and the rendering, but the extracted text keeps no table structure.
- **Single server:** storage is a local directory, so several API instances need a shared directory with the same `RootPath`.
