/**
 * Modo servicio: corre como servicio de Windows (LocalSystem) con
 * ELECTRON_RUN_AS_NODE=1, sin ventana y antes de que inicie sesión un usuario.
 * Aquí solo se usan APIs de Node, nunca el módulo 'electron'.
 *
 * Fase 1: se registra con el token de la sucursal, mantiene la conexión WSS con el servidor y
 * reporta métricas e inventarios a través de una cola local.
 */
import { join } from 'node:path';
import { ensureCredential } from './bootstrap';
import {
  collectDrives,
  collectHardware,
  collectInterfaces,
  collectSoftware,
  collectTemperature,
} from './collectors/windows';
import { EventsCollector } from './collectors/events';
import {
  collectDiskHealth,
  collectProcesses,
  collectSecurity,
  collectServices,
  collectVolumes,
} from './collectors/health';
import { SystemSampler } from './collectors/sampler';
import { loadServiceConfig, removeEnrollToken } from './config';
import { CredentialStore, dpapiProtector } from './credential-store';
import { enrollAgent } from './enroll';
import { GatewayClient } from './gateway-client';
import { readMachineIdentity } from './machine-info';
import { Outbox } from './outbox';
import { AgentRuntime, type Log } from './runtime';

const log: Log = (level, message, extra = {}) => {
  const line = JSON.stringify({ ts: new Date().toISOString(), level, message, ...extra });
  (level === 'error' ? process.stderr : process.stdout).write(`${line}\n`);
};

async function main(): Promise<void> {
  const config = loadServiceConfig(process.env);
  const identity = await readMachineIdentity();
  log('info', 'Servicio del agente iniciado', { server: config.serverUrl.origin, version: config.agentVersion });

  const state: { stopRequested: boolean; runtime?: AgentRuntime; outbox?: Outbox } = { stopRequested: false };

  const stop = (signal: string) => {
    state.stopRequested = true;
    state.runtime?.stop();
    state.outbox?.close();
    log('info', 'Servicio del agente detenido', { signal });
    process.exit(0);
  };
  process.once('SIGINT', () => stop('SIGINT'));
  process.once('SIGTERM', () => stop('SIGTERM'));

  const credential = await ensureCredential({
    serverOrigin: config.serverUrl.origin,
    enrollToken: config.enrollToken,
    store: new CredentialStore(config.dataDir, dpapiProtector),
    enroll: (enrollToken) =>
      enrollAgent({
        serverUrl: config.serverUrl,
        enrollToken,
        agentVersion: config.agentVersion,
        identity,
      }),
    log,
    isStopped: () => state.stopRequested,
  });
  if (!credential) return;
  // Con la credencial permanente el token de la sucursal ya no hace falta en disco.
  removeEnrollToken(config.dataDir);

  const outbox = new Outbox(join(config.dataDir, 'outbox.db'));
  state.outbox = outbox;
  const sampler = new SystemSampler(
    { drives: collectDrives, temperature: collectTemperature },
    Date.now,
    (message, error) => log('error', message, { error: String(error) }),
  );

  const events = new EventsCollector(config.dataDir);
  const runtime = new AgentRuntime({
    outbox,
    log,
    health: {
      volumes: collectVolumes,
      processes: collectProcesses,
      services: collectServices,
      events: () => events.collect(),
      diskHealth: collectDiskHealth,
      security: collectSecurity,
    },
    collectors: {
      metrics: () => sampler.sample(),
      hardware: collectHardware,
      software: collectSoftware,
      interfaces: collectInterfaces,
    },
    createGateway: ({ onReady }) =>
      new GatewayClient({
        serverUrl: config.serverUrl,
        credential: credential.credential,
        agentVersion: config.agentVersion,
        hostname: identity.hostname,
        operatingSystem: identity.operatingSystem,
        heartbeatSeconds: config.heartbeatSeconds,
        onReady,
        log,
      }),
  });
  state.runtime = runtime;
  runtime.start();
}

main().catch((error: unknown) => {
  log('error', error instanceof Error ? error.message : String(error));
  process.exit(1);
});
