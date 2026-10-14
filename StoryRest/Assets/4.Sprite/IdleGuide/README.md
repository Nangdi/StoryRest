# IdleGuide — 바닥 대기 안내 글자

"알아보고 싶은 아카이브 북을 골라 테이블 위에 올려보세요" 를 음절마다 한 장씩 구운 흰 글씨(2배 크기, 투명 배경).
색은 굽지 않는다 — `ArUcoIdleGuide` 가 층별 그라데이션을 입힌다.

- `Glyph_NN.png` — 읽는 순서. 공백은 장이 없다. 모든 장의 세로 범위가 같아 나란히 놓으면 기준선이 맞는다.
- `glyphs.json` — 각 장의 가로 자리(`center`, 1배 px, 줄 가운데 = 0)와 PSD 의 줄 위치.
- 다시 만들기: `python Tools/idle_export/export_idle.py` (원본: 바탕화면 `전북과_스토리쉼터-대기화면_260928.psd`).
  같은 이름으로 덮어써 GUID 가 유지된다. 글자 수가 바뀌면 `ArUcoSet.prefab` › `IdleGuide` 의 글자 칸도 다시 만든다.

자세한 동작은 `docs/ARCHITECTURE.md` §5 "바닥 대기 안내".
