StoryRest 설정 안내
====================

이 폴더(StreamingAssets)의 설정 파일과 프로그램 안의 설정창(ESC)이 무엇을 정하는지 적어 둡니다.
콘텐츠(영상·그림) 넣는 법은 floor_1/README.txt 를 보세요.


0. 먼저 알아 둘 것 — 파일이 실제로 읽히는 자리
----------------------------------------------
Setting.json 과 aruco.json 은 이 폴더의 것이 그대로 쓰이는 게 아닙니다.
프로그램을 처음 켤 때 아래 자리로 한 번 복사되고, 그 뒤로는 거기 있는 파일을 읽고 씁니다.

  C:\Users\<사용자>\AppData\LocalLow\DefaultCompany\StoryRest\
    Setting.json     ← 층 · 역할
    aruco.json       ← 카메라 · 마커 · 보정값 (편집모드에서 저장하면 여기에 씀)
    aruco.json.bak   ← 직전 값 백업 (저장할 때마다 갱신)
    stats\           ← 관람 기록 CSV (views_YYYY-MM-DD.csv)

  그래서 한 번 켠 뒤에 StreamingAssets 쪽 파일을 고쳐도 반영되지 않습니다.
  위 자리의 파일을 고치거나, 지우고 다시 켜면(그러면 StreamingAssets 것이 다시 복사됨) 됩니다.
  정확한 경로는 프로그램 시작 로그의 "설정 파일:" / "층 설정:" 줄에 찍힙니다.

keywordwall.json 은 예외로 이 폴더의 것을 바로 읽습니다.


1. Setting.json — 이 PC 의 층과 역할
-------------------------------------
바꾸면 프로그램을 다시 시작해야 적용됩니다. ESC 설정창 맨 아래에서도 바꿀 수 있습니다.

  floor      1 / 2 / 3.  이 PC 가 설치된 층. floor_<N>/ 폴더의 콘텐츠와 그 층 번호가 든 세트·월을 씁니다.
  role       all / aruco / wall.
               all   = PC 1대가 카메라·마커·영상 + 키워드 월을 다 맡음 (1층). 통신 없음.
               aruco = 카메라·마커·영상만. 관람 기록을 statsPort 로 월 PC 에 보냄 (2·3층 ArUco PC).
               wall  = 키워드 월만. statsHost:statsPort 의 ArUco PC 에 붙어 관람 기록을 받음 (2·3층 월 PC).
  statsHost  wall 일 때 같은 층 ArUco PC 의 IP. all 은 안 씀.
  statsPort  aruco 가 여는 포트 = wall 이 붙는 포트 (기본 5100). 양쪽 같아야 하고 ArUco PC 방화벽에서 열어 둡니다.
  help       설명 글. 읽을 때 무시됩니다.

  PC 별 값
    1층            floor 1, role all
    2층 ArUco PC   floor 2, role aruco
    2층 월 PC      floor 2, role wall, statsHost = 2층 ArUco PC 의 IP
    3층            위와 같고 floor 3


2. aruco.json — 카메라 · 마커 · 투사 보정
-----------------------------------------
대부분은 편집모드(F2)와 ESC 설정창이 대신 써 주므로 손으로 고칠 일은 아래 몇 개뿐입니다.

  [손으로 고치는 값]
  dictionaryId           마커 딕셔너리 번호. 2 = DICT_4X4_250 (ID 0~249). 인쇄한 마커와 반드시 같아야 합니다.
                         (0~99 번은 예전 DICT_4X4_100 과 같은 그림이라 이미 인쇄한 마커는 그대로 씁니다)
  calibrationMarkerIds   화면 보정용으로 예약한 마커 4개 [107, 108, 109, 110]. 콘텐츠 폴더 번호와 겹치면 안 됩니다.
                         → 콘텐츠에 쓸 수 있는 번호는 0~106 입니다.
  sets[]                 카메라 1대 + 프로젝터 1대 짝. 이 층 번호(floors)가 든 세트만 만들어집니다.
    name                   로그·기록에 찍히는 이름 (A, B)
    floors                 이 세트를 쓰는 층 목록
    displayIndex           프로젝터(디스플레이) 번호. 0 이 주 화면. keywordwall.json 의 월 번호와 겹치면 안 됩니다.
    camera.deviceId        카메라 번호 (0, 1, …). 세트마다 달라야 합니다.
    camera.backend         0 = DirectShow(권장) / 1 = 자동 / 2 = MediaFoundation
    camera.width/height/fps  검출용 해상도. 기본 1280x720 30fps. 낮을수록 CPU 를 아낍니다.
    camera.fourcc          "MJPG" 권장. 비우면 드라이버 기본값(대개 느려짐).
    camera.flipHorizontal/flipVertical  카메라가 거울상을 내보낼 때만 true.
    showCameraPreview      카메라 영상을 프로젝터에 같이 투사(점검용). 전시 전에 반드시 false. (C 키로도 토글)

  [편집모드 · 설정창이 관리하는 값 — 직접 고칠 필요 없음]
  smoothing              마커 움직임 부드럽기. 0 = 즉시(떨림), 클수록 부드럽지만 늦게 따라옴.
  holdSeconds            마커를 놓친 뒤 콘텐츠를 몇 초 더 그려 둘지 (손이 스칠 때 깜빡임 방지).
  viewMinDwellSeconds    이 시간 이상 잡혀야 관람 1회로 셈.
  viewResumeGraceSeconds 놓친 뒤 이 시간 안에 돌아오면 같은 관람 · 영상은 멈춘 자리에서 이어서. 넘기면 처음으로.
  recordViews            관람 기록을 파일로 남길지.
  maxSimultaneous        세트 하나가 동시에 띄울 마커 수. 0 = 제한 없음.
  perspectiveMapping     true = 원근까지 맞춤 / false = 회전·크기만 (마커가 작아 심하게 떨릴 때).
  warpSubdivisions       원근 격자 분할 수. 6 이상.
  maxConcurrentVideos    세트당 동시에 돌릴 영상 수 (기본 4).
  maxCachedImages        메모리에 올려 둘 이미지 수 (기본 24).
  appear                 등장 연출. enabled / effect(none·grow·fade·topReveal·spiral·wave) / ring(빛 고리) /
                         recognizeSeconds(읽는 시간) / appearSeconds(등장 시간) / settleSeconds(멈춤 판정) /
                         moveThreshold(움직임 기준).
  manualTargetSize       수동 보정 때 "마커를 여기 놓으세요" 사각형 크기. 편집모드에서 +/- 로 맞춤.
  adjustPositionStep / adjustScaleStep / adjustRotationStep  편집모드에서 키 한 번에 움직이는 양.
  sets[].calibration     카메라↔프로젝터 대응점. 편집모드 보정(F3)이 채웁니다. valid 가 true 여야 투사가 맞습니다.
  sets[].globalScale / globalOffsetX / globalOffsetY   세트 전체 크기·위치 보정.
  sets[].markers[]       마커별 보정(scale, offsetX/Y, rotationOffset, enabled)과 파일별 보정(items[]).

  [점검용 — 전시 중엔 꺼 둘 것]
  editorPreview          에디터에서만 세트들을 한 화면에 나눠 그림. 빌드에서는 무시.
  debugPreviewContent    카메라 없이 콘텐츠를 격자로 늘어놓음 (F6). 파일이 제대로 들어갔는지 볼 때만.
  autoScanIntervalFrames 인쇄된 마커의 딕셔너리를 모를 때 전체 딕셔너리를 훑는 진단 기능. 평소 0.


3. keywordwall.json — 벽면(키워드 월) 화면
-------------------------------------------
이 폴더의 파일을 바로 읽습니다. 바꾸면 프로그램을 다시 시작해야 합니다.

  [어느 프로젝터에 띄우나]
  enabled          false 면 월을 만들지 않음.
  walls[]          월 화면 하나에 한 줄. displayIndex(프로젝터 번호) + floors(층) + roles(역할) 로 고릅니다.
                   기본값:
                     1층 all  → 디스플레이 1, 2   (0 은 ArUco 세트)
                     2·3층 wall → 디스플레이 0, 1  (월 PC 에는 세트가 없어 0 부터)
                   세트(aruco.json 의 displayIndex)와 겹치면 시작할 때 경고가 납니다.

  [무엇이 떠다니나]
  - floor_<N>/<마커ID>/sprite/ 에 그림이 하나라도 있으면 → 그림(스프라이트)이 떠다닙니다.
  - 하나도 없으면 → floor_<N>/keywords.txt 의 낱말이 떠다닙니다.
  minSpriteFolders / maxSpriteFolders
                   그림이 있는 폴더 중 몇 개를 띄울지 (기본 5~10). 켤 때마다 이 범위에서 개수를 뽑고 폴더를 무작위로 고릅니다.
                   둘 다 0 이면 전부.
  maxOnScreen      한 화면에 동시에 떠 있는 개수 (기본 16).
  minSpriteSize / maxSpriteSize   그림 크기 범위 (긴 변, 1920x1200 기준 픽셀). 기본 360~760 — 가로로 긴 글자 이미지 기준.
  spriteGradients  흰 글씨 이미지에 입힐 그라데이션 목록 [{from, to}, …]. 낱말마다 하나를 무작위로 고릅니다.
                   비우면 원본 색 그대로. 색 있는 이미지에 쓰면 곱해져 탁해집니다. 카드 그림에도 같이 적용.
  spriteGradientAngle  그라데이션 방향(도). 0 = 왼쪽→오른쪽, 90 = 위→아래.
  minFontSize / maxFontSize       낱말 글자 크기 범위.
  minSpeed / maxSpeed             이동 속도. 화면 전체를 0~1 로 본 초당 이동량.
  minAlpha / maxAlpha             투명도 범위.
  minLifetime / maxLifetime       하나가 머무는 시간(초). 지나면 사라지고 다른 것이 나옵니다.
  fadeSeconds      나타나고 사라지는 시간.
  motions          움직임 종류. Drift(직선) Wave(물결) Orbit(궤도) Bob(제자리) Zigzag(지그재그). 지우면 그 움직임은 안 씀.
  colors           낱말 색 목록 (#RRGGBB). 그림에는 적용되지 않습니다.
  backgroundColor  배경색. 프로젝터는 검정 = 빛 없음이므로 검정 유지.
  avoidDuplicates  같은 것이 화면에 두 개 뜨지 않게.
  separation / separationPadding  서로 겹쳤을 때 밀어내는 세기와 여백.
  edgeMargin       화면 가장자리 여백(비율). 렌즈 왜곡이 크거나 모서리가 벽에 걸리면 올립니다.

  [왼쪽 위 주제 카드 — spotlight]
  네 장이 돌아가며 뜹니다: 오늘의 추천 · 오늘의 인기 주제 · 이번 주 인기 주제 · 이달의 인기 주제.
  카드는 제목 + 그 마커의 sprite/ 첫 그림입니다. 그림이 없는 마커는 카드에 안 나옵니다.
  인기 = 관람 기록의 횟수 (오늘 / 최근 7일 / 최근 30일). 기록이 없는 기간의 카드는 건너뜁니다.
  추천 = 그림 있는 폴더 중 날짜로 정해지는 하나 (하루 동안 같음).
  카드가 뜨기 직전에 그 자리의 낱말·그림이 먼저 비켜나고, 카드가 사라지면 다시 그 자리를 씁니다.

    enabled          false 면 카드 없음.
    intervalSeconds  카드가 뜨는 간격(초). 한 장이 뜨고 다음 장이 뜰 때까지. 기본 60.
    cardSeconds      한 장이 떠 있는 시간. 기본 12.
    fadeSeconds      뜨고 질 때 페이드 시간.
    showTopicName    true 면 제목 아래에 폴더 이름의 제목 부분("17_별의일생" → 별의일생)을 한 줄 넣음. 기본 false.
    refreshSeconds   관람 기록을 다시 세는 주기. 기록이 한 건 적힐 때도 다시 셉니다.
    margin / width / imageSize   카드 자리 (1920x1200 기준 픽셀). 왼쪽 위에서 margin 만큼 띄움.
    recommendLabel / todayLabel / weekLabel / monthLabel   카드 제목 글.
    accentColor      제목 글자색.


4. floor_<N>/keywords.txt — 낱말 목록
--------------------------------------
한 줄에 하나. # 으로 시작하면 주석. 스프라이트가 없을 때만 쓰입니다. 바꾸면 재시작.


5. ESC 설정창 (프로그램 안)
---------------------------
ESC 로 열고 닫습니다. 값을 움직이면 바로 적용되고 1초 뒤 aruco.json 에 저장됩니다.

  ArUco — 현장 조절값
    콘텐츠 유지 · 관람 최소 유지 · 놓침 복귀 유예 · 움직임 부드럽게 · 동시 표시 마커 수 · 원근 매핑 · 관람 기록
  등장 연출
    켜기 · 연출 종류 · 빛 고리 · 읽는 시간 · 등장 시간 · 멈춤 판정 · 움직임 기준
  이 PC 의 층 · 역할 (Setting.json)
    층 · 역할 — 이 둘만 재시작해야 적용됩니다. 저장은 바로 됩니다.
  오른쪽 판: 관람 기록 링크의 연결 상태와 송수신 로그 (2·3층에서 두 PC 가 붙었는지 확인)

  카메라 번호 · 디스플레이 번호 · 세트 구성처럼 한 번 정하면 끝나는 값은 설정창에 없습니다. aruco.json 을 직접 고칩니다.


6. 단축키
---------
  F1   단축키 안내
  F2   편집모드 (마커 위치·크기·회전 보정, 세트 보정)
  F3   (편집모드 안) 카메라↔프로젝터 보정 단계
  F4   관람 기록 패널 (오늘 집계)
  F5   (편집모드 안) 콘텐츠 다시 읽기 — video/ 폴더 파일을 바꾼 뒤 재시작 없이 반영
  F6   디버그 격자 — 카메라 없이 콘텐츠 파일 점검
  F7   관람 기록 링크 상태 (월 PC 연결 · 마지막 송수신)
  C    카메라 영상 투사 켜고 끄기 (점검용, 설정에 저장됨)
  ESC  설정창

  편집모드 안 (F2 로 들어간 뒤)
    F3                      단계 전환 (마커 배치 ↔ 코너 보정)
    1 ~ 9                   조정할 세트 선택
    Tab / Shift+Tab         마커 선택
    , / .                   그 마커 안에서 파일 선택 (마커 전체 ↔ 파일 한 장)
    ← → ↑ ↓  + -            세트 전체 위치 · 크기
    Ctrl + ← → ↑ ↓  + -  [ ]  선택한 대상 위치 · 크기 · 회전
    PageUp / PageDown       세트 전체 배율
    Space                   코너 보정 진행 (자동 → 수동 → 점 확정 → 완료)
    + -  (수동 보정 중)     조준 사각형 크기
    Backspace / Ctrl+Backspace  세트 전체 초기화 / 선택한 대상 초기화
    V  D  P  F5             콘텐츠 표시 · 마커 테두리 · 원근 · 콘텐츠 다시 읽기
    H                       안내판 자리 옮기기
    Enter                   즉시 저장 (그 밖에는 잠시 무입력 시 자동 저장)
    Shift 병행              미세조정


7. 그 밖의 파일
---------------
  port.json / tcp.json   이전 프로젝트의 RS232 · TCP 설정. 지금 전시 기능에서는 쓰지 않습니다. 건드리지 마세요.
  aruco.json.bak         이 폴더의 것은 개발용 백업이며 무시해도 됩니다.
