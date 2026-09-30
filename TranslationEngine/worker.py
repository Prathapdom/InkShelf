import json
import os
import re
import sys
import traceback
from pathlib import Path


sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")

root = Path(sys.argv[1]).resolve()
models = root / "TranslationModels"
os.environ["HF_HOME"] = str(models / "huggingface")
os.environ["XDG_DATA_HOME"] = str(models / "data")
os.environ["XDG_CONFIG_HOME"] = str(models / "config")
os.environ["XDG_CACHE_HOME"] = str(models / "cache")
os.environ["ARGOS_DEVICE_TYPE"] = "cpu"
os.environ["ARGOS_CHUNK_TYPE"] = "MINISBD"
os.environ["ARGOS_STANZA_AVAILABLE"] = "false"
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_DATASETS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"

ocr = None


def get_ocr():
    global ocr
    if ocr is None:
        from manga_ocr import MangaOcr
        ocr = MangaOcr(pretrained_model_name_or_path=str(models / "manga-ocr-onnx"), force_cpu=True)
    return ocr


def has_japanese(text: str) -> bool:
    return bool(re.search(r"[\u3040-\u30ff\u3400-\u9fff]", text))


def translate(image_path: str) -> dict:
    from PIL import Image
    import argostranslate.translate

    with Image.open(image_path) as image:
        japanese = get_ocr()(image)
    if not japanese or not has_japanese(japanese):
        return {"japaneseText": japanese, "englishText": "No Japanese text was confidently detected on this page."}
    english = argostranslate.translate.translate(japanese, "ja", "en")
    return {"japaneseText": japanese, "englishText": english}


for line in sys.stdin:
    request_id = None
    try:
        request = json.loads(line.lstrip("\ufeff"))
        request_id = request.get("id")
        response = {"id": request_id, **translate(request["imagePath"])}
    except Exception as exc:
        response = {"id": request_id, "error": str(exc), "detail": traceback.format_exc(limit=3)}
    print(json.dumps(response, ensure_ascii=False), flush=True)
