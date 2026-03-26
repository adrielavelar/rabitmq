FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY rabbitmq.pkg.sln .
COPY rabbitmq.lib/rabbitmq.lib.csproj rabbitmq.lib/
COPY rabbitmq.consumption/rabbitmq.consumption.csproj rabbitmq.consumption/
RUN dotnet restore rabbitmq.consumption/rabbitmq.consumption.csproj

COPY rabbitmq.lib/ rabbitmq.lib/
COPY rabbitmq.consumption/ rabbitmq.consumption/
RUN dotnet publish rabbitmq.consumption/rabbitmq.consumption.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "rabbitmq.consumption.dll"]
