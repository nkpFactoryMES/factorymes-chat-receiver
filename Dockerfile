FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY .packages/ ./.packages/
COPY simulators/chat-receiver/*.csproj simulators/chat-receiver/packages.lock.json ./simulators/chat-receiver/
RUN dotnet restore simulators/chat-receiver/FactoryMES.ChatReceiver.csproj --locked-mode
COPY simulators/chat-receiver/ ./simulators/chat-receiver/
RUN dotnet publish simulators/chat-receiver/FactoryMES.ChatReceiver.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
RUN mkdir -p /var/factorymes/keys /var/factorymes/chat-captures \
    && chown -R app:app /var/factorymes
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app
ENTRYPOINT ["dotnet", "FactoryMES.ChatReceiver.dll"]
