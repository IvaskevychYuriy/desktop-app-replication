# Build context is src/:  docker build -f Sample.Owner/Dockerfile -t replication-owner .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY Replication.Core/Replication.Core.csproj Replication.Core/
COPY Sample.Domain/Sample.Domain.csproj Sample.Domain/
COPY Sample.Owner/Sample.Owner.csproj Sample.Owner/
RUN dotnet restore Sample.Owner/Sample.Owner.csproj

COPY Replication.Core Replication.Core
COPY Sample.Domain Sample.Domain
COPY Sample.Owner Sample.Owner
RUN dotnet publish Sample.Owner/Sample.Owner.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV Owner__ConnectionString="Data Source=/data/owner.db" \
    Kestrel__Endpoints__Grpc__Url=http://0.0.0.0:5005
VOLUME /data
EXPOSE 5005
ENTRYPOINT ["dotnet", "ReplicationOwner.dll"]
