import json
import os
import sys
from pathlib import Path


def main() -> None:
    root = Path(sys.argv[1]).resolve()
    models = root / "TranslationModels"
    ocr_dir = models / "manga-ocr-onnx"
    data_dir = models / "data"
    models.mkdir(parents=True, exist_ok=True)
    os.environ["HF_HOME"] = str(models / "huggingface")
    os.environ["XDG_DATA_HOME"] = str(data_dir)
    os.environ["XDG_CONFIG_HOME"] = str(models / "config")
    os.environ["XDG_CACHE_HOME"] = str(models / "cache")
    os.environ["ARGOS_DEVICE_TYPE"] = "cpu"
    os.environ["ARGOS_CHUNK_TYPE"] = "MINISBD"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"

    from huggingface_hub import snapshot_download
    snapshot_download(
        repo_id="mayocream/manga-ocr-onnx",
        local_dir=str(ocr_dir),
        allow_patterns=["*.json", "*.txt", "*.onnx", "vocab.txt", "tokenizer*", "special_tokens_map.json", "preprocessor_config.json"],
    )

    import argostranslate.package
    argostranslate.package.update_package_index()
    packages = argostranslate.package.get_available_packages()
    package = next((p for p in packages if p.from_code == "ja" and p.to_code == "en"), None)
    if package is None:
        raise RuntimeError("The Argos package index does not contain a Japanese-to-English model.")
    installed = argostranslate.package.get_installed_packages()
    if not any(p.from_code == "ja" and p.to_code == "en" for p in installed):
        argostranslate.package.install_from_path(package.download())

    ready = {
        "ocr": "mayocream/manga-ocr-onnx",
        "translation": f"Argos {package.package_version} ja-en",
        "runtime": sys.version.split()[0],
        "offlineReady": True,
    }
    (models / "ready.json").write_text(json.dumps(ready, indent=2), encoding="utf-8")
    print(json.dumps(ready), flush=True)


if __name__ == "__main__":
    main()
