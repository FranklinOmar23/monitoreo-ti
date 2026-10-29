# App móvil

React Native + Expo SDK 58 + TypeScript (React 19, que exige el SDK; el panel usa React 18).

```powershell
npm run start -w @monitoreoti/mobile       # abre Expo; escanear el QR con Expo Go o usar un emulador
npm run typecheck -w @monitoreoti/mobile
```

Esquema de enlaces profundos: `monitoreoti://` (los QR usan `monitoreoti://q/{codigo_qr}`).
