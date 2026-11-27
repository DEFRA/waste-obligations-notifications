FROM mcr.microsoft.com/dotnet/aspnet@sha256:ca396ece3f2a2e62c7a03f1d7d710b8bc7e8c9dea5597d091178d9f5bfca13f0 AS base
WORKDIR /app

RUN apt update && \
    apt install curl -y && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src

COPY .config/dotnet-tools.json .config/dotnet-tools.json
COPY .csharpierrc .csharpierrc
COPY .editorconfig .editorconfig
COPY Directory.Build.props Directory.Build.props
COPY global.json global.json
COPY src/Consumer/Consumer.csproj src/Consumer/Consumer.csproj
COPY tests/Consumer.IntegrationTests/Consumer.IntegrationTests.csproj tests/Consumer.IntegrationTests/Consumer.IntegrationTests.csproj
COPY tests/Consumer.Tests/Consumer.Tests.csproj tests/Consumer.Tests/Consumer.Tests.csproj
COPY tests/Consumer.MigrationFixtures/Consumer.MigrationFixtures.csproj tests/Consumer.MigrationFixtures/Consumer.MigrationFixtures.csproj
COPY waste-obligations-notifications.slnx waste-obligations-notifications.slnx

RUN dotnet tool restore
RUN dotnet restore

COPY src/Consumer src/Consumer
COPY tests/Consumer.IntegrationTests tests/Consumer.IntegrationTests
COPY tests/Consumer.Tests tests/Consumer.Tests
COPY tests/Consumer.MigrationFixtures tests/Consumer.MigrationFixtures

RUN dotnet csharpier check .
RUN dotnet build --no-restore --warnaserror
RUN dotnet test --test-modules tests/Consumer.Tests/bin/Debug/net10.0/Consumer.Tests.dll --no-build
RUN dotnet publish src/Consumer -c Release --no-restore --warnaserror -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app

COPY --from=build /app/publish .

USER app

EXPOSE 8085
ENTRYPOINT ["dotnet", "Consumer.dll"]
