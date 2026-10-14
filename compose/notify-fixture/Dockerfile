FROM mcr.microsoft.com/dotnet/sdk@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /fixture
COPY . .
RUN dotnet publish NotifyFixture.csproj -c Release -o /publish
FROM mcr.microsoft.com/dotnet/aspnet@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
WORKDIR /app
COPY --from=build /publish .
COPY start-consumer.sh .
RUN mkdir /fixtures && chown app:app /fixtures
USER app
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "NotifyFixture.dll"]
