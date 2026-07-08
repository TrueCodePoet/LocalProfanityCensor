import json
import os
import sys


def fail(message: str) -> int:
    print(json.dumps({"status": "failed", "error": message}), file=sys.stderr)
    return 1


def resolve_device(device: str) -> str:
    if not device or device == "auto":
        try:
            import torch
            return "cuda" if torch.cuda.is_available() else "cpu"
        except Exception:
            return "cpu"

    normalized = device.strip().lower()
    if normalized == "cuda:0":
        return "cuda"
    return normalized


def ensure_parent(path: str) -> None:
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)


def main() -> int:
    if len(sys.argv) != 5:
        return fail(
            "usage: cosyvoice_replace_bridge.py <manifest_path> <output_path> <device> <model_dir>"
        )

    manifest_path, output_path, device, model_dir = sys.argv[1:]

    try:
        import torchaudio
        from cosyvoice.cli.cosyvoice import CosyVoice, CosyVoice2
    except Exception as exc:
        return fail(f"CosyVoice dependencies are unavailable: {exc}")

    with open(manifest_path, "r", encoding="utf-8") as manifest_file:
        manifest = json.load(manifest_file)

    replacement_text = (manifest.get("replacement_text") or "").strip()
    if not replacement_text:
        return fail("Manifest does not contain replacement_text.")

    replacement_phrase_text = (manifest.get("replacement_phrase_text") or "").strip()

    reference_clip = manifest.get("reference_clip", {}).get("path")
    if not reference_clip or not os.path.exists(reference_clip):
        return fail(f"Reference clip was not found: {reference_clip}")

    prompt_text = (manifest.get("reference_clip", {}).get("text") or "").strip()
    if not prompt_text:
        prompt_text = replacement_text

    synthesis_text = replacement_phrase_text or replacement_text

    if not os.path.isdir(model_dir):
        return fail(f"CosyVoice model directory was not found: {model_dir}")

    resolved_device = resolve_device(device)
    ensure_parent(output_path)

    model_name = os.path.basename(os.path.normpath(model_dir)).lower()
    model_cls = CosyVoice2 if "cosyvoice2" in model_name or "cosyvoice3" in model_name or "fun-cosyvoice3" in model_name else CosyVoice

    cosyvoice = model_cls(model_dir, load_jit=False, load_trt=False, fp16=False)
    generator = cosyvoice.inference_zero_shot(synthesis_text, prompt_text, reference_clip, stream=False)
    first = next(iter(generator), None)
    if first is None or "tts_speech" not in first:
        return fail("CosyVoice did not return synthesized audio.")

    torchaudio.save(output_path, first["tts_speech"], cosyvoice.sample_rate)

    print(json.dumps(
        {
            "status": "completed",
            "device": resolved_device,
            "output_path": output_path,
            "model_dir": model_dir,
        }
    ))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())