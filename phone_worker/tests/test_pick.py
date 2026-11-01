"""GTS 박스에서 봇 포켓몬 칸 고르기 판단 시험 (에뮬레이터 없이).  python -m pytest phone_worker/tests"""
import os, sys, types

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
try:
    import cv2, numpy  # noqa: F401
except ImportError:                                   # cv2·numpy가 없는 곳에서도 순수 함수만 시험
    sys.modules["cv2"] = types.ModuleType("cv2")
    v = types.ModuleType("vision"); v.TPL_DIR = ""
    sys.modules["vision"] = v

from flows import FlowError, _norm, can_skip_read, choose_slot, pick_mode  # noqa: E402

FP = {"lv": 5, "stats": [20, 11]}


def rows(*names):
    """화면 칸 순서대로 읽은 이름 → choose_slot 이 받는 norm 목록 (칸, 정규화 이름, 읽은 글, bx, by)"""
    return [(i, _norm(t), t, 0, 0) for i, t in enumerate(names)]


def wants(nick):
    return [_norm(w) for w in nick.split("|") if _norm(w)]


# (설명, nick, 읽은 이름들, 화면 칸 수, total, nth, fp) → (kind, 고른 칸 또는 요약 후보 칸들)
CASES = [
    ("이름 정확·혼자·지문", "Pikachu", ["Pikachu"], 1, 1, 0, FP, ("slot", 0)),
    ("남은 같은 종 개체: 종 이름이 둘 다 같음", "피카츄|Pikachu", ["Pikachu", "Pikachu"], 2, 1, 0, FP, ("summary", [0, 1])),
    ("남은 개체 이름을 못 읽음: 하나만 정확해도 요약", "피카츄|Pikachu", ["Pikachu", "@#"], 2, 1, 0, FP, ("summary", [0, 1])),
    ("다른 칸 메뉴가 안 뜸(안 읽힘): 요약", "Pikachu", ["Pikachu"], 2, 1, 0, FP, ("summary", [0])),
    ("다른 칸은 분명히 다른 닉네임: 바로 고름", "Pikachu", ["Pikachu", "Bolt"], 2, 1, 0, FP, ("slot", 0)),
    ("한 칸·이름 못 읽음·지문 있음: 요약", "Pikachu", ["?"], 1, 1, 0, FP, ("summary", [0])),
    ("한 칸·이름 틀림·지문 있음: 요약 (예전엔 0점이어도 보냄)", "Pikachu", ["Bolt"], 1, 1, 0, FP, ("summary", [0])),
    ("한 칸·비슷한 이름·지문 없음", "갑주무사", ["감주무사"], 1, 1, 0, None, ("slot", 0)),
    ("한 칸·이름 못 읽음·지문 없음: 실패 (0점 보내기 없앰)", "Pikachu", ["?"], 1, 1, 0, None, ("fail", None)),
    ("같은 이름 두 신청 nth=1, 지문 없음", "Pikachu", ["Pikachu", "Pikachu"], 2, 2, 1, None, ("slot", 1)),
    ("같은 이름 두 신청 nth=0, 지문 없음", "Pikachu", ["Pikachu", "Pikachu"], 2, 2, 0, None, ("slot", 0)),
    ("같은 이름 두 신청인데 화면엔 셋 (남은 개체): 실패", "Pikachu", ["Pikachu", "Pikachu", "Pikachu"], 3, 2, 0, None, ("fail", None)),
    ("같은 이름 두 신청 + 지문: 요약", "Pikachu", ["Pikachu", "Pikachu"], 2, 2, 1, FP, ("summary", [0, 1])),
    ("같은 이름 둘·지문 없음·봇 개체 하나: 실패", "Pikachu", ["Pikachu", "Pikachu"], 2, 1, 0, None, ("fail", None)),
    ("메뉴 뜬 칸 없음·지문 있음: 실패", "Pikachu", [], 2, 1, 0, FP, ("fail", None)),
]


@pytest.mark.parametrize("desc,nick,names,n_slots,total,nth,fp,want", CASES, ids=[c[0] for c in CASES])
def test_choose_slot(desc, nick, names, n_slots, total, nth, fp, want):
    kind, chosen, _ = choose_slot(wants(nick), rows(*names), n_slots, total, nth, fp)
    assert kind == want[0]
    if kind == "slot":
        assert chosen[0] == want[1]
    elif kind == "summary":
        assert sorted(e[0] for e in chosen) == want[1]


@pytest.mark.parametrize("n_slots,total,fp,want", [
    (1, 1, None, True),      # 지문 없고 한 마리: 이름 안 읽고 보냄 (예전 동작)
    (1, 1, FP, False),       # 지문 있으면 반드시 확인
    (2, 1, None, False),
    (1, 2, None, False),
])
def test_can_skip_read(n_slots, total, fp, want):
    assert can_skip_read(n_slots, total, fp) is want


@pytest.mark.parametrize("ocr,nick,slot,total,fp,want", [
    (True, "Pikachu", 0, 1, FP, "name"),
    (False, "Pikachu", 0, 1, None, FlowError),   # OCR 준비 안 됨 + 이름 있음 → 멈춤
    (False, "", 0, 1, FP, FlowError),            # 지문 있음 → 멈춤
    (False, "", 0, 2, None, FlowError),          # 같은 이름 여럿 → 멈춤
    (False, "", 0, 1, None, "blind"),            # 확인할 정보가 전혀 없을 때만 첫 칸
    (False, "", 1, 1, None, FlowError),          # 2번째 이후 칸은 이름 없이 못 고름
    (True, "", 0, 1, None, "blind"),
])
def test_pick_mode(ocr, nick, slot, total, fp, want):
    if want is FlowError:
        with pytest.raises(FlowError):
            pick_mode(ocr, nick, slot, total, fp)
    else:
        assert pick_mode(ocr, nick, slot, total, fp) == want
