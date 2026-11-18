# Image du site ChefOS.Vitrine. À construire depuis la racine du dépôt :
#   docker build -f src/ChefOS.Vitrine/Dockerfile -t chefos-vitrine .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Les fichiers de projet d'abord : la restauration des paquets reste en cache tant qu'ils ne changent pas.
COPY src/ChefOS.Core/ChefOS.Core.csproj src/ChefOS.Core/
COPY src/ChefOS.Infrastructure/ChefOS.Infrastructure.csproj src/ChefOS.Infrastructure/
COPY src/ChefOS.Vitrine/ChefOS.Vitrine.csproj src/ChefOS.Vitrine/
RUN dotnet restore src/ChefOS.Vitrine/ChefOS.Vitrine.csproj
COPY src/ src/
# Pas de --no-restore : la restauration ci-dessus a eu lieu sans les pages .razor, et le SDK n'y ajoute pas le paquet
# qui fournit _framework/blazor.web.js (sans lui, aucune page n'est interactive). Les paquets déjà en cache ne sont pas retéléchargés.
RUN dotnet publish src/ChefOS.Vitrine/ChefOS.Vitrine.csproj -c Release -o /app/publish \
    && test -f /app/publish/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# curl sert uniquement à la sonde de santé de la composition (l'image d'exécution n'en contient pas).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# L'image aspnet fournit déjà l'utilisateur non-root « app ».
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChefOS.Vitrine.dll"]
