using System;
using System.IO;
using UnityEngine;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingCompositingMode = System.Drawing.Drawing2D.CompositingMode;
using DrawingCompositingQuality = System.Drawing.Drawing2D.CompositingQuality;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using DrawingInterpolationMode = System.Drawing.Drawing2D.InterpolationMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingPixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode;
using DrawingRectangle = System.Drawing.Rectangle;

namespace TerrainLab
{
    internal static class TerrainImagePreviewLoader
    {
        private const int MaximumPreviewDimension = 2048;
        private const long MaximumPreviewFileBytes =
            256L * 1024L * 1024L;

        public static Texture2D Load(
            string path,
            string textureName,
            out int sourceWidth,
            out int sourceHeight)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException(
                    "Source image was not found.",
                    path);
            }
            if (info.Length > MaximumPreviewFileBytes)
            {
                throw new InvalidDataException(
                    "TerrainLab image preview is limited to 256 MiB per file.");
            }

            byte[] png;
            using (DrawingImage source = DrawingImage.FromFile(path))
            {
                sourceWidth = source.Width;
                sourceHeight = source.Height;
                float previewScale = Math.Min(
                    1f,
                    MaximumPreviewDimension /
                    (float)Math.Max(sourceWidth, sourceHeight));
                int previewWidth = Math.Max(
                    1,
                    (int)Math.Round(sourceWidth * previewScale));
                int previewHeight = Math.Max(
                    1,
                    (int)Math.Round(sourceHeight * previewScale));
                using (DrawingBitmap bitmap = new DrawingBitmap(
                    previewWidth,
                    previewHeight,
                    DrawingPixelFormat.Format32bppArgb))
                using (DrawingGraphics graphics =
                       DrawingGraphics.FromImage(bitmap))
                using (MemoryStream stream = new MemoryStream())
                {
                    graphics.CompositingMode =
                        DrawingCompositingMode.SourceCopy;
                    graphics.CompositingQuality =
                        DrawingCompositingQuality.HighQuality;
                    graphics.InterpolationMode =
                        DrawingInterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode =
                        DrawingPixelOffsetMode.HighQuality;
                    graphics.DrawImage(
                        source,
                        new DrawingRectangle(
                            0,
                            0,
                            previewWidth,
                            previewHeight));
                    bitmap.Save(stream, DrawingImageFormat.Png);
                    png = stream.ToArray();
                }
            }

            Texture2D texture = new Texture2D(
                2,
                2,
                TextureFormat.RGBA32,
                false)
            {
                name = string.IsNullOrWhiteSpace(textureName)
                    ? "TerrainLabImagePreview"
                    : textureName,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.LoadImage(png, true))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException(
                    "Unity could not decode the TerrainLab image preview.");
            }

            return texture;
        }
    }
}
