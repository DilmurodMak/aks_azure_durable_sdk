FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Approval.Contracts/Approval.Contracts.csproj src/Approval.Contracts/packages.lock.json src/Approval.Contracts/
COPY src/Approval.Worker/Approval.Worker.csproj src/Approval.Worker/packages.lock.json src/Approval.Worker/
RUN dotnet restore src/Approval.Worker/Approval.Worker.csproj --locked-mode --source "$NUGET_SOURCE"
COPY src/Approval.Contracts/ src/Approval.Contracts/
COPY src/Approval.Worker/ src/Approval.Worker/
RUN dotnet publish src/Approval.Worker/Approval.Worker.csproj \
    --configuration Release --no-restore --output /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.15
WORKDIR /app
COPY --from=build /publish/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Approval.Worker.dll"]
