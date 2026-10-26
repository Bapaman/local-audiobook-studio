from __future__ import annotations

import pytest

from spike_eval.text import comparison_text, normalize_for_cer


def test_normalize_for_cer_applies_nfc_casefold_and_collapses_whitespace() -> None:
    decomposed_yo = "Е\u0308Ж"

    assert normalize_for_cer(f"  {decomposed_yo},\tЁЛКА!\n") == "ёж ёлка"


def test_normalize_for_cer_keeps_yo_distinct_from_e() -> None:
    assert normalize_for_cer("всё ещё") == "всё ещё"
    assert normalize_for_cer("все еще") == "все еще"
    assert normalize_for_cer("всё ещё") != normalize_for_cer("все еще")


@pytest.mark.parametrize(
    ("source", "expected"),
    [
        ("«Привет!» — сказала Анна...", "привет сказала анна"),
        ("21.10.2026, 07:45", "21 10 2026 07 45"),
        ("CPU / GPU; USB-C", "cpu gpu usb c"),
    ],
)
def test_normalize_for_cer_removes_punctuation_without_joining_tokens(
    source: str,
    expected: str,
) -> None:
    assert normalize_for_cer(source) == expected


def test_comparison_text_uses_spoken_normalized_not_display_text() -> None:
    item = {
        "text": "21.10.2026 в 07:45, CPU — 80%.",
        "spoken_normalized": (
            "двадцать первого октября две тысячи двадцать шестого года в семь часов "
            "сорок пять минут си пи ю восемьдесят процентов"
        ),
    }

    assert comparison_text(item) == item["spoken_normalized"]
    assert comparison_text(item) != normalize_for_cer(item["text"])


@pytest.mark.parametrize("value", [{}, {"spoken_normalized": ""}, {"spoken_normalized": 42}])
def test_comparison_text_rejects_missing_or_invalid_spoken_normalized(value: object) -> None:
    with pytest.raises(ValueError, match="INVALID_SPOKEN_NORMALIZED"):
        comparison_text(value)

