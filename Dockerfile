# Multi-stage Dockerfile for BusGo (.NET 10)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies first (layer caching)
COPY ["BusGo.csproj", "./"]
RUN dotnet restore "BusGo.csproj"

# Copy full source and publish
COPY . .
RUN dotnet publish "BusGo.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install fontconfig and basic font utilities required by SkiaSharp / QuestPDF on Linux
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Copy published application
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "BusGo.dll"]
