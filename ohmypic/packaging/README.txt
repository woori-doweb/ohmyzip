ohMypic {VERSION} (Windows x64)
================================

[English]

An image browser and viewer for Windows. Browse folders as thumbnails, view images,
run a slideshow, and rotate, resize, convert or batch-edit images.

Requirements
------------
- Windows 10 1809 or later, or Windows 11 (x64)
- .NET Desktop Runtime 9 (x64). If it isn't installed, Windows offers to install it
  the first time you start ohMypic. This package doesn't include the runtime.

Running
-------
Run ohMypic.exe in the extracted folder. Pass a folder or an image path as an argument
to open it; with no argument it opens your Pictures folder.
There is no installer or code signature yet.
To use ohMypic for images, use Settings > File Associations (or run
ohMypic.exe --register-associations), then pick ohMypic in Windows Settings > Default apps.
Windows doesn't let apps change the default app by themselves.
To add an ohMypic submenu (Open, Slideshow, Rotate, Resize, Convert, Batch edit) when you
right-click an image in File Explorer, use Settings > File Explorer Menu, or run
ohMypic.exe --register-shell-menu. On Windows 11 it appears under Show more options.
--unregister-associations and --unregister-shell-menu remove them.
Only the current user is affected; no administrator rights are needed.
The display language follows Windows (Korean or English); change it in Settings.
ohMypic checks GitHub for a new version once a day and installs it when you close it.
You can turn this off in Settings > General.

Settings and data
-----------------
Settings: %APPDATA%\ohMypic\settings.json
Updates are downloaded to %LOCALAPPDATA%\ohMypic\updates.
To remove ohMypic, first unregister the File Explorer menu and file associations in
Settings (if you registered them), then delete the extracted folder and the
%APPDATA%\ohMypic folder.

Included files
--------------
ohMypic.exe, ohMypic.dll, OhMyPic.Core.dll,
OhMyPic.App.Text.dll, ko\*.resources.dll          ohMypic (ko = Korean text)
HELP.md, HELP.ko.md                               user help (English, Korean)
SHA256SUMS.txt                                    hashes for checking file integrity
ohMypic uses only Windows imaging (WIC) and .NET; it ships no third-party components.


[한국어]

Windows 용 이미지 탐색·보기 프로그램입니다. 폴더를 작은 그림으로 훑어보고, 이미지를 크게 보고,
연속 보기를 하고, 회전·크기 변경·포맷 변환·일괄 편집을 할 수 있습니다.

실행 조건
---------
- Windows 10 1809 이상 또는 Windows 11 (x64)
- .NET Desktop Runtime 9 (x64). 설치되어 있지 않으면 처음 실행할 때 Windows 가
  런타임 설치 안내를 보여 줍니다. 이 패키지는 런타임을 포함하지 않습니다.

실행
----
압축을 푼 폴더의 ohMypic.exe 를 실행합니다. 폴더나 이미지 경로를 인자로 주면 그것을 열고,
인자가 없으면 사진(Pictures) 폴더를 엽니다. 설치 프로그램과 코드 서명은 아직 없습니다.
이미지를 ohMypic 으로 열려면 환경 설정 > 파일 연결에서 등록하거나
ohMypic.exe --register-associations 를 실행한 뒤, Windows 설정 > 기본 앱에서 ohMypic 을 고릅니다.
Windows 는 프로그램이 기본 앱을 스스로 바꾸지 못하게 합니다.
탐색기에서 이미지를 오른쪽 클릭했을 때 ohMypic 하위 메뉴(열기, 연속 보기, 회전, 크기 변경,
포맷 변환, 일괄 편집)를 넣으려면 환경 설정 > 탐색기 메뉴에서 등록하거나
ohMypic.exe --register-shell-menu 를 실행합니다. Windows 11 에서는 '추가 옵션 표시' 아래에 나옵니다.
--unregister-associations, --unregister-shell-menu 로 해제합니다.
현재 사용자에만 적용되며 관리자 권한이 필요하지 않습니다.
화면 언어는 Windows 표시 언어를 따릅니다(한국어 또는 영어). 환경 설정에서 바꿀 수 있습니다.
하루에 한 번 GitHub 에서 새 버전을 확인하고, 프로그램을 닫을 때 설치합니다.
환경 설정 > 일반에서 끌 수 있습니다.

설정과 데이터
-------------
설정: %APPDATA%\ohMypic\settings.json
업데이트는 %LOCALAPPDATA%\ohMypic\updates 에 내려받습니다.
프로그램을 지우려면 먼저 환경 설정에서 탐색기 메뉴와 파일 연결을 해제하고(등록했다면),
압축을 푼 폴더와 %APPDATA%\ohMypic 폴더를 삭제합니다.

포함 파일
---------
ohMypic.exe, ohMypic.dll, OhMyPic.Core.dll,
OhMyPic.App.Text.dll, ko\*.resources.dll          ohMypic (ko = 한국어 문구)
HELP.md, HELP.ko.md                               사용 도움말 (영어, 한국어)
SHA256SUMS.txt                                    파일 무결성 확인용 해시
ohMypic 은 Windows 이미징(WIC)과 .NET 만 사용하며 제3자 구성요소를 포함하지 않습니다.
