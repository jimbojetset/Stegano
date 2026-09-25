using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Stegano
{
    internal enum SteganoError
    {
        UnsupportedImage,
        InsufficientCapacity,
        InvalidExtractionFile,
        UnsupportedVersion,
        CorruptedData,
        AuthenticationFailed
    }

    internal sealed class SteganoException(SteganoError error, string message, Exception? inner = null)
        : Exception(message, inner)
    {
        public SteganoError Error { get; } = error;
    }

    internal sealed record EmbeddedFile(string FileName, byte[] Contents);

    internal static class Stegano
    {
        internal enum Stealthiness : byte
        {
            None = 0,
            Maximum = 0xF8,
            Medium = 0xFC,
            Minimum = 0xFE
        }

        // Extraction file, little-endian: version (1), flags (1), stealth mask (1), reserved (1),
        // payload length (4), KDF iterations (4), salt (16), nonce (12), filename length (4),
        // followed by a GCM tag (16) or SHA-256 checksum (32). None of this is written to the image.
        // Image payload: UTF-8 filename then file contents, encrypted together if requested.
        // Bits run most-significant first through eligible pixels in row-major R, G, B order.
        internal const int MaxFilenameBytes = 255;
        private const int HeaderLength = 44;
        private const int TagLength = 16;
        private const int ChecksumLength = 32;
        private const int Iterations = 600_000;
        private static readonly UTF8Encoding Utf8 = new(false, true);

        // Reserve the full filename allowance even when the actual UTF-8 name is shorter.
        // The unused allowance is not written into the image.
        public static int GetCapacity(SKBitmap bitmap, string fileName, Stealthiness stealth = Stealthiness.Maximum)
        {
            ArgumentNullException.ThrowIfNull(fileName);
            GetFilenameByteCount(fileName);
            long storage = ValidateImage(bitmap, stealth);
            long available = Math.Min(storage, Array.MaxLength) - MaxFilenameBytes;
            return (int)Math.Max(0, available);
        }

        // Save ExtractionFile separately and pass it back unchanged to Extract. It contains
        // no password or key. The caller owns Bitmap; the source is never modified.
        public static (SKBitmap Bitmap, byte[] ExtractionFile) Embed(
            SKBitmap source, string fileName, byte[] contents, string? password = null,
            Stealthiness stealth = Stealthiness.Maximum)
        {
            ArgumentNullException.ThrowIfNull(fileName);
            ArgumentNullException.ThrowIfNull(contents);
            long storage = ValidateImage(source, stealth);
            bool encrypted = !string.IsNullOrEmpty(password);
            int filenameLength = GetFilenameByteCount(fileName);
            long storedLength = (long)filenameLength + contents.Length;
            if ((long)MaxFilenameBytes + contents.Length > Math.Min(storage, Array.MaxLength))
                throw new SteganoException(SteganoError.InsufficientCapacity, "Image is too small for this file and the reserved filename space.");

            byte[] extractionFile = new byte[HeaderLength + (encrypted ? TagLength : ChecksumLength)];
            Span<byte> header = extractionFile.AsSpan(0, HeaderLength);
            Span<byte> verification = extractionFile.AsSpan(HeaderLength);
            header[0] = 4;
            header[1] = encrypted ? (byte)1 : (byte)0;
            header[2] = (byte)stealth;
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], (int)storedLength);
            BinaryPrimitives.WriteInt32LittleEndian(header[40..], filenameLength);

            byte[] plaintext = new byte[(int)storedLength];
            Utf8.GetBytes(fileName.AsSpan(), plaintext.AsSpan(0, filenameLength));
            contents.CopyTo(plaintext, filenameLength);
            byte[] payload = new byte[(int)storedLength];
            try
            {
                if (encrypted)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(header[8..], Iterations);
                    RandomNumberGenerator.Fill(header.Slice(12, 16));
                    RandomNumberGenerator.Fill(header.Slice(28, 12));
                    byte[] key = DeriveKey(password!, header);
                    try
                    {
                        using var aes = new AesGcm(key, TagLength);
                        aes.Encrypt(header.Slice(28, 12), plaintext, payload, verification, header);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                }
                else
                {
                    plaintext.CopyTo(payload, 0);
                    ComputeChecksum(header, plaintext).CopyTo(verification);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            SKBitmap result = source.Copy() ?? throw new SteganoException(
                SteganoError.UnsupportedImage, "Unable to copy the image pixels.");
            bool complete = false;
            try
            {
                WriteBytes(result, payload, stealth);
                complete = true;
                return (result, extractionFile);
            }
            finally
            {
                if (!complete)
                    result.Dispose();
            }
        }

        public static EmbeddedFile Extract(SKBitmap source, byte[] extractionFile, string? password = null)
        {
            ArgumentNullException.ThrowIfNull(extractionFile);
            if (extractionFile.Length < HeaderLength)
                throw new SteganoException(SteganoError.InvalidExtractionFile, "Invalid or incomplete extraction file.");
            ReadOnlySpan<byte> header = extractionFile.AsSpan(0, HeaderLength);
            if (header[0] != 4)
                throw new SteganoException(SteganoError.UnsupportedVersion, "Unsupported extraction file version.");
            if (header[1] > 1 || header[3] != 0)
                throw new SteganoException(SteganoError.InvalidExtractionFile, "Invalid extraction file flags.");

            var stealth = (Stealthiness)header[2];
            if (!Enum.IsDefined(stealth))
                throw new SteganoException(SteganoError.InvalidExtractionFile, "Invalid stealth setting.");
            long storage = ValidateImage(source, stealth);

            // Validate unauthenticated parameters before allocating payload memory or deriving a key.
            bool encrypted = header[1] == 1;
            int verificationLength = encrypted ? TagLength : ChecksumLength;
            if (extractionFile.Length != HeaderLength + verificationLength)
                throw new SteganoException(SteganoError.InvalidExtractionFile, "Invalid extraction file size.");
            ReadOnlySpan<byte> verification = extractionFile.AsSpan(HeaderLength);
            int storedLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            int filenameLength = BinaryPrimitives.ReadInt32LittleEndian(header[40..]);
            if (storedLength < 0 || storedLength > Array.MaxLength || storedLength > storage
                || filenameLength < 0 || filenameLength > MaxFilenameBytes || filenameLength > storedLength)
                throw new SteganoException(SteganoError.InvalidExtractionFile, "Invalid payload length or insufficient image capacity.");
            if (encrypted)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Iterations)
                    throw new SteganoException(SteganoError.InvalidExtractionFile, "Unsupported password derivation parameters.");
                if (string.IsNullOrEmpty(password))
                    throw new SteganoException(SteganoError.AuthenticationFailed, "A password is required for this embedded file.");
            }
            else
            {
                foreach (byte value in header.Slice(8, 32))
                    if (value != 0)
                        throw new SteganoException(SteganoError.InvalidExtractionFile, "Unencrypted extraction file contains encryption parameters.");
            }

            byte[] payload = ReadBytes(source, storedLength, stealth);
            byte[] plaintext;
            if (encrypted)
            {
                plaintext = new byte[storedLength];
                byte[] key = DeriveKey(password!, header);
                try
                {
                    using var aes = new AesGcm(key, TagLength);
                    aes.Decrypt(header.Slice(28, 12), payload, verification, plaintext, header);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    throw new SteganoException(SteganoError.AuthenticationFailed,
                        "Wrong password, mismatched extraction file, or damaged image data.", ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            else
            {
                byte[] checksum = ComputeChecksum(header, payload);
                if (!CryptographicOperations.FixedTimeEquals(checksum, verification))
                    throw new SteganoException(SteganoError.CorruptedData, "Image data or extraction file does not match its checksum.");
                plaintext = payload;
            }

            try
            {
                string fileName;
                try
                {
                    fileName = Utf8.GetString(plaintext, 0, filenameLength);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new SteganoException(SteganoError.CorruptedData, "Embedded filename is not valid UTF-8.", ex);
                }
                return new EmbeddedFile(fileName, plaintext.AsSpan(filenameLength).ToArray());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static int GetFilenameByteCount(string fileName)
        {
            int length = Utf8.GetByteCount(fileName);
            if (length > MaxFilenameBytes)
                throw new ArgumentException($"The filename, including its extension, must not exceed {MaxFilenameBytes} UTF-8 bytes.", nameof(fileName));
            return length;
        }

        private static byte[] DeriveKey(string password, ReadOnlySpan<byte> header) =>
            Rfc2898DeriveBytes.Pbkdf2(password, header.Slice(12, 16), Iterations, HashAlgorithmName.SHA256, 32);

        private static byte[] ComputeChecksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header);
            hash.AppendData(payload);
            return hash.GetHashAndReset();
        }

        private static long ValidateImage(SKBitmap bitmap, Stealthiness stealth)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            if (!Enum.IsDefined(stealth))
                throw new ArgumentOutOfRangeException(nameof(stealth));
            if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.GetPixels() == IntPtr.Zero
                || (bitmap.ColorType != SKColorType.Rgba8888 && bitmap.ColorType != SKColorType.Bgra8888))
                throw new SteganoException(SteganoError.UnsupportedImage, "Image must contain 8-bit RGBA or BGRA pixels.");
            // Partial alpha can change RGB low bits when Skia converts premultiplied colours.
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).Alpha != 255)
                        throw new SteganoException(SteganoError.UnsupportedImage, "Image must be fully opaque.");
            long pixels = 0;
            foreach (var (_, _) in EmbeddingPixels(bitmap, stealth))
                pixels++;
            return pixels * 3 / 8;
        }

        private static IEnumerable<(int X, int Y)> EmbeddingPixels(SKBitmap bitmap, Stealthiness stealth)
        {
            int mask = (int)stealth;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (stealth != Stealthiness.None)
                    {
                        if (x == 0 || y == 0 || x == bitmap.Width - 1 || y == bitmap.Height - 1)
                            continue;
                        // Every mask ignores the embedded low bit, so writing a pixel cannot
                        // change whether it or its neighbours qualify during extraction.
                        int colour = MaskedColour(bitmap.GetPixel(x, y), mask);
                        if (colour == MaskedColour(bitmap.GetPixel(x - 1, y), mask)
                            && colour == MaskedColour(bitmap.GetPixel(x + 1, y), mask)
                            && colour == MaskedColour(bitmap.GetPixel(x, y - 1), mask)
                            && colour == MaskedColour(bitmap.GetPixel(x, y + 1), mask))
                            continue;
                    }
                    yield return (x, y);
                }
        }

        private static int MaskedColour(SKColor pixel, int mask) =>
            ((pixel.Red & mask) << 16) | ((pixel.Green & mask) << 8) | (pixel.Blue & mask);

        private static byte[] ReadBytes(SKBitmap bitmap, int length, Stealthiness stealth)
        {
            byte[] bytes = new byte[length];
            long end = (long)length * 8;
            long bit = 0;
            if (end == 0)
                return bytes;
            foreach (var (x, y) in EmbeddingPixels(bitmap, stealth))
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                for (int channel = 0; channel < 3 && bit < end; channel++, bit++)
                {
                    int value = channel == 0 ? pixel.Red : channel == 1 ? pixel.Green : pixel.Blue;
                    bytes[(int)(bit >> 3)] |= (byte)((value & 1) << (7 - (int)(bit & 7)));
                }
                if (bit == end)
                    return bytes;
            }
            throw new SteganoException(SteganoError.CorruptedData, "Image has too few eligible pixels for the payload.");
        }

        private static void WriteBytes(SKBitmap bitmap, ReadOnlySpan<byte> bytes, Stealthiness stealth)
        {
            long end = (long)bytes.Length * 8;
            long bit = 0;
            if (end == 0)
                return;
            foreach (var (x, y) in EmbeddingPixels(bitmap, stealth))
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                byte red = pixel.Red, green = pixel.Green, blue = pixel.Blue;
                for (int channel = 0; channel < 3 && bit < end; channel++, bit++)
                {
                    int value = (bytes[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1;
                    switch (channel)
                    {
                        case 0: red = (byte)((red & 0xFE) | value); break;
                        case 1: green = (byte)((green & 0xFE) | value); break;
                        case 2: blue = (byte)((blue & 0xFE) | value); break;
                    }
                }
                bitmap.SetPixel(x, y, new SKColor(red, green, blue, pixel.Alpha));
                if (bit == end)
                    return;
            }
            throw new SteganoException(SteganoError.InsufficientCapacity, "Image has too few eligible pixels for the payload.");
        }
    }
}
