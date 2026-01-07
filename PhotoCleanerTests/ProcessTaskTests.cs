using PhotoCleaner;

namespace PhotoCleanerTests;

public class ProcessTaskTests
{
    [Theory]
    [InlineData("/path/to/file.ext.heic.jpg", "/path/to/file.ext", ".heic.jpg")]
    [InlineData("/path/to/file.heic.jpg.ext", "/path/to/file.heic.jpg.ext", "")]
    [InlineData("/path/to/file.jpeg.heic", "/path/to/file", ".jpeg.heic")]
    [InlineData("/path/to/file", "/path/to/file", "")]
    [InlineData("/path/to/file.ext", "/path/to/file.ext", "")]
    public void GetFileMediaExtensionMultipleMediaExtensionsReturnsCorrectSplit(
        string filePath,
        string expectedBaseName,
        string expectedExtension
    )
    {
        // Act
        ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

        // Assert
        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData("/file", "/file", "")]
    [InlineData("/path/to/file", "/path/to/file", "")]
    [InlineData("file", "file", "")]
    public void GetFileMediaExtensionNoExtensionReturnsFullPathAsBase(
        string filePath,
        string expectedBaseName,
        string expectedExtension
    )
    {
        // Act
        ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

        // Assert
        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData("/file.ext", "/file.ext", "")]
    [InlineData("/path/to/file.ext", "/path/to/file.ext", "")]
    [InlineData("/path/file.jpeg.ext", "/path/file.jpeg.ext", "")]
    [InlineData("/path/file.txt.doc", "/path/file.txt.doc", "")]
    public void GetFileMediaExtensionNonMediaExtensionReturnsFullPathAsBase(
        string filePath,
        string expectedBaseName,
        string expectedExtension
    )
    {
        // Act
        ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

        // Assert
        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData("/path/to/file.jpg", "/path/to/file", ".jpg")]
    [InlineData("/photos/image.heic", "/photos/image", ".heic")]
    [InlineData("C:\\Pictures\\video.mp4", "C:\\Pictures\\video", ".mp4")]
    [InlineData("/media/audio.mov", "/media/audio", ".mov")]
    [InlineData("/path/document.tif", "/path/document", ".tif")]
    public void GetFileMediaExtensionSingleMediaExtensionReturnsCorrectSplit(
        string filePath,
        string expectedBaseName,
        string expectedExtension
    )
    {
        // Act
        ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

        // Assert
        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData("/path/file.jpeg.txt", "/path/file.jpeg.txt", "")]
    [InlineData("/path/file.jpg.doc.pdf", "/path/file.jpg.doc.pdf", "")]
    [InlineData("/path/file.heic.ext.unknown", "/path/file.heic.ext.unknown", "")]
    public void GetFileMediaExtensionMediaExtensionFollowedByNonMediaReturnsFullPathAsBase(
        string filePath,
        string expectedBaseName,
        string expectedExtension
    )
    {
        // Act
        ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

        // Assert
        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData("/path/file.JPEG.HEIC", "/path/file", ".JPEG.HEIC")]
    [InlineData("/path/file.Jpeg.Heic", "/path/file", ".Jpeg.Heic")]
    [InlineData("/path/file.JPG", "/path/file", ".JPG")]
    [InlineData("/path/file.Jpg", "/path/file", ".Jpg")]
    public void GetFileMediaExtensionCaseInsensitiveMediaExtensionsReturnsCorrectSplit(
        string filePath,
        string expectedBaseName,
        string expectedExtension
    )
    {
        // Act
        ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

        // Assert
        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData(
        "/complex/path.with.dots/file.backup.jpg",
        "/complex/path.with.dots/file.backup",
        ".jpg"
    )]
    [InlineData("/path/file.v1.jpeg.heic", "/path/file.v1", ".jpeg.heic")]
    [InlineData("/path/file.2024.01.01.mp4", "/path/file.2024.01.01", ".mp4")]
    public void GetFileMediaExtensionComplexPathsWithDotsReturnsCorrectSplit(
        string filePath,
        string expectedBaseName,
        string expectedExtension
    )
    {
        // Act
        ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

        // Assert
        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".JPG")]
    [InlineData(".jpeg")]
    [InlineData(".JPEG")]
    [InlineData(".png")]
    [InlineData(".PNG")]
    [InlineData(".heic")]
    [InlineData(".HEIC")]
    [InlineData(".mp4")]
    [InlineData(".MP4")]
    public void IsMixedCaseExtensionAllLowercaseOrUppercaseReturnsFalse(string extension)
    {
        // Act
        bool result = ProcessTask.IsMixedCaseExtension(extension.AsSpan());

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(".Jpg")]
    [InlineData(".jPg")]
    [InlineData(".jpG")]
    [InlineData(".JPg")]
    [InlineData(".jPG")]
    [InlineData(".JpG")]
    [InlineData(".Jpeg")]
    [InlineData(".hEic")]
    [InlineData(".Mp4")]
    public void IsMixedCaseExtensionMixedCaseReturnsTrue(string extension)
    {
        // Act
        bool result = ProcessTask.IsMixedCaseExtension(extension.AsSpan());

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void IsMixedCaseExtensionEmptyOrDotOnlyReturnsFalse(string extension)
    {
        // Act
        bool result = ProcessTask.IsMixedCaseExtension(extension.AsSpan());

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(".123")]
    [InlineData(".456")]
    [InlineData("...")]
    public void IsMixedCaseExtensionNumericOnlyReturnsFalse(string extension)
    {
        // Act
        bool result = ProcessTask.IsMixedCaseExtension(extension.AsSpan());

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetBackupFileName_WithNewFile_ReturnsFileNameWithBakExtension()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.txt");
        File.WriteAllText(tempFile, "test");

        try
        {
            // Act
            string result = ProcessTask.GetBackupFileName(tempFile);

            // Assert
            Assert.Equal(tempFile + ".bak", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetBackupFileName_WhenBakExists_ReturnsFileNameWithBak1()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.txt");
        string bakFile = tempFile + ".bak";
        File.WriteAllText(tempFile, "test");
        File.WriteAllText(bakFile, "backup");

        try
        {
            // Act
            string result = ProcessTask.GetBackupFileName(tempFile);

            // Assert
            Assert.Equal(tempFile + ".bak1", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
            if (File.Exists(bakFile))
                File.Delete(bakFile);
        }
    }

    [Fact]
    public void GetBackupFileName_WhenBakAndBak1Exist_ReturnsFileNameWithBak2()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.txt");
        string bakFile = tempFile + ".bak";
        string bak1File = tempFile + ".bak1";
        File.WriteAllText(tempFile, "test");
        File.WriteAllText(bakFile, "backup");
        File.WriteAllText(bak1File, "backup1");

        try
        {
            // Act
            string result = ProcessTask.GetBackupFileName(tempFile);

            // Assert
            Assert.Equal(tempFile + ".bak2", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
            if (File.Exists(bakFile))
                File.Delete(bakFile);
            if (File.Exists(bak1File))
                File.Delete(bak1File);
        }
    }

    [Fact]
    public void GetBackupFileName_IncrementsCounterUntilAvailable()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.txt");
        string bakFile = tempFile + ".bak";
        string bak1File = tempFile + ".bak1";
        string bak2File = tempFile + ".bak2";
        string bak3File = tempFile + ".bak3";
        File.WriteAllText(tempFile, "test");
        File.WriteAllText(bakFile, "backup");
        File.WriteAllText(bak1File, "backup1");
        File.WriteAllText(bak2File, "backup2");
        File.WriteAllText(bak3File, "backup3");

        try
        {
            // Act
            string result = ProcessTask.GetBackupFileName(tempFile);

            // Assert
            Assert.Equal(tempFile + ".bak4", result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
            if (File.Exists(bakFile))
                File.Delete(bakFile);
            if (File.Exists(bak1File))
                File.Delete(bak1File);
            if (File.Exists(bak2File))
                File.Delete(bak2File);
            if (File.Exists(bak3File))
                File.Delete(bak3File);
        }
    }

    [Fact]
    public void GetBackupFileName_WithFileInSubdirectory_ReturnsCorrectPath()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"testdir_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, "test.txt");
        File.WriteAllText(tempFile, "test");

        try
        {
            // Act
            string result = ProcessTask.GetBackupFileName(tempFile);

            // Assert
            Assert.Equal(tempFile + ".bak", result);
            Assert.Contains(tempDir, result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
