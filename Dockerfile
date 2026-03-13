FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ClaudeProxy.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app \
    /p:PublishReadyToRun=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5066
RUN mkdir -p /data
VOLUME ["/data"]
EXPOSE 5066
ENTRYPOINT ["dotnet", "ClaudeProxy.dll"]
