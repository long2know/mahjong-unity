/** Shared links contain admission intent, never a creator's configuration or seat. */
export function buildRoomJoinUrl(gameId: string, absolute = false): string {
  if (!/^[A-Za-z0-9_.-]{1,64}$/.test(gameId)) {
    throw new Error('Invalid table ID.');
  }
  const url = new URL('/autotable/', window.location.origin);
  url.searchParams.set('gameId', gameId);
  url.searchParams.set('variant', 'changsha');
  url.searchParams.set('join', '1');
  return absolute ? url.href : `${url.pathname}${url.search}`;
}

export function isJoinOnly(search = window.location.search): boolean {
  const params = new URLSearchParams(search);
  return params.get('join') === '1'
    && (params.get('variant') ?? 'changsha').toLowerCase() === 'changsha';
}
