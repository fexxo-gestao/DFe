FROM debian:bookworm AS acbr
ARG ACBR_SVN_REVISION=48390
ARG FORTESREPORT_COMMIT=888e387faca5a691b246a6b493776b8435be59f3
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      fpc lazarus-ide-gtk2 lcl-nogui lcl-units lcl-gtk2 lazarus-src make binutils subversion git ca-certificates \
      libxml2-dev libxmlsec1-dev libssl-dev \
 && rm -rf /var/lib/apt/lists/* \
 && cp -r /usr/lib/lazarus/2.2.6 /opt/lazarus && make -C /opt/lazarus lazbuild >/dev/null \
 && ln -s /opt/lazarus/lazbuild /usr/local/bin/lazbuild
WORKDIR /acbr
RUN R=svn://svn.code.sf.net/p/acbr/code/trunk2 \
 && svn co -q -r "$ACBR_SVN_REVISION" "$R/Fontes" Fontes \
 && svn co -q -r "$ACBR_SVN_REVISION" "$R/Pacotes/Lazarus" Pacotes/Lazarus \
 && svn co -q -r "$ACBR_SVN_REVISION" "$R/Projetos/ACBrLib/Fontes/Comum" Projetos/ACBrLib/Fontes/Comum \
 && svn co -q -r "$ACBR_SVN_REVISION" "$R/Projetos/ACBrLib/Fontes/NFSe" Projetos/ACBrLib/Fontes/NFSe \
 && svn co -q -r "$ACBR_SVN_REVISION" "$R/Exemplos/ACBrDFe/Schemas/NFSe" Schemas/NFSe \
 && git init -q fortesreport-ce && git -C fortesreport-ce fetch -q --depth 1 https://github.com/fortesinformatica/fortesreport-ce.git "$FORTESREPORT_COMMIT" \
 && git -C fortesreport-ce checkout -q FETCH_HEAD
COPY fexxo-gateway/acbr/build.sh /acbr/build.sh
RUN /acbr/build.sh

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["source/.NET Standard/Unimake.Business.DFe/", "source/.NET Standard/Unimake.Business.DFe/"]
COPY fexxo-gateway/Fexxo.FiscalGateway/ fexxo-gateway/Fexxo.FiscalGateway/
RUN dotnet publish fexxo-gateway/Fexxo.FiscalGateway/Fexxo.FiscalGateway.csproj -c Release -o /app --nologo -v q

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      libxml2 libxmlsec1 libxmlsec1-openssl libcairo2 libpango-1.0-0 libpangocairo-1.0-0 libglib2.0-0 \
 && apt-get upgrade -y && rm -rf /var/lib/apt/lists/* \
 && cd /usr/lib/x86_64-linux-gnu \
 && ln -sf libxml2.so.2 libxml2.so && ln -sf libxmlsec1.so.1 libxmlsec1.so && ln -sf libxmlsec1-openssl.so.1 libxmlsec1-openssl.so \
 && ln -sf libssl.so.3 libssl.so && ln -sf libcrypto.so.3 libcrypto.so
COPY --from=acbr /acbr/Projetos/ACBrLib/Fontes/NFSe/bin/Linux/ST/libacbrnfse64.so /opt/acbr/libacbrnfse64.so
COPY --from=acbr /acbr/Schemas/NFSe /opt/acbr/Schemas/NFSe
COPY --from=acbr /acbr/Fontes/ACBrDFe/ACBrNFSeX/ACBrNFSeXServicos.ini /opt/acbr/ACBrNFSeXServicos.ini
COPY fexxo-gateway/acbr/openssl-legacy.cnf /opt/acbr/openssl-legacy.cnf
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimit=0x8000000 \
    LD_LIBRARY_PATH=/opt/acbr \
    OPENSSL_CONF=/opt/acbr/openssl-legacy.cnf \
    FISCAL_GATEWAY_URLS=http://127.0.0.1:8090
EXPOSE 8090
ENTRYPOINT ["dotnet", "Fexxo.FiscalGateway.dll"]
