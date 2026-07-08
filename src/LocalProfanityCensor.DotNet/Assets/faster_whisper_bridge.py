import os
import json
import importlib.util
import sys
import time


os.environ.setdefault("HF_HUB_DISABLE_IMPLICIT_TOKEN", "1")


def log_stage(message: str) -> None:
    print(f"[bridge] {message}", file=sys.stderr, flush=True)

def main() -> int:
    if len(sys.argv) not in {8, 9}:
        print(
            json.dumps({"error": "usage: faster_whisper_bridge.py <engine> <audio_path> <model> <device> <compute_type> <language> <word_timestamps> [vad_filter]"}),
            file=sys.stderr,
        )
        return 1

    engine, audio_path, model_name, device, compute_type, language, word_timestamps = sys.argv[1:8]
    effective_language = None if language in {"", "auto", "none"} else language
    emit_word_timestamps = word_timestamps.lower() == "true"
    use_vad_filter = True if len(sys.argv) < 9 else sys.argv[8].lower() == "true"

    if engine == "faster-whisper":
        payload = run_faster_whisper(
            audio_path=audio_path,
            model_name=model_name,
            device=device,
            compute_type=compute_type,
            effective_language=effective_language,
            emit_word_timestamps=emit_word_timestamps,
            use_vad_filter=use_vad_filter,
        )
    elif engine == "faster-whisper-batch":
        payload = run_faster_whisper_batch(
            manifest_path=audio_path,
            model_name=model_name,
            device=device,
            compute_type=compute_type,
            effective_language=effective_language,
            emit_word_timestamps=emit_word_timestamps,
            use_vad_filter=use_vad_filter,
        )
    elif engine == "whisperx":
        payload = run_whisperx(
            audio_path=audio_path,
            model_name=model_name,
            device=device,
            compute_type=compute_type,
            effective_language=effective_language,
        )
    else:
        print(json.dumps({"error": f"unsupported engine: {engine}"}), file=sys.stderr)
        return 1

    print(json.dumps(payload))
    return 0


def run_faster_whisper(
    audio_path: str,
    model_name: str,
    device: str,
    compute_type: str,
    effective_language: str | None,
    emit_word_timestamps: bool,
    use_vad_filter: bool,
) -> dict:
    model, resolved_device = load_faster_whisper_model(model_name, device, compute_type)

    return transcribe_faster_whisper_audio(
        model=model,
        audio_path=audio_path,
        resolved_device=resolved_device,
        effective_language=effective_language,
        emit_word_timestamps=emit_word_timestamps,
        use_vad_filter=use_vad_filter,
    )


def run_faster_whisper_batch(
    manifest_path: str,
    model_name: str,
    device: str,
    compute_type: str,
    effective_language: str | None,
    emit_word_timestamps: bool,
    use_vad_filter: bool,
) -> dict:
    with open(manifest_path, "r", encoding="utf-8") as manifest_file:
        manifest = json.load(manifest_file)

    items = manifest.get("items", []) or []
    model, resolved_device = load_faster_whisper_model(model_name, device, compute_type)
    results = []

    for index, item in enumerate(items, start=1):
        key = item.get("key") or str(index)
        audio_path = item.get("audio_path")
        if not audio_path:
            raise RuntimeError(f"Batch manifest item '{key}' is missing audio_path.")

        log_stage(f"faster-whisper batch item start key={key} index={index}/{len(items)} audio={audio_path}")
        item_payload = transcribe_faster_whisper_audio(
            model=model,
            audio_path=audio_path,
            resolved_device=resolved_device,
            effective_language=effective_language,
            emit_word_timestamps=emit_word_timestamps,
            use_vad_filter=use_vad_filter,
        )
        results.append(
            {
                "key": key,
                "segments": item_payload["segments"],
            }
        )
        log_stage(f"faster-whisper batch item done key={key} segments={len(item_payload['segments'])}")

    return {
        "device": resolved_device,
        "results": results,
    }


def load_faster_whisper_model(model_name: str, device: str, compute_type: str):
    from faster_whisper import WhisperModel

    resolved_device, resolved_compute_type = resolve_faster_whisper_runtime(device, compute_type)
    load_started_at = time.perf_counter()
    log_stage(f"faster-whisper load_model start model={model_name} device={resolved_device} compute_type={resolved_compute_type}")

    model = WhisperModel(model_name, device=resolved_device, compute_type=resolved_compute_type)
    load_elapsed = time.perf_counter() - load_started_at
    log_stage(f"faster-whisper load_model done elapsed={load_elapsed:.3f}s")
    return model, resolved_device


def transcribe_faster_whisper_audio(
    model,
    audio_path: str,
    resolved_device: str,
    effective_language: str | None,
    emit_word_timestamps: bool,
    use_vad_filter: bool,
) -> dict:

    transcribe_started_at = time.perf_counter()
    log_stage(f"faster-whisper transcribe start audio={audio_path}")
    segments, info = model.transcribe(
        audio_path,
        language=effective_language,
        word_timestamps=emit_word_timestamps,
        vad_filter=use_vad_filter,
        condition_on_previous_text=False,
    )
    log_stage(f"faster-whisper transcribe iterator ready elapsed={time.perf_counter() - transcribe_started_at:.3f}s")

    payload = {
        "device": resolved_device,
        "language": getattr(info, "language", None),
        "segments": [],
    }
    collect_started_at = time.perf_counter()
    for segment in segments:
        words = []
        for word in getattr(segment, "words", []) or []:
            words.append(
                {
                    "text": getattr(word, "word", ""),
                    "start": float(getattr(word, "start", 0.0) or 0.0),
                    "end": float(getattr(word, "end", getattr(word, "start", 0.0)) or 0.0),
                    "confidence": getattr(word, "probability", None),
                }
            )

        payload["segments"].append(
            {
                "text": getattr(segment, "text", "") or "",
                "start": float(getattr(segment, "start", 0.0) or 0.0),
                "end": float(getattr(segment, "end", getattr(segment, "start", 0.0)) or 0.0),
                "words": words,
            }
        )

    log_stage(
        "faster-whisper collect_segments done "
        f"segments={len(payload['segments'])} elapsed={time.perf_counter() - collect_started_at:.3f}s total={time.perf_counter() - transcribe_started_at:.3f}s"
    )
    return payload


def run_whisperx(
    audio_path: str,
    model_name: str,
    device: str,
    compute_type: str,
    effective_language: str | None,
) -> dict:
    import whisperx

    resolved_device_name, runtime_device, effective_compute_type = resolve_whisperx_runtime(device, compute_type)
    load_started_at = time.perf_counter()
    log_stage(
        "whisperx load_model start "
        f"model={model_name} device={resolved_device_name} runtime_device={runtime_device} compute_type={effective_compute_type}"
    )
    model = whisperx.load_model(model_name, runtime_device, compute_type=effective_compute_type)
    log_stage(f"whisperx load_model done elapsed={time.perf_counter() - load_started_at:.3f}s")

    align_language = effective_language or "en"
    align_started_at = time.perf_counter()
    log_stage(f"whisperx load_align_model start language={align_language}")
    align_model, metadata = whisperx.load_align_model(language_code=align_language, device=runtime_device)
    log_stage(f"whisperx load_align_model done elapsed={time.perf_counter() - align_started_at:.3f}s")

    audio_load_started_at = time.perf_counter()
    log_stage(f"whisperx load_audio start audio={audio_path}")
    audio = whisperx.load_audio(audio_path)
    log_stage(f"whisperx load_audio done elapsed={time.perf_counter() - audio_load_started_at:.3f}s")

    transcribe_started_at = time.perf_counter()
    log_stage("whisperx transcribe start")
    result = model.transcribe(audio, batch_size=16, language=effective_language)
    log_stage(f"whisperx transcribe done elapsed={time.perf_counter() - transcribe_started_at:.3f}s")

    alignment_started_at = time.perf_counter()
    log_stage("whisperx align start")
    aligned = whisperx.align(result["segments"], align_model, metadata, audio, runtime_device, return_char_alignments=False)
    log_stage(f"whisperx align done elapsed={time.perf_counter() - alignment_started_at:.3f}s total={time.perf_counter() - load_started_at:.3f}s")

    payload = {
        "device": resolved_device_name,
        "language": result.get("language", effective_language),
        "segments": [],
    }

    for segment in aligned.get("segments", []):
        words = []
        for word in segment.get("words", []) or []:
            words.append(
                {
                    "text": word.get("word", "") or "",
                    "start": float(word.get("start", segment.get("start", 0.0)) or 0.0),
                    "end": float(word.get("end", word.get("start", segment.get("start", 0.0))) or 0.0),
                    "confidence": word.get("score"),
                }
            )

        payload["segments"].append(
            {
                "text": segment.get("text", "") or "",
                "start": float(segment.get("start", 0.0) or 0.0),
                "end": float(segment.get("end", segment.get("start", 0.0)) or 0.0),
                "words": words,
            }
        )

    log_stage(f"whisperx collect_segments done segments={len(payload['segments'])}")
    return payload


def resolve_faster_whisper_runtime(requested_device: str, compute_type: str) -> tuple[str, str]:
    normalized_device = normalize_device_name(requested_device)
    effective_compute_type = "default" if compute_type == "auto" else compute_type

    if normalized_device == "auto":
        return ("cuda", effective_compute_type) if has_ctranslate2_cuda() else ("cpu", effective_compute_type)

    if normalized_device in {"cuda", "cpu"}:
        return normalized_device, effective_compute_type

    if normalized_device in {"directml", "dml", "rocm"}:
        raise RuntimeError(
            f"faster-whisper does not support the requested device '{requested_device}' in this bridge. "
            "Use CPU for faster-whisper and WhisperX for AMD-capable fallback backends."
        )

    return normalized_device, effective_compute_type


def resolve_whisperx_runtime(requested_device: str, compute_type: str):
    normalized_device = normalize_device_name(requested_device)

    if normalized_device == "auto":
        if has_torch_cuda():
            backend_name = "rocm" if is_torch_rocm() else "cuda"
            return backend_name, "cuda", resolve_whisperx_compute_type(backend_name, compute_type)
        if has_torch_directml():
            import torch_directml

            return "directml", torch_directml.device(), resolve_whisperx_compute_type("directml", compute_type)
        return "cpu", "cpu", resolve_whisperx_compute_type("cpu", compute_type)

    if normalized_device == "cuda":
        if not has_torch_cuda():
            raise RuntimeError("WhisperX requested CUDA, but torch CUDA support is not available.")
        backend_name = "rocm" if is_torch_rocm() else "cuda"
        return backend_name, "cuda", resolve_whisperx_compute_type(backend_name, compute_type)

    if normalized_device == "rocm":
        if not is_torch_rocm() or not has_torch_cuda():
            raise RuntimeError("WhisperX requested ROCm, but the active torch build is not ROCm-enabled.")
        return "rocm", "cuda", resolve_whisperx_compute_type("rocm", compute_type)

    if normalized_device in {"directml", "dml"}:
        if not has_torch_directml():
            raise RuntimeError("WhisperX requested DirectML, but torch_directml is not installed.")
        import torch_directml

        return "directml", torch_directml.device(), resolve_whisperx_compute_type("directml", compute_type)

    if normalized_device == "cpu":
        return "cpu", "cpu", resolve_whisperx_compute_type("cpu", compute_type)

    return normalized_device, normalized_device, resolve_whisperx_compute_type(normalized_device, compute_type)


def resolve_whisperx_compute_type(resolved_device: str, compute_type: str) -> str:
    if compute_type != "auto":
        return compute_type

    if resolved_device in {"cuda", "rocm"}:
        return "float16"

    return "float32"


def normalize_device_name(device: str) -> str:
    return (device or "auto").strip().lower()


def has_ctranslate2_cuda() -> bool:
    try:
        import ctranslate2

        return bool(ctranslate2.get_cuda_device_count() > 0)
    except Exception:
        return False


def has_torch_cuda() -> bool:
    try:
        import torch

        return bool(torch.cuda.is_available())
    except Exception:
        return False


def is_torch_rocm() -> bool:
    try:
        import torch

        return bool(getattr(torch.version, "hip", None))
    except Exception:
        return False


def has_torch_directml() -> bool:
    return importlib.util.find_spec("torch_directml") is not None


if __name__ == "__main__":
    raise SystemExit(main())