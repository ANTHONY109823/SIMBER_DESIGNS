# ==========================================
# SIMBER DESIGNS - DOCKERFILE MULTI-ETAPA
# ASP.NET Core 9 + Blazor WASM + ONNX Runtime
# ==========================================

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /app

# Node para compilar Tailwind (evita CDN lento en runtime)
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

COPY SimberDesigns.sln ./
COPY SimberDesigns.Server/SimberDesigns.Server.csproj ./SimberDesigns.Server/
COPY SimberDesigns.Client/SimberDesigns.Client.csproj ./SimberDesigns.Client/
COPY SimberDesigns.Tests/SimberDesigns.Tests.csproj ./SimberDesigns.Tests/
RUN dotnet restore

COPY . ./
WORKDIR /app/SimberDesigns.Client/wwwroot
RUN npm install && npm run build:css
WORKDIR /app/SimberDesigns.Server
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build-env /app/out .
RUN mkdir -p /app/models /app/Models

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "SimberDesigns.Server.dll"]
