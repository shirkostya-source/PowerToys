/*
 * Copyright (c) Microsoft Corporation
 * The Microsoft Corporation licenses this file to you under the MIT license.
 * See the LICENSE file in the project root for more information.
 */

using System.Diagnostics;
using System.Drawing;
using Windows.Globalization;

namespace PowerOCR.Core.Ocr;

public sealed partial class HybridOcrRecognizer : IOcrRecognizer, IDisposable
{
    private readonly OneOcrRecognizer _oneOcr = new();
    private readonly WindowsOcrRecognizer _windowsOcr = new();

    public async Task<PowerOCR.Core.Models.OcrDocument> RecognizeAsync(
        Bitmap bitmap,
        Language language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(language);

        string languageTag = language.LanguageTag;

        bool isRussian =
            languageTag.Equals("ru", StringComparison.OrdinalIgnoreCase)
            || languageTag.StartsWith("ru-", StringComparison.OrdinalIgnoreCase);

        Trace.TraceInformation($"OCR language: {languageTag}");

        if (!isRussian)
        {
            Trace.TraceInformation("OCR engine selected: Windows OCR");
            return await _windowsOcr.RecognizeAsync(
                bitmap,
                language,
                cancellationToken);
        }

        try
        {
            Trace.TraceInformation("OCR engine selected: OneOCR");
            return await _oneOcr.RecognizeAsync(
                bitmap,
                language,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"OneOCR failed. Falling back to Windows OCR. Details: {exception}");

            return await _windowsOcr.RecognizeAsync(
                bitmap,
                language,
                cancellationToken);
        }
    }

    public void Dispose()
    {
        _oneOcr.Dispose();
    }
}
