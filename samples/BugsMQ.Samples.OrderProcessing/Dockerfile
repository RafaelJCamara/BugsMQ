FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/BugsMQ.Abstractions/BugsMQ.Abstractions.csproj src/BugsMQ.Abstractions/
COPY src/BugsMQ.Chaos/BugsMQ.Chaos.csproj src/BugsMQ.Chaos/
COPY src/BugsMQ.Core/BugsMQ.Core.csproj src/BugsMQ.Core/
COPY src/BugsMQ.Observability/BugsMQ.Observability.csproj src/BugsMQ.Observability/
COPY src/BugsMQ.Persistence.EFCore/BugsMQ.Persistence.EFCore.csproj src/BugsMQ.Persistence.EFCore/
COPY src/BugsMQ.Transport.Common/BugsMQ.Transport.Common.csproj src/BugsMQ.Transport.Common/
COPY src/BugsMQ.Transport.RabbitMQ/BugsMQ.Transport.RabbitMQ.csproj src/BugsMQ.Transport.RabbitMQ/
COPY src/BugsMQ.Transport.Wolverine/BugsMQ.Transport.Wolverine.csproj src/BugsMQ.Transport.Wolverine/
COPY src/BugsMQ.Transport.MassTransit/BugsMQ.Transport.MassTransit.csproj src/BugsMQ.Transport.MassTransit/
COPY src/BugsMQ.Transport.Brighter/BugsMQ.Transport.Brighter.csproj src/BugsMQ.Transport.Brighter/
COPY samples/BugsMQ.Samples.OrderProcessing.Contracts/BugsMQ.Samples.OrderProcessing.Contracts.csproj samples/BugsMQ.Samples.OrderProcessing.Contracts/
COPY samples/BugsMQ.Samples.OrderProcessing/BugsMQ.Samples.OrderProcessing.csproj samples/BugsMQ.Samples.OrderProcessing/
RUN dotnet restore samples/BugsMQ.Samples.OrderProcessing/BugsMQ.Samples.OrderProcessing.csproj

COPY src/ src/
COPY samples/ samples/
RUN dotnet publish samples/BugsMQ.Samples.OrderProcessing/BugsMQ.Samples.OrderProcessing.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "BugsMQ.Samples.OrderProcessing.dll"]
