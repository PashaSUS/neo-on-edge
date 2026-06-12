import * as signalR from '@microsoft/signalr'
import { endpointBase } from './utils'

let _conn: signalR.HubConnection | null = null
const HUB_URL = endpointBase(import.meta.env.VITE_HUB_URL, '/hub')

export function getHub(): signalR.HubConnection {
  if (!_conn) {
    _conn = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build()
  }
  return _conn
}

export async function ensureConnected(): Promise<signalR.HubConnection> {
  const hub = getHub()
  if (hub.state === signalR.HubConnectionState.Disconnected) {
    await hub.start()
  }
  return hub
}
