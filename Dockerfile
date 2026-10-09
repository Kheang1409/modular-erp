FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore Erp.slnx && dotnet publish src/Platform/Erp.Host/Erp.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
COPY modules/external /app/modules/external
RUN mkdir -p /var/erp/keys /var/erp/files /var/erp/modules /var/erp/trusted-publishers && chown -R $APP_UID:$APP_UID /var/erp
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Erp.Host.dll"]
