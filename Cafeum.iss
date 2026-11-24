; Cafeum 설치 프로그램 스크립트 (Inno Setup)
; publish 폴더의 self-contained 단일 exe를 그대로 배포한다. 관리자 권한 없이(현재 사용자 전용) 설치된다.
; 커피뷰어(CoffeeViewer.iss)와는 별개 프로그램이라 AppId · 설치 폴더 · 바로가기가 모두 따로다.
; 탐색기 우클릭 "Cafeum으로 보기"(클래식 메뉴)는 등록한다. 윈도우 11 새 우클릭 메뉴 직접 노출은 아직 넣지 않았다.

#define MyAppName "Cafeum"
#define MyAppDisplayName "Cafeum"
#define MyAppVersion "1.4.14"
#define MyAppPublisher "JuneSilver"
#define MyAppExeName "Cafeum.exe"

[Setup]
AppId={{B3A5E0C4-7D1F-4E8A-9A52-5C2D6F0B1E37}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppDisplayName}
DisableProgramGroupPage=yes
UsePreviousGroup=no
PrivilegesRequired=lowest
OutputDir=Install
OutputBaseFilename=Cafeum-Setup-{#MyAppVersion}
SetupIconFile=Img\Used\CafeumTaskbar.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 아이콘 만들기"; GroupDescription: "추가 아이콘:"

[InstallDelete]
; 예전 이름(CafeumOffice.exe)으로 설치돼 있던 사용자가 업데이트하면, 옛 exe가 남지 않게 지운다.
Type: files; Name: "{app}\CafeumOffice.exe"

[Files]
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppDisplayName} 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppDisplayName} 실행"; Flags: nowait postinstall

[Registry]
; 탐색기 오른쪽 클릭 메뉴 "Cafeum으로 보기"(현재 사용자 전용 · 관리자 권한 불필요 · Windows 10 / 11 공통). 앱도 실행할 때 같은 항목을 맞춰 둔다.
; 제거하면 이 키들도 함께 지워진다(uninsdeletekey).
; 이름을 바꾸기 전 버전("Caffeum")의 옛 우클릭 항목은 설치할 때 지운다(남아 있으면 메뉴가 두 개 나온다).
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.epub\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.cbz\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwp\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwpx\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\Caffeum"; Flags: deletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.epub\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.epub\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.epub\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.cbz\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.cbz\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.cbz\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwp\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwp\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwp\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwpx\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwpx\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.hwpx\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\Cafeum"; ValueType: string; ValueData: "Cafeum으로 보기"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\Cafeum"; ValueName: "Icon"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\Cafeum\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
