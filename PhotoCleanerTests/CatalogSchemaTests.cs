using Microsoft.Data.Sqlite;
using PhotoCleaner;

namespace PhotoCleanerTests;

// These tests need neither exiftool nor ffmpeg, so they run on a runner that has neither.
public sealed class CatalogSchemaTests
{
    [Fact]
    public async Task InitializeAsync_OldSchemaDatabase_UpgradesInPlaceKeepingRows()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - a database in the shape written before the catalog columns existed
            string dbPath = Path.Combine(workDir, "old.db");
            await using (SqliteConnection connection = new($"Data Source={dbPath};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE files (
                        path         TEXT NOT NULL PRIMARY KEY,
                        sha256       TEXT NOT NULL,
                        sha1         TEXT NOT NULL,
                        file_size    INTEGER NOT NULL,
                        mtime_ticks  INTEGER NOT NULL,
                        is_processed INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO files VALUES ('/photos/IMG_1234.heic', 'sha256-a', 'sha1-a', 10, 20, 1)
                    """;
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // Act
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            FileRecord? record = await db.GetByPathAsync(
                "/photos/IMG_1234.heic",
                TestContext.Current.CancellationToken
            );

            // Assert - rows intact, metadata marked as not read yet
            record.Should().NotBeNull();
            record.Sha256.Should().Be("sha256-a");
            record.Sha1.Should().Be("sha1-a");
            record.FileSize.Should().Be(10);
            record.MtimeTicks.Should().Be(20);
            record.IsProcessed.Should().BeTrue();
            record.MetadataRead.Should().BeFalse();
            record.ContentIdentifier.Should().BeNull();
            record.ImageHash.Should().BeNull();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task InitializeAsync_OldSchemaReadOnly_StillAnswersHashLookups()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - a skip database written before the catalog columns existed
            string dbPath = Path.Combine(workDir, "old.db");
            await using (SqliteConnection connection = new($"Data Source={dbPath};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE files (
                        path TEXT NOT NULL PRIMARY KEY, sha256 TEXT NOT NULL, sha1 TEXT NOT NULL,
                        file_size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
                        is_processed INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO files VALUES ('/photos/IMG_1234.heic', 'sha256-a', 'sha1-a', 10, 20, 0)
                    """;
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // Act
            await using Database db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            bool found = await db.Sha256ExistsAsync(
                "sha256-a",
                TestContext.Current.CancellationToken
            );

            // Assert
            found.Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void FromExifTool_ErrorReported_LeavesRowUnread()
    {
        FileMetadata
            .FromExifTool(new ExifToolJson { ExifToolError = "cannot read" })
            .Should()
            .BeNull();
        FileMetadata.FromExifTool(null).Should().BeNull();
        FileMetadata
            .FromExifTool(new ExifToolJson { ImageDataHash = "abc" })
            .Should()
            .BeEquivalentTo(new FileMetadata(null, "abc"));
    }
}
