ohmyZip {VERSION} (Windows x64)
================================

Windows 용 압축 관리자입니다. 압축파일을 열어 내용을 보고, 풀고, 테스트하고,
새로 만들거나 항목을 추가·삭제·이름 바꾸기 할 수 있습니다.

실행 조건
---------
- Windows 10 1809 이상 또는 Windows 11 (x64)
- .NET Desktop Runtime 9 (x64). 설치되어 있지 않으면 처음 실행할 때 Windows 가
  런타임 설치 안내를 보여 줍니다. 이 패키지는 런타임을 포함하지 않습니다.

실행
----
압축을 푼 폴더의 ohmyZip.exe 를 실행합니다. 압축파일 경로를 인자로 주면 그 파일을 엽니다.
설치 프로그램과 코드 서명은 아직 없습니다. 파일 연결·탐색기 메뉴 등록은 하지 않습니다.

설정과 데이터
-------------
설정: %APPDATA%\ohmyZip\settings.json
암호 관리자: %APPDATA%\ohmyZip\passwords.dat (Windows 데이터 보호로 암호화, 현재 사용자 전용)
프로그램을 지우려면 압축을 푼 폴더와 위 %APPDATA%\ohmyZip 폴더를 삭제합니다.

포함 파일
---------
ohmyZip.exe, ohmyZip.dll, OhMyZip.Core.dll        ohmyZip
SharpCompress.dll                                 제3자 구성요소 (MIT)
System.Security.Cryptography.ProtectedData.dll    제3자 구성요소 (MIT)
HELP.md                                           사용 도움말 (기능·단축키·준비 중 목록)
THIRD-PARTY-NOTICES.txt                           제3자 구성요소 라이선스 전문
SHA256SUMS.txt                                    파일 무결성 확인용 해시
