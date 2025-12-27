using PhotoCleaner;
using Xunit;

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
    [InlineData("/path/file.JPEG.HEIC", "/path/file", ".jpeg.heic")]
    [InlineData("/path/file.JPG.PNG", "/path/file", ".jpg.png")]
    [InlineData("/path/file.MP4.MOV", "/path/file", ".mp4.mov")]
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
}
