# Host-only container image. CLI commands now live in the separate MuxLlmProxy.Cli project.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
ARG APP_PORT=9000
EXPOSE ${APP_PORT}


FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MuxLlmProxy.Host/MuxLlmProxy.Host.csproj", "MuxLlmProxy.Host/"]
COPY ["MuxLlmProxy.Core/MuxLlmProxy.Core.csproj", "MuxLlmProxy.Core/"]
COPY ["MuxLlmProxy.Infrastructure/MuxLlmProxy.Infrastructure.csproj", "MuxLlmProxy.Infrastructure/"]
RUN dotnet restore "./MuxLlmProxy.Host/MuxLlmProxy.Host.csproj"
COPY . .
WORKDIR "/src/MuxLlmProxy.Host"
RUN dotnet build "./MuxLlmProxy.Host.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MuxLlmProxy.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MuxLlmProxy.Host.dll"]
