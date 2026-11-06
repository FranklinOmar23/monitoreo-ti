; Instalador del agente MonitoreoTI (NSIS, incluido por electron-builder).
;
; Interactivo: pide la dirección del servidor y el token de instalación de la sucursal.
; Silencioso:  MonitoreoTI-Agente-Setup.exe /S /SERVER=https://servidor /TOKEN=<token> [/TIPO=servidor]
;
; /TIPO=servidor marca el equipo como servidor (habilita los respaldos, decisión 82). Sin él, el agente lo detecta solo
; (las ediciones Windows Server quedan como servidor).
;
; Servidor con certificado propio (por ejemplo, en la red local): copie MonitoreoTI-CA.pem junto al instalador y el
; agente confiará en ese certificado (queda en la carpeta de datos como ca-confianza.pem).
;
; Copia los archivos y deja el resto al anfitrión del servicio (resources\host\MonitoreoTI.AgentHost.exe):
; crea el servicio de Windows, protege la carpeta de datos y escribe config.json.
;
; Un solo instalador para todos los Windows (decisión 84): en Windows 7, 8 y 8.1 (compilación anterior a 10240), donde
; no corren Electron ni .NET 8, instala el agente clásico (resources\host\MonitoreoTI.AgenteClasico.exe, .NET Framework
; 4.5.2) con las mismas capacidades remotas. Mismo servicio (MonitoreoTIAgent) y misma carpeta de datos.

!include MUI2.nsh
!include nsDialogs.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include WordFunc.nsh

!define AGENT_SERVICE "MonitoreoTIAgent"
!define AGENT_HOST "$INSTDIR\resources\host\MonitoreoTI.AgentHost.exe"
!define AGENT_CLASSIC "$INSTDIR\resources\host\MonitoreoTI.AgenteClasico.exe"

; $R9 = 1 si este Windows necesita el agente clásico (anterior a Windows 10).
!macro _DetectClassic
  StrCpy $R9 0
  ReadRegStr $R8 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $R8 != ""
  ${AndIf} $R8 < 10240
    StrCpy $R9 1
  ${EndIf}
!macroend
!define DetectClassic "!insertmacro _DetectClassic"

!ifndef BUILD_UNINSTALLER
  Var AgentServer
  Var AgentToken
  Var AgentServerField
  Var AgentTokenField
  Var AgentType
  Var AgentTypeField
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
  ${GetOptions} $R0 "/TIPO=" $AgentType
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
  ${If} $AgentToken == ""
    ${If} ${FileExists} "$1\MonitoreoTI\Agent\config.json"
    ${OrIf} ${FileExists} "$1\MonitoreoTI\Agent\agente-clasico.json"
      Abort
    ${EndIf}
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

  ${NSD_CreateLabel} 0 80u 100% 24u "El token se genera en el panel, en Instaladores. Solo se usa para registrar este equipo la primera vez."

  ${NSD_CreateCheckbox} 0 108u 100% 12u "Este equipo es un servidor (habilita los respaldos)"
  Pop $AgentTypeField
  ${If} $AgentType == "servidor"
    ${NSD_Check} $AgentTypeField
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function AgentConfigLeave
  ${NSD_GetText} $AgentServerField $AgentServer
  ${NSD_GetText} $AgentTokenField $AgentToken
  ${NSD_GetState} $AgentTypeField $1
  ${If} $1 == ${BST_CHECKED}
    StrCpy $AgentType "servidor"
  ${ElseIf} $AgentType == "servidor"
    StrCpy $AgentType ""
  ${EndIf}

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
  ${DetectClassic}
  ${If} $R9 == 1
    !insertmacro InstallClassic
  ${Else}
    !insertmacro InstallHost
  ${EndIf}
!macroend

; Windows 7 / 8 / 8.1: el agente clásico se registra, se copia a %ProgramFiles%\MonitoreoTI\AgenteClasico y crea el servicio.
!macro InstallClassic
  DetailPrint "Windows anterior a 10: se instala el agente clásico…"
  ; .NET Framework 4.5.2 o posterior (Release 379893).
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 == ""
  ${OrIf} $0 < 379893
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP "Este equipo necesita .NET Framework 4.5.2 o posterior (se recomienda 4.8) para el agente de MonitoreoTI.$\r$\n$\r$\nInstálelo desde Windows Update o el sitio de Microsoft y vuelva a ejecutar este instalador."
    ${EndIf}
    SetErrorLevel 3
    Abort
  ${EndIf}
  ; El clásico toma la autoridad propia de su misma carpeta.
  ${If} ${FileExists} "$EXEDIR\MonitoreoTI-CA.pem"
    CopyFiles /SILENT "$EXEDIR\MonitoreoTI-CA.pem" "$INSTDIR\resources\host\MonitoreoTI-CA.pem"
  ${EndIf}
  StrCpy $3 ""
  ${If} $AgentType == "servidor"
    StrCpy $3 " /TIPO=servidor"
  ${EndIf}
  ${If} $AgentToken == ""
    ; Actualización: conserva el registro del equipo y reemplaza el programa.
    nsExec::ExecToStack '"${AGENT_CLASSIC}" instalar'
  ${Else}
    nsExec::ExecToStack '"${AGENT_CLASSIC}" instalar "/SERVER=$AgentServer" "/TOKEN=$AgentToken"$3'
  ${EndIf}
  Pop $0
  Pop $1
  DetailPrint $1
  ${If} $0 != 0
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP "No se pudo instalar el agente clásico (código $0).$\r$\n$\r$\n$1$\r$\nDetalle en C:\ProgramData\MonitoreoTI\Agent\agente-clasico.log"
    ${EndIf}
    SetErrorLevel $0
    Abort
  ${EndIf}
!macroend

!macro InstallHost
  DetailPrint "Registrando el servicio del agente…"
  StrCpy $3 ""
  ${If} $AgentType == "servidor"
    StrCpy $3 ' --tipo servidor'
  ${EndIf}
  ${If} $AgentToken == ""
    ; Actualización: el anfitrión conserva la configuración existente.
    nsExec::ExecToStack '"${AGENT_HOST}" install --server "$AgentServer" --app "$INSTDIR"$3'
  ${Else}
    nsExec::ExecToStack '"${AGENT_HOST}" install --server "$AgentServer" --token "$AgentToken" --app "$INSTDIR"$3'
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
  ${DetectClassic}
  ${If} $R9 == 1
    ; Agente clásico: misma regla, en una actualización se conserva el registro del equipo.
    ${If} ${isUpdated}
      nsExec::ExecToLog '"${AGENT_CLASSIC}" desinstalar /CONSERVAR'
    ${Else}
      nsExec::ExecToLog '"${AGENT_CLASSIC}" desinstalar'
    ${EndIf}
  ; En una actualización solo se quita el servicio: la credencial y la cola local se conservan.
  ${ElseIf} ${isUpdated}
    nsExec::ExecToLog '"${AGENT_HOST}" uninstall --keep-data --app "$INSTDIR"'
  ${Else}
    nsExec::ExecToLog '"${AGENT_HOST}" uninstall --app "$INSTDIR"'
  ${EndIf}
  Pop $0
!macroend
