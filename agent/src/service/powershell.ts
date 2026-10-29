import { spawn } from 'node:child_process';
import { join } from 'node:path';

const POWERSHELL = join(
  process.env['SystemRoot'] ?? 'C:\\Windows',
  'System32',
  'WindowsPowerShell',
  'v1.0',
  'powershell.exe',
);

/**
 * Ejecuta un script de PowerShell 5.1 y devuelve su salida estándar. Ruta absoluta (el servicio corre
 * como LocalSystem: no se confía en el PATH). Los datos sensibles van por stdin, nunca en la línea de comandos.
 */
export function runPowerShell(script: string, options: { stdin?: string; timeoutMs?: number } = {}): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(
      POWERSHELL,
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script],
      { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] },
    );

    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`PowerShell excedió ${options.timeoutMs ?? 30_000} ms.`));
    }, options.timeoutMs ?? 30_000);

    child.stdout.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
    child.stderr.setEncoding('utf8').on('data', (chunk: string) => (stderr += chunk));
    child.on('error', (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0) resolve(stdout);
      else reject(new Error(`PowerShell terminó con código ${code}: ${stderr.trim()}`));
    });

    child.stdin.end(options.stdin ?? '');
  });
}
