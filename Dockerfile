# ============================================================================
# DynamicAI - Multi-Stage Dockerfile
# Combines Angular Frontend + .NET 8 Backend in a single image
# ============================================================================

# Stage 1: Build Angular Frontend
# ----------------------------------------------------------------------------
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend

# Copy package files first for better layer caching
COPY DynamicAi.UI/package*.json ./
RUN npm ci --legacy-peer-deps

# Copy Angular source and build
COPY DynamicAi.UI/ .
RUN npm run build -- --configuration production

# Stage 2: Build .NET Backend
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY DynamicAi.sln .
COPY DynamicAi/DynamicAi.Api.csproj ./DynamicAi/
COPY DynamicAi.Application/DynamicAi.Application.csproj ./DynamicAi.Application/
COPY Domain/DynamicAi.Domain.csproj ./Domain/
COPY Infrastructure/DynamicAi.Infrastructure.csproj ./Infrastructure/

# Restore dependencies
RUN dotnet restore DynamicAi/DynamicAi.Api.csproj

# Copy all source files
COPY DynamicAi/ ./DynamicAi/
COPY DynamicAi.Application/ ./DynamicAi.Application/
COPY Domain/ ./Domain/
COPY Infrastructure/ ./Infrastructure/

# Publish the application
WORKDIR /src/DynamicAi
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

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl --fail http://localhost:8080/health || exit 1

# Entry point
ENTRYPOINT ["dotnet", "DynamicAi.Api.dll"]
