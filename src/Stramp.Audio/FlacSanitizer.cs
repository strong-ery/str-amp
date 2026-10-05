namespace Stramp.Audio;

/// <summary>
/// Strips embedded picture metadata blocks from FLAC files without re-encoding audio frames.
/// Used when Windows Media Foundation's FLAC byte stream handler fails on oversized metadata.
/// </summary>
public static class FlacSanitizer
{
    public static bool TryStripPictureBlocks(string sourcePath, string targetPath)
    {
        try
        {
            using var inStream = File.OpenRead(sourcePath);
            if (inStream.Length < 4)
                return false;

            var marker = new byte[4];
            if (inStream.Read(marker, 0, 4) != 4 ||
                marker[0] != 0x66 || marker[1] != 0x4C || marker[2] != 0x61 || marker[3] != 0x43)
            {
                return false;
            }

            using var outStream = File.Create(targetPath);
            outStream.Write(marker, 0, 4);

            var blocks = new List<(byte type, byte[] data)>();
            var isLast = false;
            var strippedAny = false;

            while (!isLast)
            {
                var header = new byte[4];
                if (inStream.Read(header, 0, 4) != 4)
                {
                    CleanupTarget(targetPath, outStream);
                    return false;
                }

                isLast = (header[0] & 0x80) != 0;
                var blockType = (byte)(header[0] & 0x7F);
                var length = (header[1] << 16) | (header[2] << 8) | header[3];

                if (length < 0 || inStream.Position + length > inStream.Length)
                {
                    CleanupTarget(targetPath, outStream);
                    return false;
                }

                if (blockType == 6) // PICTURE
                {
                    strippedAny = true;
                    inStream.Position += length;
                }
                else
                {
                    var data = new byte[length];
                    if (inStream.Read(data, 0, length) != length)
                    {
                        CleanupTarget(targetPath, outStream);
                        return false;
                    }
                    blocks.Add((blockType, data));
                }
            }

            if (!strippedAny || blocks.Count == 0)
            {
                CleanupTarget(targetPath, outStream);
                return false;
            }

            for (var i = 0; i < blocks.Count; i++)
            {
                var (type, data) = blocks[i];
                var currentIsLast = i == blocks.Count - 1;
                var b0 = (byte)((currentIsLast ? 0x80 : 0) | (type & 0x7F));
                outStream.WriteByte(b0);
                outStream.WriteByte((byte)((data.Length >> 16) & 0xFF));
                outStream.WriteByte((byte)((data.Length >> 8) & 0xFF));
                outStream.WriteByte((byte)(data.Length & 0xFF));
                outStream.Write(data, 0, data.Length);
            }

            inStream.CopyTo(outStream);
            outStream.Flush();
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
            }
            catch
            {
            }
            return false;
        }
    }

    private static void CleanupTarget(string targetPath, Stream outStream)
    {
        try
        {
            outStream.Dispose();
            if (File.Exists(targetPath))
                File.Delete(targetPath);
        }
        catch
        {
        }
    }
}
