# ============================================================================
# DynamicAI - Multi-Stage Dockerfile
# Combines Angular Frontend + .NET 8 Backend in a single image
# ============================================================================

# Stage 1: Build Angular Frontend
# ----------------------------------------------------------------------------
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend

# Copy package files first for better layer caching
COPY frontend/DynamicAi.UI/package*.json ./
RUN npm ci --legacy-peer-deps

# Copy Angular source and build
COPY frontend/DynamicAi.UI/ .
RUN npm run build -- --configuration production

# Stage 2: Build .NET Backend
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY DynamicAi.sln .
COPY src/DynamicAi.Api/DynamicAi.Api.csproj ./src/DynamicAi.Api/
COPY src/DynamicAi.Application/DynamicAi.Application.csproj ./src/DynamicAi.Application/
COPY src/DynamicAi.Domain/DynamicAi.Domain.csproj ./src/DynamicAi.Domain/
COPY src/DynamicAi.Infrastructure/DynamicAi.Infrastructure.csproj ./src/DynamicAi.Infrastructure/

# Restore dependencies
RUN dotnet restore src/DynamicAi.Api/DynamicAi.Api.csproj

# Copy all source files
COPY src/DynamicAi.Api/ ./src/DynamicAi.Api/
COPY src/DynamicAi.Application/ ./src/DynamicAi.Application/
COPY src/DynamicAi.Domain/ ./src/DynamicAi.Domain/
COPY src/DynamicAi.Infrastructure/ ./src/DynamicAi.Infrastructure/

# Publish the application
WORKDIR /src/src/DynamicAi.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 3: Final Runtime Image
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Create non-root user for security
RUN adduser --disabled-password --gecos "" appuser

# Copy published .NET application
COPY --from=backend-build /app/publish .

# Copy Angular static files to wwwroot
# Note: Angular 17+ outputs to dist/<project-name>/browser
COPY --from=frontend-build /app/frontend/dist/DynamicAi.UI/browser ./wwwroot

# Set ownership to non-root user
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose port (Kestrel default for non-root)
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Health check using bash /dev/tcp against /aiquery/ping (curl is not installed in aspnet:8.0)
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /aiquery/ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3 && grep -q "200 OK" <&3' || exit 1

# Entry point
ENTRYPOINT ["dotnet", "DynamicAi.Api.dll"]
