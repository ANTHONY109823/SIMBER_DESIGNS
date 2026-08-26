# ==========================================
# SIMBER DESIGNS - DOCKERFILE MULTI-ETAPA
# Optimizado para ASP.NET Core, Blazor y ONNX
# ==========================================

# Etapa 1: Compilación y Publicación
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Copiar archivos de solución y restaurar dependencias NuGet
# Se asume una estructura estándar donde el Web API/Blazor está en la carpeta 'SimberDesigns.WebApi'
COPY *.sln ./
COPY SimberDesigns.Core/*.csproj ./SimberDesigns.Core/
COPY SimberDesigns.Infrastructure/*.csproj ./SimberDesigns.Infrastructure/
COPY SimberDesigns.WebApi/*.csproj ./SimberDesigns.WebApi/
RUN dotnet restore

# Copiar todo el código fuente restante e iniciar compilación
COPY . ./
WORKDIR /app/SimberDesigns.WebApi
RUN dotnet publish -c Release -o /app/out

# Etapa 2: Entorno de Ejecución (Runtime)
# Usamos la imagen oficial de ASP.NET Core que está optimizada para producción
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Instalar dependencias nativas mínimas necesarias para ONNX Runtime en Linux
# El paquete NuGet de ONNX Runtime para C# incluye binarios nativos .so, pero requieren libc6 y libstdc++
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Copiar la aplicación compilada desde la etapa de compilación
COPY --from=build-env /app/out .

# Crear un directorio seguro para almacenar el modelo ONNX de CLIP de forma local
# Este directorio no debe estar sincronizado en Git (excluido en .gitignore)
RUN mkdir -p /app/models

# Exponer el puerto por defecto de ASP.NET Core 8.0+ (8080)
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Comando de inicio seguro
ENTRYPOINT ["dotnet", "SimberDesigns.WebApi.dll"]
