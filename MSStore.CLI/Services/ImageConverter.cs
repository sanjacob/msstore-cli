// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MSStore.CLI.Services
{
    internal class ImageConverter : IImageConverter
    {
        private ILogger<ImageConverter> _logger;

        public ImageConverter(ILogger<ImageConverter> logger)
        {
            _logger = logger;
        }

        public async Task<bool> ConvertIcoToPngAsync(string sourceFilePath, string destinationFilePath, CancellationToken ct)
        {
            try
            {
                using var bitmap = SKBitmap.Decode(sourceFilePath);

                using MemoryStream memStream = new MemoryStream();
                using (SKManagedWStream wstream = new SKManagedWStream(memStream))
                {
                    bitmap.Encode(wstream, SKEncodedImageFormat.Png, 100);
                }

                memStream.Seek(0, SeekOrigin.Begin);

                using var fileStream = new FileStream(destinationFilePath, FileMode.Create);
                await memStream.CopyToAsync(fileStream, ct);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to convert ICO to PNG");
                return false;
            }
        }
    }
}
