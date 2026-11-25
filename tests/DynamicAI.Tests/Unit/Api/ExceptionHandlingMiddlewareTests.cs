using DynamicAi.Api.Middleware;
using DynamicAi.Application.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DynamicAI.Tests.Unit.Api;

public class ExceptionHandlingMiddlewareTests
{
    private sealed class RecordingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task InvokeAsync_InteractionRequiredException_ShouldReturn200AndLogInformationWithoutException()
    {
        var logger = new RecordingLogger();
        var queryLogId = Guid.NewGuid();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InteractionRequiredException("يرجى توضيح الفترة المطلوبة.", queryLogId, 0.42f),
            logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/AIQuery/integration/directed-page-ask";
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        httpContext.Response.ContentType.Should().Be("application/problem+json");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Information);
        logger.Entries[0].Exception.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_UnhandledException_ShouldReturn500AndLogErrorWithException()
    {
        var logger = new RecordingLogger();
        var expectedException = new InvalidOperationException("Unexpected database failure");
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw expectedException,
            logger);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/AIQuery/integration/directed-page-ask";
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        httpContext.Response.ContentType.Should().Be("application/problem+json");
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Error);
        logger.Entries[0].Exception.Should().BeSameAs(expectedException);
    }
}
