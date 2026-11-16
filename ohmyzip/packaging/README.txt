ohmyZip {VERSION} (Windows x64)
================================

[English]

An archive manager for Windows. Open an archive to view its contents, extract it,
test it, create new archives, and add, delete, or rename items.

Requirements
------------
- Windows 10 1809 or later, or Windows 11 (x64)
- .NET Desktop Runtime 9 (x64). If it isn't installed, Windows offers to install it
  the first time you start ohmyZip. This package doesn't include the runtime.

Running
-------
Run ohmyZip.exe in the extracted folder. Pass an archive path as an argument to open it.
There is no installer or code signature yet. To make ohmyZip the default app for
archives, use Settings > File Associations in ohmyZip.
To add an ohmyZip submenu when you right-click a file or folder in File Explorer, use
Settings > File Explorer Menu, or run ohmyZip.exe --register-shell-menu
(--unregister-shell-menu removes it). Archives get Open, the extract items, and the two
compress items; any other file or folder gets the two compress items (Compress... opens
the New Archive window, Compress to ZIP creates name.zip next to the items without
asking). If you select several items, ohmyZip runs only once for the whole selection.
On Windows 11 the menu is still under Show more options. Only the current user is
affected; no administrator rights are needed.
The display language follows Windows (Korean or English). To change it, use
Settings > Language; ohmyZip offers to restart when you click OK.
ohmyZip checks GitHub for a new version once a day and installs it when you close it.
You can turn this off in Settings > General.

Settings and data
-----------------
Settings: %APPDATA%\ohmyZip\settings.json
Password Manager: %APPDATA%\ohmyZip\passwords.dat (encrypted with Windows Data Protection, current user only)
Updates are downloaded to %LOCALAPPDATA%\ohmyZip\updates.
File Explorer menu selections are passed through short-lived list files in
%LOCALAPPDATA%\ohmyZip\selections (deleted after reading).
To remove ohmyZip, first unregister the File Explorer menu and file associations in
Settings (if you registered them), then delete the extracted folder and the
%APPDATA%\ohmyZip folder.

Included files
--------------
ohmyZip.exe, ohmyZip.dll, OhMyZip.Core.dll,
OhMyZip.App.Text.dll, ko\*.resources.dll          ohmyZip (ko = Korean text)
ShellCommand.dll                                  ohmyZip File Explorer menu command handler
SharpCompress.dll                                 third-party component (MIT)
System.Security.Cryptography.ProtectedData.dll    third-party component (MIT)
HELP.en.md, HELP.md                               user help (English, Korean)
THIRD-PARTY-NOTICES.txt                           full third-party license texts
SHA256SUMS.txt                                    hashes for checking file integrity


[한국어]

Windows 용 압축 관리자입니다. 압축 파일을 열어 내용을 보고, 풀고, 테스트하고,
새로 만들거나 항목을 추가·삭제·이름 바꾸기 할 수 있습니다.

실행 조건
---------
- Windows 10 1809 이상 또는 Windows 11 (x64)
- .NET Desktop Runtime 9 (x64). 설치되어 있지 않으면 처음 실행할 때 Windows 가
  런타임 설치 안내를 보여 줍니다. 이 패키지는 런타임을 포함하지 않습니다.

실행
----
압축을 푼 폴더의 ohmyZip.exe 를 실행합니다. 압축 파일 경로를 인자로 주면 그 파일을 엽니다.
설치 프로그램과 코드 서명은 아직 없습니다. 압축 파일의 기본 프로그램으로 쓰려면
ohmyZip 의 환경 설정 > 파일 연결에서 등록합니다.
탐색기에서 파일이나 폴더를 오른쪽 클릭했을 때 ohmyZip 하위 메뉴를 넣으려면 환경 설정 >
탐색기 메뉴에서 등록하거나 ohmyZip.exe --register-shell-menu 를 실행합니다
(--unregister-shell-menu 로 해제). 압축 파일에는 열기·풀기 항목과 압축하기 두 항목이, 그 밖의
파일과 폴더에는 압축하기 두 항목이 나타납니다('압축하기...' = 새 압축 창 열기,
'ZIP으로 압축' = 묻지 않고 항목 옆에 이름.zip 만들기). 여러 항목을 골라도 ohmyZip 은
한 번만 실행됩니다. Windows 11 에서는 여전히 '추가 옵션 표시' 아래에 나타납니다.
현재 사용자에만 적용되며 관리자 권한이 필요하지 않습니다.
화면 언어는 Windows 표시 언어를 따릅니다(한국어 또는 영어). 바꾸려면 환경 설정 > 언어 설정에서
고르고 확인을 누르면 다시 시작할지 묻습니다.
하루에 한 번 GitHub 에서 새 버전을 확인하고, 프로그램을 닫을 때 설치합니다.
환경 설정 > 일반 설정에서 끌 수 있습니다.

설정과 데이터
-------------
설정: %APPDATA%\ohmyZip\settings.json
암호 관리자: %APPDATA%\ohmyZip\passwords.dat (Windows 데이터 보호로 암호화, 현재 사용자 전용)
업데이트는 %LOCALAPPDATA%\ohmyZip\updates 에 내려받습니다.
탐색기 메뉴에서 고른 항목은 %LOCALAPPDATA%\ohmyZip\selections 의 짧은 목록 파일로 넘기며
읽은 뒤 지웁니다.
프로그램을 지우려면 먼저 환경 설정에서 탐색기 메뉴와 파일 연결을 해제하고(등록했다면),
압축을 푼 폴더와 위 %APPDATA%\ohmyZip 폴더를 삭제합니다.

포함 파일
---------
ohmyZip.exe, ohmyZip.dll, OhMyZip.Core.dll,
OhMyZip.App.Text.dll, ko\*.resources.dll          ohmyZip (ko = 한국어 문구)
ShellCommand.dll                                  ohmyZip 탐색기 메뉴 명령 처리기
SharpCompress.dll                                 제3자 구성요소 (MIT)
System.Security.Cryptography.ProtectedData.dll    제3자 구성요소 (MIT)
HELP.en.md, HELP.md                               사용 도움말 (영어, 한국어)
THIRD-PARTY-NOTICES.txt                           제3자 구성요소 라이선스 전문
SHA256SUMS.txt                                    파일 무결성 확인용 해시
