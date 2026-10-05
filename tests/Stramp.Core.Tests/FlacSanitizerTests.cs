using NAudio.Wave;
using Stramp.Audio;
using Xunit;

namespace Stramp.Core.Tests;

public class FlacSanitizerTests
{
    [Fact]
    public void NonFlacFile_ReturnsFalse()
    {
        var tempSource = Path.Combine(Path.GetTempPath(), $"test_nonflac_{Guid.NewGuid():N}.txt");
        var tempTarget = Path.Combine(Path.GetTempPath(), $"test_target_{Guid.NewGuid():N}.flac");
        try
        {
            File.WriteAllText(tempSource, "This is not a flac file.");
            var result = FlacSanitizer.TryStripPictureBlocks(tempSource, tempTarget);
            Assert.False(result);
            Assert.False(File.Exists(tempTarget));
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
            if (File.Exists(tempTarget)) File.Delete(tempTarget);
        }
    }

    [Fact]
    public void FlacWithoutPicture_ReturnsFalse()
    {
        var tempSource = Path.Combine(Path.GetTempPath(), $"test_no_pic_{Guid.NewGuid():N}.flac");
        var tempTarget = Path.Combine(Path.GetTempPath(), $"test_target_{Guid.NewGuid():N}.flac");
        try
        {
            using (var fs = File.Create(tempSource))
            {
                // 'fLaC'
                fs.Write([(byte)'f', (byte)'L', (byte)'a', (byte)'C']);
                // StreamInfo block (type 0, length 34, isLast = true)
                fs.WriteByte(0x80); // isLast=true, type=0
                fs.WriteByte(0);
                fs.WriteByte(0);
                fs.WriteByte(34); // length = 34
                fs.Write(new byte[34]);
            }

            var result = FlacSanitizer.TryStripPictureBlocks(tempSource, tempTarget);
            Assert.False(result);
            Assert.False(File.Exists(tempTarget));
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
            if (File.Exists(tempTarget)) File.Delete(tempTarget);
        }
    }

    [Fact]
    public void FlacWithPicture_StripsPictureBlockSuccessfully()
    {
        var tempSource = Path.Combine(Path.GetTempPath(), $"test_with_pic_{Guid.NewGuid():N}.flac");
        var tempTarget = Path.Combine(Path.GetTempPath(), $"test_target_{Guid.NewGuid():N}.flac");
        try
        {
            using (var fs = File.Create(tempSource))
            {
                // 'fLaC'
                fs.Write([(byte)'f', (byte)'L', (byte)'a', (byte)'C']);
                // StreamInfo block (type 0, length 34, isLast = false)
                fs.WriteByte(0x00);
                fs.WriteByte(0);
                fs.WriteByte(0);
                fs.WriteByte(34);
                fs.Write(new byte[34]);
                // Picture block (type 6, length 100, isLast = true)
                fs.WriteByte(0x86); // isLast=true, type=6
                fs.WriteByte(0);
                fs.WriteByte(0);
                fs.WriteByte(100);
                fs.Write(new byte[100]);
                // Fake audio frame byte
                fs.WriteByte(0xFF);
            }

            var result = FlacSanitizer.TryStripPictureBlocks(tempSource, tempTarget);
            Assert.True(result);
            Assert.True(File.Exists(tempTarget));

            // Verify target contents
            var targetBytes = File.ReadAllBytes(tempTarget);
            // 4 header + 4 streaminfo header + 34 streaminfo data + 1 audio frame = 43 bytes
            Assert.Equal(43, targetBytes.Length);
            // Verify StreamInfo in target now has isLast = true (0x80)
            Assert.Equal(0x80, targetBytes[4]);
            Assert.Equal(0xFF, targetBytes[42]);
        }
        finally
        {
            if (File.Exists(tempSource)) File.Delete(tempSource);
            if (File.Exists(tempTarget)) File.Delete(tempTarget);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RealFailingFlacFile_WhenPresent_IsSanitizedAndPlayableByMediaFoundation()
    {
        var realPath = @"C:\Users\dylan\Music\Games\legend-of-zelda-the-twilight-princess-gamerip\Wolf Song 7 - Ballad of Twilight (Howling Stone).flac";
        if (!File.Exists(realPath))
            return;

        var tempTarget = Path.Combine(Path.GetTempPath(), $"test_target_{Guid.NewGuid():N}.flac");
        try
        {
            var stripped = FlacSanitizer.TryStripPictureBlocks(realPath, tempTarget);
            Assert.True(stripped);
            Assert.True(File.Exists(tempTarget));

            using var reader = new MediaFoundationReader(tempTarget, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = true,
                RepositionInRead = true,
            });
            Assert.True(reader.TotalTime.TotalSeconds > 10);
        }
        finally
        {
            if (File.Exists(tempTarget)) File.Delete(tempTarget);
        }
    }
}
