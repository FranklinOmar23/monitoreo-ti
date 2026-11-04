; Instalador del agente MonitoreoTI (NSIS, incluido por electron-builder).
;
; Interactivo: pide la dirección del servidor y el token de instalación de la sucursal.
; Silencioso:  MonitoreoTI-Agente-Setup.exe /S /SERVER=https://servidor /TOKEN=<token>
;
; Servidor con certificado propio (por ejemplo, en la red local): copie MonitoreoTI-CA.pem junto al instalador y el
; agente confiará en ese certificado (queda en la carpeta de datos como ca-confianza.pem).
;
; Copia los archivos y deja el resto al anfitrión del servicio (resources\host\MonitoreoTI.AgentHost.exe):
; crea el servicio de Windows, protege la carpeta de datos y escribe config.json.

!include MUI2.nsh
!include nsDialogs.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include WordFunc.nsh

!define AGENT_SERVICE "MonitoreoTIAgent"
!define AGENT_HOST "$INSTDIR\resources\host\MonitoreoTI.AgentHost.exe"

!ifndef BUILD_UNINSTALLER
  Var AgentServer
  Var AgentToken
  Var AgentServerField
  Var AgentTokenField
!endif

; Antes de copiar o borrar archivos: detener el servicio y cerrar el agente (corre como SYSTEM, el
; chequeo estándar de electron-builder no puede cerrarlo y pediría al usuario que lo cierre).
!macro customCheckAppRunning
  nsExec::Exec '"$SYSDIR\sc.exe" stop ${AGENT_SERVICE}'
  Sleep 3000
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /T /IM "${APP_EXECUTABLE_FILENAME}"'
!macroend

!ifndef BUILD_UNINSTALLER

; /SERVER= y /TOKEN= desde la línea de comandos (instalación silenciosa o valores sugeridos).
!macro customInit
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/SERVER=" $AgentServer
  ClearErrors
  ${GetOptions} $R0 "/TOKEN=" $AgentToken
  ClearErrors
!macroend

!macro customPageAfterChangeDir
  Page custom AgentConfigPage AgentConfigLeave
!macroend

; Quita espacios y saltos de línea al principio y al final (al pegar se cuelan con facilidad).
!macro _TrimString Out In
  Push "${In}"
  Call TrimString
  Pop ${Out}
!macroend
!define Trim "!insertmacro _TrimString"

Function TrimString
  Exch $R0
  Push $R1
  trimLeft:
    StrCpy $R1 $R0 1
    ${If} $R1 == " "
    ${OrIf} $R1 == "$\r"
    ${OrIf} $R1 == "$\n"
    ${OrIf} $R1 == "$\t"
      StrCpy $R0 $R0 "" 1
      Goto trimLeft
    ${EndIf}
  trimRight:
    StrCpy $R1 $R0 1 -1
    ${If} $R1 == " "
    ${OrIf} $R1 == "$\r"
    ${OrIf} $R1 == "$\n"
    ${OrIf} $R1 == "$\t"
      StrCpy $R0 $R0 -1
      Goto trimRight
    ${EndIf}
  Pop $R1
  Exch $R0
FunctionEnd

Function AgentConfigPage
  ; En una actualización ya hay configuración: no se vuelve a pedir.
  ReadEnvStr $1 "PROGRAMDATA"
  ${If} ${FileExists} "$1\MonitoreoTI\Agent\config.json"
  ${AndIf} $AgentToken == ""
    Abort
  ${EndIf}

  !insertmacro MUI_HEADER_TEXT "Conexión con el servidor" "Datos que le dio el administrador de MonitoreoTI."
  nsDialogs::Create 1018
  Pop $0

  ${NSD_CreateLabel} 0 0 100% 12u "Dirección del servidor (por ejemplo, https://monitoreo.miempresa.com):"
  ${NSD_CreateText} 0 14u 100% 13u "$AgentServer"
  Pop $AgentServerField

  ${NSD_CreateLabel} 0 40u 100% 12u "Token de instalación de la sucursal:"
  ${NSD_CreateText} 0 54u 100% 13u "$AgentToken"
  Pop $AgentTokenField

  ${NSD_CreateLabel} 0 80u 100% 36u "El token se genera en el panel, en Instaladores. Solo se usa para registrar este equipo la primera vez."
  nsDialogs::Show
FunctionEnd

Function AgentConfigLeave
  ${NSD_GetText} $AgentServerField $AgentServer
  ${NSD_GetText} $AgentTokenField $AgentToken

  ; Si se pegó el comando completo ("... /SERVER=... /TOKEN=...") en cualquiera de los campos, se toman de ahí.
  ${GetOptions} "$AgentServer $AgentToken" "/SERVER=" $1
  ${IfNot} ${Errors}
    StrCpy $AgentServer $1
  ${EndIf}
  ClearErrors
  ${GetOptions} "$AgentServer $AgentToken" "/TOKEN=" $1
  ${IfNot} ${Errors}
    StrCpy $AgentToken $1
  ${EndIf}
  ClearErrors
  ${Trim} $AgentServer $AgentServer
  ${Trim} $AgentToken $AgentToken
  ${NSD_SetText} $AgentServerField $AgentServer
  ${NSD_SetText} $AgentTokenField $AgentToken

  ${If} $AgentServer == ""
    MessageBox MB_ICONEXCLAMATION "Escriba la dirección del servidor."
    Abort
  ${EndIf}
  ; El agente solo habla con el servidor cifrado (https). http solo se acepta con el propio equipo, para pruebas.
  StrCpy $1 $AgentServer 8
  StrCpy $2 $AgentServer 16
  ${If} $1 != "https://"
  ${AndIf} $2 != "http://localhost"
  ${AndIf} $2 != "http://127.0.0.1"
    MessageBox MB_ICONEXCLAMATION "La dirección del servidor debe empezar con https:// (por ejemplo, https://monitoreo.miempresa.com).$\r$\n$\r$\nPara pruebas en este mismo equipo se acepta http://localhost:5126."
    Abort
  ${EndIf}
  ${If} $AgentToken == ""
    MessageBox MB_ICONEXCLAMATION "Escriba el token de instalación."
    Abort
  ${EndIf}
  ; Un token válido empieza con el número de la empresa y un punto (ej. 1.abc…). Sin el punto se copió incompleto.
  ${WordFind} "$AgentToken" "." "E#" $1
  ${If} ${Errors}
  ${OrIf} $1 < 1
    ClearErrors
    MessageBox MB_ICONEXCLAMATION "El token parece incompleto: debe empezar con un número y un punto (por ejemplo, 1.nl-OVd…).$\r$\n$\r$\nCópielo de nuevo con el botón Copiar del panel."
    Abort
  ${EndIf}
FunctionEnd



!macro customInstall
  DetailPrint "Registrando el servicio del agente…"
  ${If} $AgentToken == ""
    ; Actualización: el anfitrión conserva la configuración existente.
    nsExec::ExecToStack '"${AGENT_HOST}" install --server "$AgentServer" --app "$INSTDIR"'
  ${Else}
    nsExec::ExecToStack '"${AGENT_HOST}" install --server "$AgentServer" --token "$AgentToken" --app "$INSTDIR"'
  ${EndIf}
  Pop $0 ; código de salida
  Pop $1 ; lo que explicó el anfitrión
  DetailPrint $1
  ; La autoridad propia (si vino junto al instalador) va a la carpeta de datos, que solo ven SYSTEM y los administradores.
  ${If} $0 == 0
  ${AndIf} ${FileExists} "$EXEDIR\MonitoreoTI-CA.pem"
    ReadEnvStr $2 "PROGRAMDATA"
    CopyFiles /SILENT "$EXEDIR\MonitoreoTI-CA.pem" "$2\MonitoreoTI\Agent\ca-confianza.pem"
    DetailPrint "Se instaló la autoridad de certificados de la empresa."
    nsExec::Exec '"$SYSDIR\sc.exe" stop ${AGENT_SERVICE}'
    Sleep 2000
    nsExec::Exec '"$SYSDIR\sc.exe" start ${AGENT_SERVICE}'
  ${EndIf}
  ${If} $0 != 0
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP "No se pudo registrar el servicio del agente (código $0).$\r$\n$\r$\n$1"
    ${EndIf}
    SetErrorLevel $0
    Abort
  ${EndIf}
!macroend

!endif

!macro customUnInstall
  ; En una actualización solo se quita el servicio: la credencial y la cola local se conservan.
  ${If} ${isUpdated}
    nsExec::ExecToLog '"${AGENT_HOST}" uninstall --keep-data --app "$INSTDIR"'
  ${Else}
    nsExec::ExecToLog '"${AGENT_HOST}" uninstall --app "$INSTDIR"'
  ${EndIf}
  Pop $0
!macroend
