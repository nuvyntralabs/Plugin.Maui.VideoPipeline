# Changelog

## 1.1.0

- Probe duration / size / resolution and generate a JPEG thumbnail when the OS can decode a frame.
- Over-budget clips try an OS transcode (Android `MediaCodec`, iOS/Catalyst `AVAssetExportSession`). No FFmpeg.
- Android transcode honors cancel and times out instead of spinning if the encoder never signals EOS.
- If the device cannot encode, the result is `CannotTranscode` — never a crash.
- `UseVideoPipeline` default max duration applies when the builder does not set `MaxDuration`.

## 1.0.3

- Catalog copy matches 1.0: reject-if-over-budget + encrypt. No transcode or thumbnail.

## 1.0.2

- Pack `nuget.png` as the NuGet gallery icon.

## 1.0.1

- README lists Android and iOS host permissions. Sample iOS `Info.plist` includes camera, microphone, and photo-library usage strings.

## 1.0.0

- Camera/gallery video → duration/size limits, thumbnail, encrypt, handoff
- `UseVideoPipeline` registration
- Sources, Limits, Encrypt / upload
- Sample app and `net10.0` unit tests
