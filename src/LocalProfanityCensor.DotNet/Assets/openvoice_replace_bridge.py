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
            return "cuda:0" if torch.cuda.is_available() else "cpu"
        except Exception:
            return "cpu"

    normalized = device.strip().lower()
    if normalized == "cuda":
        return "cuda:0"
    return device


def ensure_parent(path: str) -> None:
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)


def main() -> int:
    if len(sys.argv) != 7:
        return fail(
            "usage: openvoice_replace_bridge.py <manifest_path> <output_path> <device> <checkpoints_dir> <speaker_id> <language>"
        )

    manifest_path, output_path, device, checkpoints_dir, speaker_id, language = sys.argv[1:]

    try:
        from melo.api import TTS
        from openvoice.api import ToneColorConverter
        import torch
    except Exception as exc:
        return fail(f"OpenVoice dependencies are unavailable: {exc}")

    with open(manifest_path, "r", encoding="utf-8") as manifest_file:
        manifest = json.load(manifest_file)

    replacement_text = (manifest.get("replacement_text") or "").strip()
    if not replacement_text:
        return fail("Manifest does not contain replacement_text.")

    reference_clip = manifest.get("reference_clip", {}).get("path")
    if not reference_clip or not os.path.exists(reference_clip):
        return fail(f"Reference clip was not found: {reference_clip}")

    resolved_device = resolve_device(device)
    converter_config = os.path.join(checkpoints_dir, "converter", "config.json")
    converter_ckpt = os.path.join(checkpoints_dir, "converter", "checkpoint.pth")
    base_speaker_embedding = os.path.join(checkpoints_dir, "base_speakers", "ses", f"{speaker_id}.pth")

    missing = [
        path for path in [converter_config, converter_ckpt, base_speaker_embedding]
        if not os.path.exists(path)
    ]
    if missing:
        return fail("Missing OpenVoice checkpoint files: " + ", ".join(missing))

    ensure_parent(output_path)
    working_dir = os.path.dirname(os.path.abspath(output_path)) or os.getcwd()
    base_tts_path = os.path.join(working_dir, "openvoice.base.wav")

    tone_color_converter = ToneColorConverter(converter_config, device=resolved_device)
    tone_color_converter.load_ckpt(converter_ckpt)

    target_se = tone_color_converter.extract_se([reference_clip])
    source_se = torch.load(base_speaker_embedding, map_location=resolved_device)

    tts_model = TTS(language=language, device=resolved_device)
    speaker_ids = getattr(tts_model.hps.data, "spk2id", {})
    if speaker_id not in speaker_ids:
        fallback_key = next((key for key in speaker_ids.keys() if key.lower().startswith(language.lower()[0:2].lower())), None)
        if fallback_key is None and speaker_ids:
            fallback_key = next(iter(speaker_ids.keys()))
        if fallback_key is None:
            return fail("No MeloTTS speaker ids are available.")
        speaker_id = fallback_key

    tts_model.tts_to_file(
        replacement_text,
        speaker_id=speaker_ids[speaker_id],
        output_path=base_tts_path,
        speed=1.0,
    )

    tone_color_converter.convert(
        audio_src_path=base_tts_path,
        src_se=source_se,
        tgt_se=target_se,
        output_path=output_path,
        tau=0.3,
        message="@LocalProfanityCensor",
    )

    print(json.dumps(
        {
            "status": "completed",
            "device": resolved_device,
            "speaker_id": speaker_id,
            "base_tts_path": base_tts_path,
            "output_path": output_path,
        }
    ))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())