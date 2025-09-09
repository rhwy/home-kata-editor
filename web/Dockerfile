FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ./ .
RUN dotnet restore ./WebRunner.csproj
RUN dotnet publish ./WebRunner.csproj -c Release -o /out

FROM base AS final
WORKDIR /app
COPY --from=build /out ./
ENTRYPOINT ["dotnet", "WebRunner.dll"]
