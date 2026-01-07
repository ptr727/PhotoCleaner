using PhotoCleaner;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PhotoCleanerTests;

public class ExtensionsTests
{
    private class TestSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void LogAndPropagate_LogsExceptionAndReturnsFalse()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new InvalidOperationException("Test exception");

        // Act
        bool result = logger.LogAndPropagate(exception);

        // Assert
        Assert.False(result);
        _ = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Error, sink.Events[0].Level);
        Assert.Equal(exception, sink.Events[0].Exception);
    }

    [Fact]
    public void LogAndPropagate_IncludesFunctionNameInLog()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new InvalidOperationException("Test exception");

        // Act
        bool result = logger.LogAndPropagate(exception, "TestFunction");

        // Assert
        Assert.False(result);
        _ = Assert.Single(sink.Events);
        LogEventPropertyValue? functionValue = sink.Events[0]
            .Properties.GetValueOrDefault("Function");
        Assert.NotNull(functionValue);
        Assert.Contains("TestFunction", functionValue.ToString());
    }

    [Fact]
    public void LogAndPropagate_UsesCallerMemberNameWhenNotProvided()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new InvalidOperationException("Test exception");

        // Act - calling without explicit function name
        bool result = logger.LogAndPropagate(exception);

        // Assert
        Assert.False(result);
        _ = Assert.Single(sink.Events);
        LogEventPropertyValue? functionValue = sink.Events[0]
            .Properties.GetValueOrDefault("Function");
        Assert.NotNull(functionValue);
        // Should contain the calling method name
        Assert.Contains(
            "LogAndPropagate_UsesCallerMemberNameWhenNotProvided",
            functionValue.ToString()
        );
    }

    [Fact]
    public void LogAndHandle_LogsExceptionAndReturnsTrue()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new InvalidOperationException("Test exception");

        // Act
        bool result = logger.LogAndHandle(exception);

        // Assert
        Assert.True(result);
        _ = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Error, sink.Events[0].Level);
        Assert.Equal(exception, sink.Events[0].Exception);
    }

    [Fact]
    public void LogAndHandle_IncludesFunctionNameInLog()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new InvalidOperationException("Test exception");

        // Act
        bool result = logger.LogAndHandle(exception, "TestFunction");

        // Assert
        Assert.True(result);
        _ = Assert.Single(sink.Events);
        LogEventPropertyValue? functionValue = sink.Events[0]
            .Properties.GetValueOrDefault("Function");
        Assert.NotNull(functionValue);
        Assert.Contains("TestFunction", functionValue.ToString());
    }

    [Fact]
    public void LogAndHandle_UsesCallerMemberNameWhenNotProvided()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new InvalidOperationException("Test exception");

        // Act - calling without explicit function name
        bool result = logger.LogAndHandle(exception);

        // Assert
        Assert.True(result);
        _ = Assert.Single(sink.Events);
        LogEventPropertyValue? functionValue = sink.Events[0]
            .Properties.GetValueOrDefault("Function");
        Assert.NotNull(functionValue);
        // Should contain the calling method name
        Assert.Contains(
            "LogAndHandle_UsesCallerMemberNameWhenNotProvided",
            functionValue.ToString()
        );
    }

    [Fact]
    public void LogAndPropagate_WithDifferentExceptionTypes()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new ArgumentNullException("testParam", "Test null argument");

        // Act
        bool result = logger.LogAndPropagate(exception);

        // Assert
        Assert.False(result);
        _ = Assert.Single(sink.Events);
        _ = Assert.IsType<ArgumentNullException>(sink.Events[0].Exception);
    }

    [Fact]
    public void LogAndHandle_WithDifferentExceptionTypes()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception = new FileNotFoundException("File not found");

        // Act
        bool result = logger.LogAndHandle(exception);

        // Assert
        Assert.True(result);
        _ = Assert.Single(sink.Events);
        _ = Assert.IsType<FileNotFoundException>(sink.Events[0].Exception);
    }

    [Fact]
    public void LogAndPropagate_MultipleCallsLogMultipleEvents()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception1 = new InvalidOperationException("First exception");
        Exception exception2 = new ArgumentException("Second exception");

        // Act
        bool result1 = logger.LogAndPropagate(exception1);
        bool result2 = logger.LogAndPropagate(exception2);

        // Assert
        Assert.False(result1);
        Assert.False(result2);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(exception1, sink.Events[0].Exception);
        Assert.Equal(exception2, sink.Events[1].Exception);
    }

    [Fact]
    public void LogAndHandle_MultipleCallsLogMultipleEvents()
    {
        // Arrange
        TestSink sink = new();
        ILogger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Exception exception1 = new InvalidOperationException("First exception");
        Exception exception2 = new ArgumentException("Second exception");

        // Act
        bool result1 = logger.LogAndHandle(exception1);
        bool result2 = logger.LogAndHandle(exception2);

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(exception1, sink.Events[0].Exception);
        Assert.Equal(exception2, sink.Events[1].Exception);
    }
}
