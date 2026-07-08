# Security Policy

Please report security issues privately rather than opening a public issue with exploit details.

## Sensitive Data

Do not include media files, transcripts, generated reports, API keys, tokens, private paths, server names, or model cache contents in issues or pull requests.

## Runtime Safety

LocalProfanityCensor shells out to `ffmpeg`, `ffprobe`, Python, and optional AI runtimes. Only run trusted builds and review scripts before processing personal media libraries.