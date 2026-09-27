FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["source/.NET Standard/Unimake.Business.DFe/", "source/.NET Standard/Unimake.Business.DFe/"]
COPY fexxo-gateway/Fexxo.FiscalGateway/ fexxo-gateway/Fexxo.FiscalGateway/
RUN dotnet publish fexxo-gateway/Fexxo.FiscalGateway/Fexxo.FiscalGateway.csproj -c Release -o /app --nologo -v q

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimit=0x8000000 \
    FISCAL_GATEWAY_URLS=http://127.0.0.1:8090
EXPOSE 8090
ENTRYPOINT ["dotnet", "Fexxo.FiscalGateway.dll"]
