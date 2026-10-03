FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /fixture
COPY . .
RUN dotnet publish NotifyFixture.csproj -c Release -o /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f
WORKDIR /app
COPY --from=build /publish .
COPY start-consumer.sh .
RUN mkdir /fixtures && chown app:app /fixtures
USER app
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "NotifyFixture.dll"]
