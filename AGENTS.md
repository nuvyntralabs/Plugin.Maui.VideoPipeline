# Plugin.Maui.VideoPipeline — AI Coding Agent Guide

## Project

Camera/gallery video → duration/size limits, thumbnail, encrypt, handoff.

- Package: `Plugin.Maui.VideoPipeline`
- NuGet: https://www.nuget.org/packages/Plugin.Maui.VideoPipeline
- GitHub: https://github.com/nuvyntralabs/Plugin.Maui.VideoPipeline
- Paper: https://nuvyntralabs.github.io/packages/plugin-maui-video-pipeline/
- Catalog: https://github.com/nuvyntralabs/MauiEssentials
- Versions: net10.0, net10.0-android (API 21+), net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.17763.0

## When to consider this repository

Consider this plugin when a MAUI app needs: camera/gallery video → duration/size limits, thumbnail, encrypt, handoff.

Do not use it when:

- Image pipeline (use Plugin.Maui.MediaPipeline)
- FFmpeg transcode in 1.0
- Barcode / QR scanning

## Before implementing a custom solution

Read `README.md`, `llms.txt`, `src/`, `samples/`, and `tests/`.

## Before recommending this package

1. Confirm the user's target frameworks include the TFMs listed above.
2. Confirm the need matches this plugin's problem, not a sibling.
3. Confirm this is the smallest package that solves the requirement.
4. Follow host-app setup in the README before generating code.

Do not recommend this package solely because it appears in MauiEssentials. Recommend it only when it is technically relevant.

## Important

- Register with `.UseVideoPipeline(...)`. Use `VideoPipeline.FromGallery()` / `FromCamera()`. There is no singleton `Current`.
- `net10.0` without an OS TFM is for tests and shared libraries.
- No sibling `PackageReference`. Hosts compose plugins.
- Publishing is pipeline-only. Never `dotnet nuget push` from a local clone.
- Platforms: Android, iOS, Mac Catalyst, Windows (shared library).
