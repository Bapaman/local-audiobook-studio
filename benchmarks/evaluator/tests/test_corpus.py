from __future__ import annotations

import json
from pathlib import Path

from jsonschema import Draft202012Validator

from spike_eval.text import normalize_for_cer


REPO_ROOT = Path(__file__).resolve().parents[3]
CORPUS_PATH = REPO_ROOT / "benchmarks" / "corpus" / "ru-v1.json"
SCHEMA_PATH = REPO_ROOT / "benchmarks" / "corpus" / "ru-v1.schema.json"


def test_ru_v1_corpus_is_schema_valid_and_has_sixteen_unique_cases() -> None:
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    corpus = json.loads(CORPUS_PATH.read_text(encoding="utf-8"))

    errors = sorted(Draft202012Validator(schema).iter_errors(corpus), key=lambda error: list(error.path))

    assert errors == []
    assert len(corpus["items"]) == 16
    assert len({item["id"] for item in corpus["items"]}) == 16


def test_ru_v1_corpus_covers_required_russian_tts_cases_within_duration_cap() -> None:
    corpus = json.loads(CORPUS_PATH.read_text(encoding="utf-8"))
    items = corpus["items"]
    categories = {item["category"] for item in items}
    required_categories = {
        "neutral-short",
        "neutral-long",
        "direct-speech",
        "question",
        "exclamation",
        "date-time",
        "cardinal-numbers",
        "ordinal-numbers",
        "abbreviations-units",
        "names-geography",
        "yo-letter",
        "list",
        "embedded-quotes",
    }

    assert required_categories <= categories
    assert all(len(item["text"]) <= 350 for item in items)
    assert all(item["max_steps"] * 0.160 <= 20.0 for item in items)
    assert all(item["spoken_normalized"] == normalize_for_cer(item["spoken_normalized"]) for item in items)
    assert any(any(character.isdigit() for character in item["text"]) for item in items)
    assert any("CPU" in item["text"] or "USB" in item["text"] for item in items)
    assert any("ё" in item["text"].lower() for item in items)
    assert any("«" in item["text"] and "„" in item["text"] for item in items)

