# Experimental Russian OCR support with OneOCR

> **Status: experimental, branch-specific work.** This is an independent contribution in the `feature/oneocr-russian` branch of this fork. It is not part of an official PowerToys release and has not been merged into the upstream Microsoft/PowerToys repository.

## What this change does

This work adds an alternative OCR path for Russian-language recognition in the PowerToys PowerOCR module:

- When the selected OCR language is Russian (`ru` or a tag beginning with `ru-`), the hybrid recognizer tries OneOCR.
- For other languages, it uses the existing Windows OCR recognizer.
- If OneOCR initialization or recognition fails, the hybrid recognizer falls back to Windows OCR.
- A cancellation requested by the caller is propagated rather than treated as an OCR failure.

The overlay language-selection logic keeps a saved preferred language as the first choice. If that preference is not available, it prefers an installed Russian OCR language, then tries the current input-language layout, and finally uses the first available language.

## Files changed

- `PowerOCR/PowerOCRXAML/App.xaml.cs` registers `HybridOcrRecognizer` as the `IOcrRecognizer` implementation.
- `PowerOCR/Services/OverlayManager.cs` adds the Russian-language selection preference.
- `PowerOCR.Core/Ocr/HybridOcrRecognizer.cs` chooses between OneOCR and Windows OCR and implements the fallback.
- `PowerOCR.Core/Ocr/OneOcrRecognizer.cs` calls the native OneOCR exports and converts the recognition result into PowerOCR's OCR document model.

## Runtime requirements and setup status

The current implementation expects a 64-bit (x64) process and looks for these files in:

`%LOCALAPPDATA%\Microsoft\PowerToys\PowerOCR\OneOCR\`

- `oneocr.dll`
- `oneocr.onemodel`

The native OneOCR library may also require additional native dependencies in its DLL search path (for example, an appropriate ONNX Runtime DLL, depending on the OneOCR package). Those dependencies must match the architecture and the native library build.

**These runtime files are not included in this repository branch.** This change does not provide an installer, downloader, or automatic deployment mechanism for them. A user building the source must obtain and install the required runtime components separately, subject to their applicable license and redistribution terms. Their redistribution rights have not been established by this project.

If OneOCR cannot be loaded or initialized, the implementation is designed to fall back to Windows OCR. That fallback does not mean that the OneOCR runtime has been installed or that Russian recognition quality will be equivalent.

## Building

This is a source-code change to the existing PowerToys repository, not a standalone application. Build it using the normal PowerToys development environment and instructions for the checked-out PowerToys version. The PowerOCR project and its dependencies must be built as part of the repository.

No ready-to-install PowerToys package is provided by this branch.

## Verification and known limitations

In the development environment, the PowerOCR Release build completed successfully. Manual checks reported that:

- Russian was selected automatically even when the keyboard input language was English.
- OneOCR initialized and completed a recognition pipeline for Russian.
- Selecting English manually used Windows OCR.

These checks are not a substitute for a complete test matrix. Mixed Russian/English text, SQL and code snippets, different image types, OneOCR runtime versions, and failure cases still require broader, reproducible testing.

The native OneOCR interface is loaded dynamically from exported functions. Compatibility with other OneOCR versions has not been established.

## Project status

This branch is intended to document and develop an experimental integration for review. Before proposing it upstream or distributing a build, the implementation needs further testing, review of the native runtime's licensing and redistribution terms, and a reliable installation/deployment approach.

The changes are maintained in this fork's `feature/oneocr-russian` branch. They should not be described as an official PowerToys feature unless and until they are accepted upstream.
