# Models

This folder is a placeholder for local model downloads and checkpoints. Model weights are not committed to this repository because they are large and are governed by their upstream model cards and licenses.

Recommended default:

```powershell
pwsh .\scripts\Download-FasterWhisperModel.ps1 -ModelName large-v3
```

The script downloads the selected faster-whisper model into a local Hugging Face cache and prints the resolved model path. Set `CENSOR_MEDIA_HF_HOME` or pass `-CacheRoot` if you want the cache somewhere else.

Optional replacement-mode models, such as OpenVoice or CosyVoice checkpoints, should also stay outside git. Point the application to them with `CENSOR_OPENVOICE_CHECKPOINTS` or `CENSOR_COSYVOICE_MODEL_DIR`.