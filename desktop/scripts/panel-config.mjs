// Escribe build/panel.json con el servidor del panel instalado (lo lee src/main/server-config.ts).
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const value = process.env.MONITOREOTI_SERVER_URL;
let origin;
try {
    const url = new URL(value ?? '');
    if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error();
    origin = url.origin;
} catch {
    console.error('Defina MONITOREOTI_SERVER_URL con la dirección del servidor (por ejemplo https://ti.empresa.do).');
    process.exit(1);
}

const file = join(dirname(fileURLToPath(import.meta.url)), '..', 'build', 'panel.json');
mkdirSync(dirname(file), { recursive: true });
writeFileSync(file, JSON.stringify({ servidor: origin }, null, 2) + '\n');
console.log(`Panel para ${origin}`);
