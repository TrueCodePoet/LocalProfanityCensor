# Bundles

This folder documents opinionated release bundles for LocalProfanityCensor. A bundle is a pre-defined packaging target that groups the files, scripts, and runtime expectations for one supported workflow.

The goal of bundles is to reduce operational complexity by giving users a small number of clearly named, clearly documented paths instead of asking them to assemble the runtime from the full repository layout.

## What A Bundle Is

A bundle is a documented target for a specific usage pattern. Each bundle should define:

- the intended platform
- the intended censor mode
- the expected runtime shape
- the files included in the staged payload
- the setup steps required before first use
- the script or command used to build the bundle
- the script or command used to run the bundle

Bundles are documentation-first in this repository. They describe the supported packaging targets and how each one is produced from the existing deployment scripts.

## Current Bundles

### `windows-mute-default`

The default recommended public bundle.

- Platform: Windows
- Primary mode: `mute`
- Audience: advanced home media users and technical operators
- Goal: provide one conservative, review-first workflow with the fewest moving parts

This bundle is intended to stage everything needed for the targeted run except large model weights and user media.

See `bundles/windows-mute-default/README.md` for the bundle definition.

## Bundle Design Rules

Each bundle should stay opinionated and narrow.

- Prefer one platform over many.
- Prefer one stable censor mode over many.
- Prefer one recommended config over a menu of configs.
- Prefer one setup path over multiple optional branches.
- Mark experimental features outside the bundle unless they are the explicit purpose of that bundle.

## How Bundles Are Built

Bundles should be built from the existing deployment path rather than from ad hoc manual copying.

For the current Windows mute bundle, the intended build flow is:

1. Publish the .NET application with `Deployment/Scripts/Publish-Core.ps1`.
2. Stage the payload with `Deployment/Scripts/Stage-Payload.ps1`.
3. Copy or assemble the staged output into the bundle target layout.
4. Document any external prerequisites that are intentionally not bundled, such as model weights.

## What Is Not Bundled

Unless a bundle explicitly says otherwise, assume these are not committed into the repository bundle target:

- user media files
- generated transcripts or reports
- Hugging Face model weights
- machine-specific Python environments
- secrets, tokens, or private paths

## Adding Future Bundles

If more bundles are added later, each should get its own subfolder under `bundles/` with a dedicated `README.md` that explains:

- what the bundle is for
- what it includes
- what it excludes
- how it is built
- how it is validated
- how it is intended to be run
