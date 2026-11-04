# ohmyZip 개발 진입 지침

이 폴더를 고치기 전에 이 순서로 읽는다. 상세는 링크한 문서가 정본이다.

1. `../wiki/ohmyzip-plan.md` — 현재 단계(M0~M6)·엔진·위험. **계획에 없는 일을 하려면 먼저 계획을 고치고 이유를 run 기록에 남긴다.**
2. `../wiki/ohmyzip-acceptance-tests.md` — 지금 단계의 OZ-* 시험. 시험이 통과하기 전에는 다음 단계로 가지 않는다.
3. `../wiki/ohmyzip-design.md` — UI 를 바꿀 때만. 결정 ID(D01~D45)를 커밋 메시지·코드 주석이 아니라 traceability 에 적는다.
4. `../data/ohmyzip/traceability.json` — 기능·명령 96행. `plan_status` 는 `build_traceability.py` 의 MAP 에서만, `result` 는 시험 실행 뒤에만 바꾼다.
5. 실행 기록: `../runs/2026-10-26-ohmyzip/README.md` (명령·종료코드·해시·캡처·실패·복구).

## 구조

```
ohmyzip/
  OhMyZip.sln
  src/OhMyZip.Core/     엔진 어댑터(UI 의존 없음). IArchiveEngine, ZipEngine(BCL), SevenZipReadEngine(SharpCompress)
  src/OhMyZip.App/      WPF 앱. 명령은 traceability 의 ohmyzip_command 키로 이름 붙인다
  tests/OhMyZip.Core.Tests/  xunit. OZ-E* 이름을 테스트 이름 앞에 붙인다
  dist/                 dotnet publish 결과(생성물)
```

## 규칙

- 빌드·시험: `dotnet build ohmyzip/OhMyZip.sln`, `dotnet test ohmyzip/tests/OhMyZip.Core.Tests`. 출력은 run `logs/` 에 남긴다.
- fixtures 는 `../data/ohmyzip/fixtures/` (또는 `OHMYZIP_FIXTURES` 환경 변수)를 읽기 전용 원천으로 시험 임시 폴더에 복사해 쓴다. 원본을 고치지 않는다. fixtures 는 이 폴더(공개 저장소)에 넣지 않는다.
- 이 폴더의 코드·주석·UI 문구·문서·파일 이름에 다른 압축 프로그램의 제품명·회사명·실행 파일명을 쓰지 않는다(2026-11-03 사용자 지시). 분석 자료는 `../sources/`·`../wiki/`·`../runs/` 에 둔다.
- 미구현 명령은 메뉴에 두되 비활성 + "(준비 중)". 동작하지 않는 버튼을 활성으로 두지 않는다.
- 다른 제품의 DLL·아이콘·스킨·문구 이미지를 쓰지 않는다. SF Pro·SF Symbols 를 번들하지 않는다.
- 외부 프로세스는 인자 배열로 실행한다. 공식 배포본 해시를 기록하기 전에는 새 실행 파일을 실행하지 않는다.
- 문서를 고친 뒤 `python -X utf8 "$USERPROFILE/.gjc/agent/skills/doc-graph/scripts/docgraph.py" check --store rea-wiki` FAIL 0.
