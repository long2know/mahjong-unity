interface NewMessage {
  type: 'NEW';
}

interface JoinMessage {
  type: 'JOIN';
  gameId: string;
}

export type ViewerSeat = 0 | 1 | 2 | 3 | null;

export interface ViewerAuthority {
  roomId: string | null;
  revision: number;
  seat: ViewerSeat;
}

interface JoinedMessage {
  type: 'JOINED';
  gameId: string;
  playerId: string;
  isFirst: boolean;
  // Required on Changsha server envelopes; omitted by the relay protocol.
  viewer?: ViewerAuthority;
}

interface UpdateMessage {
  type: 'UPDATE';
  // kind, key, value
  entries: Array<Entry>;
  full: boolean;
  viewer?: ViewerAuthority;
}

export type Entry = [string, string | number, any | null];

export type Message = NewMessage
  | JoinMessage
  | JoinedMessage
  | UpdateMessage;
