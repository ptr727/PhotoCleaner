using PhotoCleaner;

namespace PhotoCleanerTests;

public class ProcessTaskTests
{
    [Fact]
    public void GetFileMediaExtensionMultipleMediaExtensionsReturnsCorrectSplit()
    {
        // Arrange - Use platform-independent paths
        (string, string, string)[] testCases =
        [
            (
                Path.Combine("path", "to", "file.ext.heic.jpg"),
                Path.Combine("path", "to", "file.ext"),
                ".heic.jpg"
            ),
            (
                Path.Combine("path", "to", "file.heic.jpg.ext"),
                Path.Combine("path", "to", "file.heic.jpg.ext"),
                ""
            ),
            (
                Path.Combine("path", "to", "file.jpeg.heic"),
                Path.Combine("path", "to", "file"),
                ".jpeg.heic"
            ),
            (Path.Combine("path", "to", "file"), Path.Combine("path", "to", "file"), ""),
            (Path.Combine("path", "to", "file.ext"), Path.Combine("path", "to", "file.ext"), ""),
        ];

        foreach (
            (string? filePath, string? expectedBaseName, string? expectedExtension) in testCases
        )
        {
            // Act
            ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

            // Assert
            Assert.Equal(expectedBaseName, baseName);
            Assert.Equal(expectedExtension, extension);
        }
    }

    [Fact]
    public void GetFileMediaExtensionNoExtensionReturnsFullPathAsBase()
    {
        // Arrange - Use platform-independent paths
        (string, string, string)[] testCases =
        [
            ("file", "file", ""),
            (Path.Combine("path", "to", "file"), Path.Combine("path", "to", "file"), ""),
        ];

        foreach (
            (string? filePath, string? expectedBaseName, string? expectedExtension) in testCases
        )
        {
            // Act
            ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

            // Assert
            Assert.Equal(expectedBaseName, baseName);
            Assert.Equal(expectedExtension, extension);
        }
    }

    [Fact]
    public void GetFileMediaExtensionNonMediaExtensionReturnsFullPathAsBase()
    {
        // Arrange - Use platform-independent paths
        (string, string, string)[] testCases =
        [
            (Path.Combine("path", "to", "file.ext"), Path.Combine("path", "to", "file.ext"), ""),
            (Path.Combine("path", "file.jpeg.ext"), Path.Combine("path", "file.jpeg.ext"), ""),
            (Path.Combine("path", "file.txt.doc"), Path.Combine("path", "file.txt.doc"), ""),
        ];

        foreach (
            (string? filePath, string? expectedBaseName, string? expectedExtension) in testCases
        )
        {
            // Act
            ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

            // Assert
            Assert.Equal(expectedBaseName, baseName);
            Assert.Equal(expectedExtension, extension);
        }
    }

    [Fact]
    public void GetFileMediaExtensionSingleMediaExtensionReturnsCorrectSplit()
    {
        // Arrange - Use platform-independent paths
        (string, string, string)[] testCases =
        [
            (Path.Combine("path", "to", "file.jpg"), Path.Combine("path", "to", "file"), ".jpg"),
            (Path.Combine("photos", "image.heic"), Path.Combine("photos", "image"), ".heic"),
            (Path.Combine("Pictures", "video.mp4"), Path.Combine("Pictures", "video"), ".mp4"),
            (Path.Combine("media", "audio.mov"), Path.Combine("media", "audio"), ".mov"),
            (Path.Combine("path", "document.tif"), Path.Combine("path", "document"), ".tif"),
        ];

        foreach (
            (string? filePath, string? expectedBaseName, string? expectedExtension) in testCases
        )
        {
            // Act
            ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

            // Assert
            Assert.Equal(expectedBaseName, baseName);
            Assert.Equal(expectedExtension, extension);
        }
    }

    [Fact]
    public void GetFileMediaExtensionMediaExtensionFollowedByNonMediaReturnsFullPathAsBase()
    {
        // Arrange - Use platform-independent paths
        (string, string, string)[] testCases =
        [
            (Path.Combine("path", "file.jpeg.txt"), Path.Combine("path", "file.jpeg.txt"), ""),
            (
                Path.Combine("path", "file.jpg.doc.pdf"),
                Path.Combine("path", "file.jpg.doc.pdf"),
                ""
            ),
            (
                Path.Combine("path", "file.heic.ext.unknown"),
                Path.Combine("path", "file.heic.ext.unknown"),
                ""
            ),
        ];

        foreach (
            (string? filePath, string? expectedBaseName, string? expectedExtension) in testCases
        )
        {
            // Act
            ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

            // Assert
            Assert.Equal(expectedBaseName, baseName);
            Assert.Equal(expectedExtension, extension);
        }
    }

    [Fact]
    public void GetFileMediaExtensionCaseInsensitiveMediaExtensionsReturnsCorrectSplit()
    {
        // Arrange - Use platform-independent paths
        (string, string, string)[] testCases =
        [
            (Path.Combine("path", "file.JPEG.HEIC"), Path.Combine("path", "file"), ".JPEG.HEIC"),
            (Path.Combine("path", "file.Jpeg.Heic"), Path.Combine("path", "file"), ".Jpeg.Heic"),
            (Path.Combine("path", "file.JPG"), Path.Combine("path", "file"), ".JPG"),
            (Path.Combine("path", "file.Jpg"), Path.Combine("path", "file"), ".Jpg"),
        ];

        foreach (
            (string? filePath, string? expectedBaseName, string? expectedExtension) in testCases
        )
        {
            // Act
            ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

            // Assert
            Assert.Equal(expectedBaseName, baseName);
            Assert.Equal(expectedExtension, extension);
        }
    }

    [Fact]
    public void GetFileMediaExtensionComplexPathsWithDotsReturnsCorrectSplit()
    {
        // Arrange - Use platform-independent paths
        (string, string, string)[] testCases =
        [
            (
                Path.Combine("complex", "path.with.dots", "file.backup.jpg"),
                Path.Combine("complex", "path.with.dots", "file.backup"),
                ".jpg"
            ),
            (
                Path.Combine("path", "file.v1.jpeg.heic"),
                Path.Combine("path", "file.v1"),
                ".jpeg.heic"
            ),
            (
                Path.Combine("path", "file.2024.01.01.mp4"),
                Path.Combine("path", "file.2024.01.01"),
                ".mp4"
            ),
        ];

        foreach (
            (string? filePath, string? expectedBaseName, string? expectedExtension) in testCases
        )
        {
            // Act
            ProcessTask.GetFileMediaExtension(filePath, out string baseName, out string extension);

            // Assert
            Assert.Equal(expectedBaseName, baseName);
            Assert.Equal(expectedExtension, extension);
        }
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
            {
                File.Delete(tempFile);
            }
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
            {
                File.Delete(tempFile);
            }

            if (File.Exists(bakFile))
            {
                File.Delete(bakFile);
            }
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
            {
                File.Delete(tempFile);
            }

            if (File.Exists(bakFile))
            {
                File.Delete(bakFile);
            }

            if (File.Exists(bak1File))
            {
                File.Delete(bak1File);
            }
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
            {
                File.Delete(tempFile);
            }

            if (File.Exists(bakFile))
            {
                File.Delete(bakFile);
            }

            if (File.Exists(bak1File))
            {
                File.Delete(bak1File);
            }

            if (File.Exists(bak2File))
            {
                File.Delete(bak2File);
            }

            if (File.Exists(bak3File))
            {
                File.Delete(bak3File);
            }
        }
    }

    [Fact]
    public void GetBackupFileName_WithFileInSubdirectory_ReturnsCorrectPath()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"testdir_{Guid.NewGuid()}");
        _ = Directory.CreateDirectory(tempDir);
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
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
