import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { bootstrapIdentity, getIdentityBootstrapState } from './identity';

export interface HubStatus {
  state: 'disconnected' | 'connecting' | 'connected' | 'reconnecting' | 'unavailable';
  error: string | null;
}

let connection: HubConnection | null = null;
let startPromise: Promise<HubConnection> | null = null;
let status: HubStatus = { state: 'disconnected', error: null };
const createdListeners = new Set<(c: HubConnection) => void>();
const connectListeners = new Set<(c: HubConnection) => void>();
const statusListeners = new Set<(value: HubStatus) => void>();

function publishStatus(state: HubStatus['state'], error: string | null = null): void {
  status = { state, error };
  for (const cb of statusListeners) cb(status);
}

function publishConnected(local: HubConnection): void {
  publishStatus('connected');
  for (const cb of connectListeners) cb(local);
}

function buildConnection(): HubConnection {
  const local = new HubConnectionBuilder()
    .withUrl('/hubs/changsha')
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();
  local.onreconnecting(error => publishStatus('reconnecting', error?.message ?? null));
  local.onreconnected(() => publishConnected(local));
  local.onclose(error => {
    startPromise = null;
    publishStatus('unavailable', error?.message ?? 'Lobby connection closed.');
  });
  return local;
}

/** Register server event handlers before start, including on a newly built connection. */
export function onHubConnectionCreated(callback: (c: HubConnection) => void): () => void {
  createdListeners.add(callback);
  if (connection !== null) callback(connection);
  return () => { createdListeners.delete(callback); };
}

export async function getHubConnection(): Promise<HubConnection> {
  if (connection?.state === HubConnectionState.Connected) return connection;
  if (startPromise !== null) return startPromise;
  if (connection?.state === HubConnectionState.Reconnecting
      || connection?.state === HubConnectionState.Disconnecting) {
    throw new Error('Lobby connection is reconnecting. Please wait or retry.');
  }

  const attempt = Promise.resolve().then(async (): Promise<HubConnection> => {
    const identity = await bootstrapIdentity();
    if (identity === null) {
      throw new Error(getIdentityBootstrapState().error ?? 'Verified identity is required.');
    }
    if (connection === null) {
      connection = buildConnection();
      for (const cb of createdListeners) cb(connection);
    }
    const local = connection;
    await local.start();
    publishConnected(local);
    return local;
  });
  startPromise = attempt;
  publishStatus('connecting');
  try {
    return await attempt;
  } catch (error) {
    publishStatus('unavailable', error instanceof Error ? error.message : String(error));
    throw error;
  } finally {
    if (startPromise === attempt) startPromise = null;
  }
}

export function onHubConnected(callback: (c: HubConnection) => void): () => void {
  connectListeners.add(callback);
  if (connection?.state === HubConnectionState.Connected) callback(connection);
  return () => { connectListeners.delete(callback); };
}

export function onHubStatus(callback: (value: HubStatus) => void): () => void {
  statusListeners.add(callback);
  callback(status);
  return () => { statusListeners.delete(callback); };
}

export function hubIsConnected(): boolean {
  return connection?.state === HubConnectionState.Connected;
}

export async function invokeHub<T = unknown>(method: string, ...args: unknown[]): Promise<T> {
  const conn = await getHubConnection();
  return conn.invoke<T>(method, ...args);
}

export async function stopHubConnection(): Promise<void> {
  if (connection === null) return;
  await connection.stop();
  publishStatus('disconnected');
}
