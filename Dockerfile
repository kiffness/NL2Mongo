FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5100

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/NL2Mongo.Api/NL2Mongo.Api.csproj", "src/NL2Mongo.Api/"]
RUN dotnet restore "src/NL2Mongo.Api/NL2Mongo.Api.csproj"

COPY . .
WORKDIR "/src/src/NL2Mongo.Api"

FROM build AS publish
RUN dotnet publish "NL2Mongo.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NL2Mongo.Api.dll"]