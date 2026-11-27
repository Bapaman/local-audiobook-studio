export const maxWavFragments = 256;

export function bookWavHref(projectId: string, revision: number, textDocumentId?: string) {
  return downloadHref(projectId, 'book.wav', revision, textDocumentId);
}

export function chaptersZipHref(projectId: string, revision: number, textDocumentId?: string) {
  return downloadHref(projectId, 'chapters.zip', revision, textDocumentId);
}

function downloadHref(projectId: string, fileName: string, revision: number, textDocumentId?: string) {
  const query = new URLSearchParams({ revision: String(revision) });
  if (textDocumentId) query.set('textDocumentId', textDocumentId);
  return `/api/projects/${encodeURIComponent(projectId)}/${fileName}?${query}`;
}
