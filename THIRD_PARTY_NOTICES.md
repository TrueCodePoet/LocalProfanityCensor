# Third-Party Notices

LocalProfanityCensor is MIT licensed. It also depends on external tools, libraries, and optional AI models. This repository does not vendor third-party source trees or model weights unless explicitly noted.

Review upstream licenses before redistributing binaries, installers, model weights, or generated bundles.

## Runtime Tools

| Project | Purpose | Upstream |
| --- | --- | --- |
| FFmpeg / ffprobe | Media probing, extraction, rendering, remuxing | https://ffmpeg.org/ |
| .NET | Application runtime and SDK | https://dotnet.microsoft.com/ |
| Python | AI bridge runtime | https://www.python.org/ |

FFmpeg builds can be LGPL or GPL depending on compile options. If you redistribute FFmpeg with a release, include the exact upstream license information for the build you ship.

## .NET Packages

| Package | Purpose | Upstream |
| --- | --- | --- |
| YamlDotNet | YAML config and dictionary parsing | https://github.com/aaubry/YamlDotNet |
| WiX Toolset | Optional MSI packaging | https://wixtoolset.org/ |

## Python And AI Packages

| Package | Purpose | Upstream |
| --- | --- | --- |
| faster-whisper | Whisper transcription bridge | https://github.com/SYSTRAN/faster-whisper |
| CTranslate2 | Inference backend used by faster-whisper | https://github.com/OpenNMT/CTranslate2 |
| Demucs | Optional dialogue/vocal isolation | https://github.com/facebookresearch/demucs |
| PyTorch | Demucs and optional fallback backend | https://pytorch.org/ |
| Hugging Face Hub | Model download/cache support | https://github.com/huggingface/huggingface_hub |
| OpenVoice | Optional prototype replacement voice conversion | https://github.com/myshell-ai/OpenVoice |
| MeloTTS | Optional OpenVoice dependency | https://github.com/myshell-ai/MeloTTS |
| CosyVoice | Optional prototype replacement synthesis path | https://github.com/FunAudioLLM/CosyVoice |

## Models

Model weights are downloaded by users into their own Hugging Face cache or local checkpoint folders. Common choices include faster-whisper model conversions such as `large-v3` and optional replacement-mode checkpoints. Model cards and upstream repositories define the allowed use, attribution, and redistribution terms for each model.