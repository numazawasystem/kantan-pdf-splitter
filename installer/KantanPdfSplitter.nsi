Unicode true
RequestExecutionLevel user
ManifestSupportedOS Win10
ManifestDPIAware true
CRCCheck force
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WordFunc.nsh"
!include "FileFunc.nsh"
!include "TextFunc.nsh"
!include "StrFunc.nsh"
!include "x64.nsh"
${StrStr}
${UnStrStr}
!define NAME "かんたんPDF分割"
!define KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\KantanPdfSplitter"
!define APP "KantanPdfSplitter.exe"
!define UNINSTALL "KantanPdfSplitter-Uninstall.exe"
!define RUN_MUTEX "Local\KantanPdfSplitter.AppRunning"
!define MAINT_MUTEX "Local\KantanPdfSplitter.Maintenance"
Name "${NAME}"
Caption "${NAME} セットアップ"
OutFile "${OUTPUT_DIR}\KantanPdfSplitter-Setup-${PRODUCT_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\KantanPdfSplitter"
InstallDirRegKey HKCU "${KEY}" "InstallLocation"
BrandingText "名古屋テクノロジー"
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1041 "ProductName" "${NAME}"
VIAddVersionKey /LANG=1041 "CompanyName" "名古屋テクノロジー"
VIAddVersionKey /LANG=1041 "FileDescription" "${NAME} セットアップ"
VIAddVersionKey /LANG=1041 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1041 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1041 "LegalCopyright" "Copyright 2026 名古屋テクノロジー"
!define MUI_ICON "${ROOT}\branding\icons\app.ico"
!define MUI_UNICON "${ROOT}\branding\icons\app.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "${ROOT}\branding\installer-welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP "${ROOT}\branding\installer-header.bmp"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "「かんたんPDF分割」をこのユーザー向けにインストールします。$\r$\n$\r$\nインターネット接続や管理者権限は必要ありません。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP}"
!define MUI_FINISHPAGE_RUN_TEXT "かんたんPDF分割を起動する"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Japanese"
!ifdef SIGNTOOL_COMMAND
  !uninstfinalize '${SIGNTOOL_COMMAND} "%1"' = 0
  !finalize '${SIGNTOOL_COMMAND} "%1"' = 0
!endif
Var MaintenanceHandle

!macro Guard PREFIX
Function ${PREFIX}Guard
  SetShellVarContext current
  SetRegView 64
  System::Call 'kernel32::CreateMutexW(p 0,i 0,w "${MAINT_MUTEX}") p .r0 ?e'
  Pop $1
  StrCpy $MaintenanceHandle $0
  ${If} $0 = 0
  ${OrIf} $1 = 183
    MessageBox MB_OK|MB_ICONEXCLAMATION "別のセットアップが実行中です。終了してからお試しください。" /SD IDOK
    SetErrorLevel 10
    Quit
  ${EndIf}
  System::Call 'kernel32::OpenMutexW(i 0x00100000,i 0,w "${RUN_MUTEX}") p .r0'
  ${If} $0 P<> 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "かんたんPDF分割を終了してからお試しください。" /SD IDOK
    SetErrorLevel 10
    Quit
  ${EndIf}
FunctionEnd
!macroend
!insertmacro Guard ""
!insertmacro Guard "un."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "64ビット版Windows 10 / 11が必要です。" /SD IDOK
    SetErrorLevel 20
    Quit
  ${EndIf}
  Call Guard
  ReadRegStr $0 HKCU "${KEY}" "DisplayVersion"
  ${If} $0 != ""
    ${VersionCompare} "$0" "${PRODUCT_VERSION}" $1
    ${If} $1 = 1
      MessageBox MB_OK|MB_ICONEXCLAMATION "新しいバージョンがインストールされています。古いバージョンへ戻すことはできません。" /SD IDOK
      SetErrorLevel 12
      Quit
    ${EndIf}
  ${EndIf}
FunctionEnd
Function un.onInit
  Call un.Guard
FunctionEnd
Function .onGUIEnd
  System::Call 'kernel32::CloseHandle(p $MaintenanceHandle)'
FunctionEnd
Function un.onGUIEnd
  System::Call 'kernel32::CloseHandle(p $MaintenanceHandle)'
FunctionEnd

; Never recursively delete the installation directory. Read the previous
; package's ownership manifest; reject traversal, absolute paths and all PDFs.
!macro Cleanup PREFIX
Function ${PREFIX}Cleanup
  IfFileExists "$INSTDIR\owned-files.txt" 0 cleanup_done
  ClearErrors
  FileOpen $0 "$INSTDIR\owned-files.txt" r
  IfErrors cleanup_failed
  cleanup_loop:
    ClearErrors
    FileRead $0 $1
    IfErrors cleanup_close
    ${${PREFIX}TrimNewLines} "$1" $1
    StrCmp $1 "" cleanup_loop
    ${${PREFIX}StrStr} $2 "$1" ".."
    StrCmp $2 "" 0 cleanup_loop
    ${${PREFIX}StrStr} $2 "$1" ":"
    StrCmp $2 "" 0 cleanup_loop
    StrCpy $2 $1 1
    StrCmp $2 "\" cleanup_loop
    StrCmp $2 "/" cleanup_loop
    ${${PREFIX}GetFileExt} "$1" $2
    StrCmp $2 "pdf" cleanup_loop
    StrCmp $1 "owned-files.txt" cleanup_loop
    ClearErrors
    Delete "$INSTDIR\$1"
    IfErrors cleanup_close_failed
    ${${PREFIX}GetParent} "$INSTDIR\$1" $3
    cleanup_parents:
      StrCmp $3 "$INSTDIR" cleanup_loop
      StrCmp $3 "" cleanup_loop
      RMDir "$3"
      ${${PREFIX}GetParent} "$3" $4
      StrCpy $3 $4
      Goto cleanup_parents
    Goto cleanup_loop
  cleanup_close_failed:
    FileClose $0
    Goto cleanup_failed
  cleanup_close:
    FileClose $0
    Delete "$INSTDIR\owned-files.txt"
    ; Non-recursive removal succeeds only for empty known package directories.
    RMDir "$INSTDIR\licenses\dotnet"
    RMDir "$INSTDIR\licenses"
    RMDir "$INSTDIR\runtimes\win\lib\net8.0"
    RMDir "$INSTDIR\runtimes\win\lib"
    RMDir "$INSTDIR\runtimes\win"
    RMDir "$INSTDIR\runtimes"
    Goto cleanup_done
  cleanup_failed:
    MessageBox MB_OK|MB_ICONSTOP "アプリのファイルを更新・削除できませんでした。アプリを終了し、もう一度お試しください。" /SD IDOK
    SetErrorLevel 30
    Abort
  cleanup_done:
FunctionEnd
!macroend
!insertmacro Cleanup ""
!insertmacro Cleanup "un."

Section "かんたんPDF分割"
  Call Cleanup
  SetOutPath "$INSTDIR"
  ClearErrors
  File /r "${PAYLOAD_DIR}\*"
  IfErrors install_failed
  WriteUninstaller "$INSTDIR\${UNINSTALL}"
  IfErrors install_failed
  SetOutPath "$INSTDIR"
  CreateShortCut "$DESKTOP\${NAME}.lnk" "$INSTDIR\${APP}" "" "$INSTDIR\${APP}" 0
  CreateShortCut "$SMPROGRAMS\${NAME}.lnk" "$INSTDIR\${APP}" "" "$INSTDIR\${APP}" 0
  WriteRegStr HKCU "${KEY}" "DisplayName" "${NAME}"
  WriteRegStr HKCU "${KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${KEY}" "Publisher" "名古屋テクノロジー"
  WriteRegStr HKCU "${KEY}" "DisplayIcon" "$INSTDIR\${APP},0"
  WriteRegStr HKCU "${KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${KEY}" "UninstallString" '$\"$INSTDIR\${UNINSTALL}$\"'
  WriteRegStr HKCU "${KEY}" "QuietUninstallString" '$\"$INSTDIR\${UNINSTALL}$\" /S'
  WriteRegDWORD HKCU "${KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${KEY}" "EstimatedSize" ${SIZE_KB}
  Goto install_done
  install_failed:
    MessageBox MB_OK|MB_ICONSTOP "インストールを完了できませんでした。空き容量と保存先を確認し、もう一度お試しください。" /SD IDOK
    SetErrorLevel 30
    Abort
  install_done:
SectionEnd
Section "Uninstall"
  Call un.Cleanup
  Delete "$DESKTOP\${NAME}.lnk"
  Delete "$SMPROGRAMS\${NAME}.lnk"
  DeleteRegKey HKCU "${KEY}"
  Delete "$INSTDIR\${UNINSTALL}"
  RMDir "$INSTDIR"
SectionEnd
