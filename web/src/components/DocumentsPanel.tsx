import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { ApiError } from '../api/client';
import { documentsApi } from '../api/documents';
import type { ProjectDocument } from '../api/types';

export function DocumentsPanel({ projectId }: { projectId: string }) {
  const { token } = useAuth();
  const [documents, setDocuments] = useState<ProjectDocument[] | null>(null);
  const [uploadFile, setUploadFile] = useState<File | null>(null);
  const [busy, setBusy] = useState<'load' | 'upload' | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    setBusy('load');
    setError(null);
    try {
      setDocuments(await documentsApi.listForProject(projectId, token));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load documents.');
    } finally {
      setBusy(null);
    }
  };

  const onUpload = async () => {
    if (!uploadFile) return;
    setBusy('upload');
    setError(null);
    try {
      await documentsApi.upload(projectId, uploadFile, token);
      setUploadFile(null);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to upload document.');
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="card">
      <h2>Project Documents</h2>
      <p className="hint">§26 — specs/notes fed into chunking + embeddings for the Copilot's semantic search below.</p>
      <button type="button" disabled={busy === 'load'} onClick={load}>
        {busy === 'load' ? 'Loading…' : documents ? 'Refresh' : 'Load documents'}
      </button>

      <div className="upload-row">
        <span className="hint">upload a .txt, .pdf, or .docx document:</span>
        <input type="file" accept=".txt,.pdf,.docx" onChange={(e) => setUploadFile(e.target.files?.[0] ?? null)} />
        <button type="button" disabled={!uploadFile || busy === 'upload'} onClick={onUpload}>
          {busy === 'upload' ? 'Uploading…' : 'Upload'}
        </button>
      </div>

      {error && <p className="error">{error}</p>}

      {documents && (
        documents.length === 0 ? (
          <p>No documents uploaded yet.</p>
        ) : (
          <ul className="document-list">
            {documents.map((d) => (
              <li key={d.id}>
                <span>{d.fileName}</span>
                <span className="hint">{d.chunkCount} chunk{d.chunkCount === 1 ? '' : 's'}</span>
                <span className="hint">{new Date(d.uploadedAt).toLocaleString()}</span>
              </li>
            ))}
          </ul>
        )
      )}
    </div>
  );
}
