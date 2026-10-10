// Phase K Wave 22 — Hicks (Frontend).
//
// Operator UI for Bishop's W22 chunked replay-download endpoint:
//
//   GET /api/admin/replays/<replayId>/chunks/<n>
//
// Replays beyond the W17 cold-storage threshold are sharded into
// fixed-size chunks; the operator UI surfaces the per-chunk
// listing so support can pull a single chunk in isolation when
// investigating a specific in-game incident (claim window
// timing, hand-resolution audit, etc.).  READ-ONLY surface — no
// CRUD writes.
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with a chunked listing.
//   • Query params (on the listing GET): `replayId` (required —
//     drives the listing for a single replay), `from` (optional —
//     chunk index lower bound), `to` (optional — upper bound).
//   • Per-chunk fetch: GET to `/api/admin/replays/<replayId>/
//     chunks/<n>` returns the raw chunk bytes (application/
//     octet-stream).  The surface renders a "Download chunk N"
//     link rather than streaming the bytes through the JS layer.
//   • No X-Admin-Reason required (read-only listing); the per-
//     chunk download surfaces audit via the request URL.
//
// Routed into the `admin-panel-tournaments` chunk (the W22
// catch-all for ops/audit surfaces; named per the directive but
// holds the wider operational + audit set).  See
// `vite.config.ts:manualChunks`.

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

interface ReplayChunkRow {
  replayId: string;
  chunkIndex: number;
  sizeBytes: number;
  contentHash: string;
  storedAt: string;
  warmthState: 'warm' | 'cold' | 'restoring';
}

function parseRow(raw: unknown): ReplayChunkRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const replayId = typeof o.replayId === 'string' ? o.replayId : null;
  const chunkIndex = typeof o.chunkIndex === 'number'
    && Number.isFinite(o.chunkIndex)
    ? Math.max(0, Math.floor(o.chunkIndex)) : null;
  if (replayId === null || chunkIndex === null) return null;
  const warmth = o.warmthState;
  const warmthState: 'warm' | 'cold' | 'restoring' =
    warmth === 'cold' || warmth === 'restoring' ? warmth : 'warm';
  return {
    replayId,
    chunkIndex,
    sizeBytes: typeof o.sizeBytes === 'number'
      && Number.isFinite(o.sizeBytes)
      ? Math.max(0, Math.floor(o.sizeBytes)) : 0,
    contentHash: typeof o.contentHash === 'string' ? o.contentHash : '',
    storedAt: typeof o.storedAt === 'string' ? o.storedAt : '',
    warmthState,
  };
}

function fmtBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(2)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function warmthClass(w: ReplayChunkRow['warmthState']): string {
  switch (w) {
    case 'warm':      return 'admin-panel-outcome-ok';
    case 'restoring': return 'admin-panel-outcome-warn';
    case 'cold':      return 'admin-panel-outcome-err';
  }
}

function warmthLabel(w: ReplayChunkRow['warmthState']): string {
  switch (w) {
    case 'warm':      return 'Warm';
    case 'restoring': return 'Restoring';
    case 'cold':      return 'Cold';
  }
}

export const REPLAY_DOWNLOAD_CHUNKED_SPEC: AdminSurfaceSpec<ReplayChunkRow, never> = {
  id: 'replay-download-chunked',
  title: 'Replays · Chunked download',
  description: 'Per-chunk listing for replays sharded beyond the '
    + 'W17 cold-storage threshold.  Use the per-row Download link '
    + 'to pull a single chunk in isolation when investigating a '
    + 'specific in-game incident.  Cold chunks must be restored '
    + 'via the Replays · Restoration audit surface before they '
    + 'can be downloaded.',
  endpoint: '/api/admin/replays/chunks',
  parseRow,
  rowKey: (r) => `${r.replayId}#${r.chunkIndex}`,
  fields: [],
  buildBody: () => { throw new Error('replay-download-chunked is read-only'); },
  columns: [
    {
      key: 'replayId',
      label: 'Replay',
      render: (r) => ({ __html: `<code>${escapeHtml(r.replayId)}</code>` }),
    },
    {
      key: 'chunkIndex',
      label: 'Chunk',
      render: (r) => `#${r.chunkIndex}`,
    },
    {
      key: 'sizeBytes',
      label: 'Size',
      render: (r) => fmtBytes(r.sizeBytes),
    },
    {
      key: 'warmthState',
      label: 'Warmth',
      render: (r) => ({
        __html: `<span class="${warmthClass(r.warmthState)}">${escapeHtml(warmthLabel(r.warmthState))}</span>`,
      }),
    },
    {
      key: 'storedAt',
      label: 'Stored',
      render: (r) => fmtIso(r.storedAt),
    },
    {
      key: 'contentHash',
      label: 'Hash',
      render: (r) => r.contentHash === ''
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : ({ __html: `<code class="admin-panel-muted">${escapeHtml(r.contentHash.slice(0, 12))}…</code>` }),
    },
    {
      key: 'download',
      label: 'Download',
      render: (r) => r.warmthState === 'cold'
        ? ({ __html: '<span class="admin-panel-muted">cold</span>' })
        : ({
          __html: `<a href="/api/admin/replays/${encodeURIComponent(r.replayId)}/chunks/${r.chunkIndex}" `
            + `class="admin-panel-link" download>Get chunk</a>`,
        }),
    },
  ],
};
