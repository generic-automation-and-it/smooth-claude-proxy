FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY ClaudeProxy.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app \
    /p:PublishReadyToRun=true \
    /p:PublishTrimmed=true \
    /p:TrimMode=partial

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5000
RUN mkdir -p /data
VOLUME ["/data"]
EXPOSE 5000
ENTRYPOINT ["dotnet", "ClaudeProxy.dll"]
