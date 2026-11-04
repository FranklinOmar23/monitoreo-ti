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
import { CommandExecutor, createRunner, FileSeenCommands } from './executor';
import { nodeRelaySocket, RemoteSessions } from './remote-sessions';
import { terminalHandlerFactory } from './terminal';
import { filesHandlerFactory } from './files';
import { desktopHandlerFactory } from './desktop';
import { AgentUpdater, UPDATE_EXIT_CODE } from './updater';
import { createEnvelope } from '@monitoreoti/shared';
import { GatewayClient } from './gateway-client';
import { readMachineIdentity } from './machine-info';
import { Outbox } from './outbox';
import { AgentRuntime, type Log } from './runtime';
import { hostSnmpQuery } from './snmp';

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

  const credentialStore = new CredentialStore(config.dataDir, dpapiProtector);
  const credential = await ensureCredential({
    serverOrigin: config.serverUrl.origin,
    enrollToken: config.enrollToken,
    store: credentialStore,
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
  const seenCommands = new FileSeenCommands(config.dataDir);
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
    // El anfitrión C# (que lanza este proceso) hace las consultas SNMP; en desarrollo sin él, el sondeo informa el error.
    snmp: { credential: credential.credential, query: hostSnmpQuery(process.env['MONITOREOTI_HOST_EXE']) },
    sessions: new RemoteSessions({
      serverUrl: config.serverUrl,
      handlers: {
        terminal: terminalHandlerFactory(process.env['MONITOREOTI_HOST_EXE'], log),
        escritorio: desktopHandlerFactory(process.env['MONITOREOTI_HOST_EXE'], log, undefined, (sesionId, activity) =>
          state.runtime?.submitMessage(createEnvelope('actividad_sesion', { sesionId, ...activity })),
        ),
        // Cada operación con archivos llega al servidor para la auditoría (ruta y tamaño, nunca el contenido).
        archivos: filesHandlerFactory((sesionId, activity) =>
          state.runtime?.submitMessage(createEnvelope('actividad_sesion', { sesionId, ...activity })),
        ),
      },
      createSocket: nodeRelaySocket,
      log,
    }),
    // Descarga lo firmado por el producto y le pasa la instalación al anfitrión (que vuelve a verificar).
    updater: new AgentUpdater({
      serverUrl: config.serverUrl,
      credential: credential.credential,
      currentVersion: config.agentVersion,
      dataDir: config.dataDir,
      log,
      handOver: () => {
        state.runtime?.stop();
        state.outbox?.close();
        log('info', 'El agente termina para instalar la actualización');
        process.exit(UPDATE_EXIT_CODE);
      },
    }),
    // Solo ejecuta lo firmado con la llave de su empresa (sección 8.1).
    executor: (submit) =>
      new CommandExecutor({
        machineId: credential.machineId,
        publicKey: () => credential.companyPublicKey,
        seen: seenCommands,
        run: createRunner(join(config.dataDir, 'tmp')),
        submit,
        log,
      }),
    // La llave llega una sola vez: después no se reemplaza desde la red (una llave nueva exige reinstalar o rotarla).
    onCompanyKey: (publicKey) => {
      if (credential.companyPublicKey) return;
      credential.companyPublicKey = publicKey;
      credentialStore
        .save(credential)
        .then(() => log('info', 'Llave de firma de la empresa guardada'))
        .catch((error: unknown) => log('error', 'No se pudo guardar la llave de la empresa', { error: String(error) }));
    },
    createGateway: ({ onReady, onMessage }) =>
      new GatewayClient({
        serverUrl: config.serverUrl,
        credential: credential.credential,
        agentVersion: config.agentVersion,
        hostname: identity.hostname,
        operatingSystem: identity.operatingSystem,
        heartbeatSeconds: config.heartbeatSeconds,
        onReady,
        onMessage,
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
