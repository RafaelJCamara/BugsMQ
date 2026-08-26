FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/BugsMQ.Abstractions/BugsMQ.Abstractions.csproj src/BugsMQ.Abstractions/
COPY src/BugsMQ.Core/BugsMQ.Core.csproj src/BugsMQ.Core/
COPY src/BugsMQ.Observability/BugsMQ.Observability.csproj src/BugsMQ.Observability/
COPY src/BugsMQ.Persistence.EFCore/BugsMQ.Persistence.EFCore.csproj src/BugsMQ.Persistence.EFCore/
COPY src/BugsMQ.Persistence.EFCore.Postgres/BugsMQ.Persistence.EFCore.Postgres.csproj src/BugsMQ.Persistence.EFCore.Postgres/
COPY src/BugsMQ.Transport.Common/BugsMQ.Transport.Common.csproj src/BugsMQ.Transport.Common/
COPY src/BugsMQ.Transport.RabbitMQ/BugsMQ.Transport.RabbitMQ.csproj src/BugsMQ.Transport.RabbitMQ/
COPY src/BugsMQ.Dashboard.Api/BugsMQ.Dashboard.Api.csproj src/BugsMQ.Dashboard.Api/
RUN dotnet restore src/BugsMQ.Dashboard.Api/BugsMQ.Dashboard.Api.csproj

COPY src/ src/
RUN dotnet publish src/BugsMQ.Dashboard.Api/BugsMQ.Dashboard.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# curl backs docker-compose's HEALTHCHECK against /health — order-processing depends on this container
# reporting healthy (i.e. migrations applied) before it starts, to avoid both processes racing to
# create the schema (order-processing's own EnsureCreatedAsync bootstrap vs. this app's MigrateAsync).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "BugsMQ.Dashboard.Api.dll"]
